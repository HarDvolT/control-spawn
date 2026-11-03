using Prism.Core;

namespace Prism.Service;

public sealed record ModuleHealth(string Name, bool Running, int Failures);
public interface IPrismModule
{
    ModuleHealth Health { get; }
    Task Completion { get; }
    void Start(CancellationToken token, bool background = true);
    Task StopAsync();
    Task RunAsync(CancellationToken token);
}

/// <summary>Each module owns its retry boundary. A failure never cancels sibling modules.</summary>
public abstract class HostedModule(string name, Action<bool> health, Func<TimeSpan, CancellationToken, Task>? delay = null) : IPrismModule
{
    private readonly Func<TimeSpan, CancellationToken, Task> delay = delay ?? Task.Delay;
    private CancellationTokenSource? lifetime;
    public Task Completion { get; private set; } = Task.CompletedTask;
    public ModuleHealth Health { get; private set; } = new(name, false, 0);
    public void Start(CancellationToken token, bool background = true)
    {
        if (lifetime is not null) throw new InvalidOperationException("Module already started.");
        lifetime = CancellationTokenSource.CreateLinkedTokenSource(token);
        Completion = background ? Task.Run(() => RunAsync(lifetime.Token), CancellationToken.None) : RunAsync(lifetime.Token);
    }
    public async Task StopAsync()
    {
        if (lifetime is null) return;
        lifetime.Cancel();
        try { await Completion; }
        finally { lifetime.Dispose(); lifetime = null; }
    }
    protected abstract Task ExecuteAsync(CancellationToken token);
    public async Task RunAsync(CancellationToken token)
    {
        int attempts = 0;
        while (!token.IsCancellationRequested)
        {
            try
            {
                Health = Health with { Running = true }; health(true);
                await ExecuteAsync(token);
                if (!token.IsCancellationRequested) throw new InvalidOperationException("Module ended unexpectedly.");
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { break; }
            catch (Exception e)
            {
                Health = Health with { Running = false, Failures = Health.Failures + 1 }; health(false);
                PrismLog.Failure(name, e);
                try { await delay(TimeSpan.FromSeconds(Math.Min(30, Math.Pow(2, Math.Min(attempts++, 5)))), token); }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { break; }
            }
        }
        Health = Health with { Running = false };
    }
}

/// <summary>One state assembly point with immediate command-driven notifications.</summary>
public sealed class StateHub(Func<Prism.Core.Ipc.StateMessage> assemble)
{
    public event Action? Changed;
    public Prism.Core.Ipc.StateMessage Read() => assemble();
    public void Publish() => Changed?.Invoke();
}
