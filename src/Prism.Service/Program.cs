using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Prism.Service;

public static class Program
{
    public static async Task Main(string[] args)
    {
        bool mock = args.Contains("--mock", StringComparer.OrdinalIgnoreCase);
        bool observe = args.Contains("--observe", StringComparer.OrdinalIgnoreCase);
        if (args.Contains("--no-gpu-sensors", StringComparer.OrdinalIgnoreCase)) Environment.SetEnvironmentVariable("PRISM_NO_GPU_SENSORS", "1");
        bool console = mock || observe || args.Contains("--console", StringComparer.OrdinalIgnoreCase) || Environment.UserInteractive;
        // Don't pass the standalone flags to the host's key/value command-line parser.
        var builder = Host.CreateDefaultBuilder([]);
        if (!console) builder.UseWindowsService();
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
}

public sealed class PrismWorker(ServiceRuntime runtime, CommandProcessor commands) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        await runtime.ApplyAsync(commands.Settings, linked.Token);
        var runtimeTask = runtime.RunAsync(linked.Token);
        var pipeTask = new PipeServer(commands).RunAsync(linked.Token);
        var automationTask = commands.RunAutomationsAsync(linked.Token);
        try { await await Task.WhenAny(runtimeTask, pipeTask); }
        catch (Exception) when (!stoppingToken.IsCancellationRequested)
        { Environment.ExitCode = 1; throw; }
        finally
        {
            linked.Cancel(); runtime.RestoreDefaults();
            try { await Task.WhenAll(runtimeTask, pipeTask, automationTask); } catch (OperationCanceledException) when (linked.IsCancellationRequested) { }
        }
    }
    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        runtime.RestoreDefaults();
        try { await base.StopAsync(cancellationToken); }
        finally { runtime.RestoreDefaults(); }
    }
}
