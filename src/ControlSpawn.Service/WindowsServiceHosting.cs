using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using System.Diagnostics;
using System.ServiceProcess;
using ControlSpawn.Core;

namespace ControlSpawn.Service;

/// <summary>Small SCM lifetime adapter using ServiceBase and the installed generic host.
/// Avoids an additional WindowsServices NuGet dependency; never invoked in console/mock mode.</summary>
public static class WindowsServiceHosting
{
    public static IHostBuilder UseWindowsService(this IHostBuilder builder) =>
        builder.ConfigureServices(services => services.AddSingleton<IHostLifetime, ScmLifetime>());
}

internal sealed class ScmLifetime : ServiceBase, IHostLifetime
{
    private readonly IHostApplicationLifetime lifetime;
    private readonly TaskCompletionSource started = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource stopped = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Thread? dispatcher;
    private readonly ManualResetEventSlim applicationStopped = new(false);
    public ScmLifetime(IHostApplicationLifetime lifetime)
    {
        this.lifetime = lifetime; ServiceName = ProductNames.ServiceKey; CanStop = true; CanShutdown = true; AutoLog = false;
        lifetime.ApplicationStopped.Register(() => applicationStopped.Set());
    }
    public Task WaitForStartAsync(CancellationToken cancellationToken)
    {
        dispatcher = new Thread(() =>
        {
            try { Run(this); }
            catch (Exception e) { started.TrySetException(e); lifetime.StopApplication(); }
            finally { stopped.TrySetResult(); }
        }) { IsBackground = true, Name = "Control Spawn SCM dispatcher" };
        dispatcher.Start();
        return started.Task.WaitAsync(cancellationToken);
    }
    protected override void OnStart(string[] args) => started.TrySetResult();
    protected override void OnStop()
    {
        var elapsed = Stopwatch.StartNew();
        var budget = TimeSpan.FromSeconds(25);
        var interval = TimeSpan.FromSeconds(1);
        lifetime.StopApplication();
        // Keep SCM in stop-pending until the host has restored fans and drained workers.
        while (budget - elapsed.Elapsed is { TotalMilliseconds: > 0 } remaining)
        {
            if (applicationStopped.Wait(remaining < interval ? remaining : interval)) return;
            int millisecondsLeft = (int)(budget - elapsed.Elapsed).TotalMilliseconds;
            if (millisecondsLeft > 0) RequestAdditionalTime(Math.Min(5000, millisecondsLeft));
        }
    }
    // Windows doesn't put the service in stop-pending for a shutdown, so RequestAdditionalTime would throw and crash us here.
    protected override void OnShutdown()
    {
        lifetime.StopApplication();
        applicationStopped.Wait(TimeSpan.FromSeconds(10));
    }
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        // OnStop only signals cancellation. Host cleanup finishes before reporting stopped to SCM.
        ExitCode = Environment.ExitCode;
        Stop();
        if (dispatcher is not null) await stopped.Task.WaitAsync(cancellationToken);
    }
}
