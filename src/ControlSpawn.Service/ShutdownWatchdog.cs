namespace ControlSpawn.Service;

internal sealed class ShutdownWatchdog(Func<Task, TimeSpan, bool>? waitForStop = null, Action<int>? exit = null) : IDisposable
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(20);
    private readonly TaskCompletionSource completed = new();
    private readonly Func<Task, TimeSpan, bool> waitForStop = waitForStop ?? ((completion, timeout) => completion.Wait(timeout));
    private readonly Action<int> exit = exit ?? Environment.Exit;
    private int started;

    public Thread? Start(Action restoreDefaultsForForcedExit)
    {
        if (Interlocked.Exchange(ref started, 1) != 0) return null;
        var watchdog = new Thread(() => Watch(restoreDefaultsForForcedExit))
        { IsBackground = true, Name = "Control Spawn shutdown watchdog" };
        watchdog.Start();
        return watchdog;
    }

    private void Watch(Action restoreDefaultsForForcedExit)
    {
        if (waitForStop(completed.Task, Deadline) || completed.Task.IsCompleted) return;
        try { restoreDefaultsForForcedExit(); }
        finally { if (!completed.Task.IsCompleted) exit(1); }
    }

    public void Dispose() => completed.TrySetResult();
}
