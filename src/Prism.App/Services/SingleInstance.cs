namespace Prism.App.Services;
/// <summary>Session-local activation, independent of the service pipe.</summary>
public sealed class SingleInstance : IDisposable
{
    private readonly Mutex _mutex;
    private readonly EventWaitHandle _activate;
    private readonly RegisteredWaitHandle? _wait;
    public bool IsPrimary { get; }
    public SingleInstance(string name, Action activated)
    {
        _mutex = new Mutex(true, @"Local\" + name, out var created);
        IsPrimary = created;
        _activate = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\" + name + ".activate");
        if (created) _wait = ThreadPool.RegisterWaitForSingleObject(_activate, (_, _) => activated(), null, Timeout.Infinite, false);
        else _activate.Set();
    }
    public void Dispose()
    {
        _wait?.Unregister(null);
        _activate.Dispose();
        if (IsPrimary) _mutex.ReleaseMutex();
        _mutex.Dispose();
    }
}
