namespace ControlSpawn.Hardware;

/// <summary>
/// Names the hardware a sensor read is stuck on. The fan guard hands fans to the BIOS after 3 s with no fresh sensor
/// cycle and ends the service, so a read that never returned left no trace of which device hung (2026-11-21).
/// This only records and reports; it never changes what the fans or the guard do.
/// </summary>
public sealed class SensorStallWatch(Func<TimeSpan>? now = null)
{
    public static readonly TimeSpan SlowRead = TimeSpan.FromSeconds(1);
    public static readonly TimeSpan StuckRead = TimeSpan.FromMilliseconds(1500);
    private readonly Func<TimeSpan> now = now ?? System.Diagnostics.Stopwatch.StartNew().ElapsedOf();
    private readonly object gate = new();
    private string? hardware;
    private TimeSpan started;
    private bool reported;

    public void Begin(string name) { lock (gate) { hardware = name; started = now(); reported = false; } }

    /// <summary>Returns a sentence when the read that just ended was slow, else null.</summary>
    public string? End()
    {
        lock (gate)
        {
            if (hardware is null) return null;
            var took = now() - started; var name = hardware; hardware = null;
            return took >= SlowRead ? $"{name} sensor read took {took.TotalMilliseconds:0} ms." : null;
        }
    }

    /// <summary>Returns a sentence once per read that has run past <see cref="StuckRead"/> and is still running.</summary>
    public string? Check()
    {
        lock (gate)
        {
            var took = now() - started;
            if (hardware is null || reported || took < StuckRead) return null;
            reported = true;
            return $"{hardware} sensor read has been stuck for {took.TotalMilliseconds:0} ms; fans go to BIOS control at 3 s.";
        }
    }
}

internal static class StopwatchElapsed
{
    public static Func<TimeSpan> ElapsedOf(this System.Diagnostics.Stopwatch clock) => () => clock.Elapsed;
}
