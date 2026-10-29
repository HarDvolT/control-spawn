using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using System.ServiceProcess;

namespace Prism.Service;

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
        this.lifetime = lifetime; ServiceName = "Prism"; CanStop = true; CanShutdown = true; AutoLog = false;
        lifetime.ApplicationStopped.Register(() => applicationStopped.Set());
    }
    public Task WaitForStartAsync(CancellationToken cancellationToken)
    {
        dispatcher = new Thread(() =>
        {
            try { Run(this); }
            catch (Exception e) { started.TrySetException(e); lifetime.StopApplication(); }
            finally { stopped.TrySetResult(); }
        }) { IsBackground = true, Name = "Prism SCM dispatcher" };
        dispatcher.Start();
        return started.Task.WaitAsync(cancellationToken);
    }
    protected override void OnStart(string[] args) => started.TrySetResult();
    protected override void OnStop()
    {
        lifetime.StopApplication();
        // Keep SCM in stop-pending until the host has restored fans and drained workers.
        while (!applicationStopped.Wait(TimeSpan.FromSeconds(1))) RequestAdditionalTime(5000);
    }
    protected override void OnShutdown() => OnStop();
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        // OnStop only signals cancellation. Host cleanup finishes before reporting stopped to SCM.
        Stop();
        if (dispatcher is not null) await stopped.Task.WaitAsync(cancellationToken);
    }
}
