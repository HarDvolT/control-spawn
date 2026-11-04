namespace Prism.Hardware;

/// <summary>Monotonic, fakeable watchdog. Only successful sensor/fan cycles renew its lease.</summary>
public sealed class FanHeartbeat(Action restore, TimeSpan started)
{
    private TimeSpan last = started;
    private bool expired;
    public void Beat(TimeSpan now) { if (!expired) last = now; }
    public bool Check(TimeSpan now)
    {
        if (expired || now - last < TimeSpan.FromSeconds(3)) return expired;
        expired = true; restore(); return true;
    }
}
