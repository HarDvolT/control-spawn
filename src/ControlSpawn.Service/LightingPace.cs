namespace ControlSpawn.Service;

/// <summary>
/// How fast the lighting loop ticks. A static colour, lights off or a paused scene renders the same frame 60 times a
/// second and holds the 1 ms timer for nothing, so after <see cref="StillAfter"/> of unchanged frames the loop drops to
/// <see cref="IdleTick"/>. Any changed frame, or a <see cref="Wake"/> (new settings, an event flash, Identify), goes
/// straight back to 60 fps without waiting for the idle tick.
/// </summary>
public sealed class LightingPace
{
    public static readonly TimeSpan StillAfter = TimeSpan.FromSeconds(3), IdleTick = TimeSpan.FromMilliseconds(100);
    private readonly SemaphoreSlim wake = new(0, 1);
    private TimeSpan? stillSince;
    private volatile bool woken;
    private TimeSpan? lastTick; private TimeSpan idleTime;
    /// <summary>Time spent idle since the last call, for the 10 s frame stats: a yes/no flag hid a loop woken every few seconds.</summary>
    public TimeSpan TakeIdleTime() { var spent = idleTime; idleTime = TimeSpan.Zero; return spent; }
    /// <summary>Only the lighting loop reads this and calls <see cref="Tick"/>; <see cref="Wake"/> may come from any thread.</summary>
    public bool Idle { get; private set; }

    /// <summary>Called once per tick: <paramref name="changed"/> if any device got a frame unlike the last one it shows.</summary>
    public void Tick(bool changed, TimeSpan now)
    {
        if (Idle && lastTick is { } last) idleTime += now - last;
        lastTick = now;
        if (changed || woken) { woken = false; stillSince = null; Idle = false; return; }
        stillSince ??= now;
        Idle = now - stillSince.Value >= StillAfter;
    }

    /// <summary>Back to full speed from the next tick; an idle wait in progress ends now.</summary>
    public void Wake()
    {
        woken = true;
        try { if (wake.CurrentCount == 0) wake.Release(); } catch (SemaphoreFullException) { }
    }

    /// <summary>The idle wait: up to <see cref="IdleTick"/>, or less when woken.</summary>
    public async Task WaitIdleAsync(CancellationToken token)
    {
        await wake.WaitAsync(IdleTick, token);
    }
}
