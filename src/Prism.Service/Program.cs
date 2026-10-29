using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Prism.Core;

namespace Prism.Service;

public static class Program
{
    public static async Task Main(string[] args)
    {
        if (args.Length == 2 && args[0] == "--fan-guard" && int.TryParse(args[1], out var parentId))
        {
            using var guardLogs = new RollingLogProvider();
            try { await FanGuardProcess.RunGuardAsync(parentId, guardLogs.CreateLogger("fans")); }
            catch (Exception e) { guardLogs.CreateLogger("fans").LogError("Fan guard failed ({ErrorType})", e.GetType().Name); throw; }
            return;
        }
        if (args.Contains("--migrate-secrets", StringComparer.OrdinalIgnoreCase))
        {
            using var logs = LoggerFactory.Create(logging => logging.AddProvider(new RollingLogProvider()));
            ConnectLibraryLogging(logs);
            Environment.ExitCode = SecretMigration.Run(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), new SecretStore()) ? 0 : 1;
            return;
        }
        bool mock = args.Contains("--mock", StringComparer.OrdinalIgnoreCase);
        bool observe = args.Contains("--observe", StringComparer.OrdinalIgnoreCase);
        if (args.Contains("--no-gpu-sensors", StringComparer.OrdinalIgnoreCase)) Environment.SetEnvironmentVariable("PRISM_NO_GPU_SENSORS", "1");
        bool console = mock || observe || args.Contains("--console", StringComparer.OrdinalIgnoreCase) || Environment.UserInteractive;
        // Don't pass the standalone flags to the host's key/value command-line parser.
        var builder = Host.CreateDefaultBuilder([]);
        if (!console) builder.UseWindowsService();
        builder.ConfigureLogging(logging => { logging.ClearProviders(); logging.AddProvider(new RollingLogProvider(mock ? Path.Combine(AppContext.BaseDirectory, "mock-logs") : null)); });
        await using var runtime = new ServiceRuntime(mock, observe);
        builder.ConfigureServices(services =>
        {
            services.AddSingleton(runtime);
            services.AddSingleton<ISettingsStore>(mock ? new MemorySettingsStore() : new JsonSettingsStore());
            services.AddSingleton<IServiceRuntime>(s => s.GetRequiredService<ServiceRuntime>());
            services.AddSingleton<CommandProcessor>();
            services.AddHostedService<PrismWorker>();
        });
        using var host = builder.Build();
        ConnectLibraryLogging(host.Services.GetRequiredService<ILoggerFactory>());
        if (console && !mock && !observe && !SecretMigration.Run(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), new SecretStore()))
            runtime.ReportSecretMigrationFailure();
        // --exit-after <seconds>: clean, timed shutdown for supervised tests, so the normal stop path
        // (StopAsync -> RestoreDefaults) always hands the fans back to the BIOS.
        int exitIndex = Array.FindIndex(args, a => a.Equals("--exit-after", StringComparison.OrdinalIgnoreCase));
        if (exitIndex >= 0 && exitIndex + 1 < args.Length && int.TryParse(args[exitIndex + 1], out var seconds) && seconds > 0)
        {
            var lifetime = host.Services.GetRequiredService<IHostApplicationLifetime>();
            _ = Task.Delay(TimeSpan.FromSeconds(seconds)).ContinueWith(_ => lifetime.StopApplication(), TaskScheduler.Default);
        }
        await host.RunAsync();
    }

    private static void ConnectLibraryLogging(ILoggerFactory factory) => PrismLog.ErrorSink = (module, errorType) =>
    {
        var logger = factory.CreateLogger(module);
        using var scope = logger.BeginScope("{Module}", module);
        logger.LogError("Operation failed ({ErrorType})", errorType);
    };
}

public sealed class PrismWorker(ServiceRuntime runtime, CommandProcessor commands) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        await runtime.ApplyAsync(commands.Settings, linked.Token);
        var runtimeTask = runtime.RunAsync(linked.Token);
        var pipeTask = new PipeServer(commands).RunAsync(linked.Token);
        try { await await Task.WhenAny(runtimeTask, pipeTask); }
        catch (Exception) when (!stoppingToken.IsCancellationRequested)
        { Environment.ExitCode = 1; throw; }
        finally
        {
            linked.Cancel(); runtime.RestoreDefaults();
            try { await Task.WhenAll(runtimeTask, pipeTask); } catch (OperationCanceledException) when (linked.IsCancellationRequested) { }
        }
    }
    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        runtime.RestoreDefaults();
        try { await base.StopAsync(cancellationToken); }
        finally { runtime.RestoreDefaults(); }
    }
}
