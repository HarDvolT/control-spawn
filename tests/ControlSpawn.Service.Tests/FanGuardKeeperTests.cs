using ControlSpawn.Service;
using Xunit;

namespace ControlSpawn.Service.Tests;

// These block thread-pool threads on purpose (a guard that is slow to start). Run them alone so they cannot
// starve the timing-sensitive tests on a small CI runner.
[Collection(nameof(ServiceLoopTests))]
public sealed class FanGuardKeeperTests
{
    private sealed class Guard : IFanGuard
    {
        public bool HasExited { get; set; }
        public bool Stalled { get; set; }
        public bool Disposed { get; private set; }
        public int Beats { get; private set; }
        public void Beat() { if (HasExited) throw new IOException("Fan guard exited."); Beats++; }
        public void Dispose() => Disposed = true;
    }

    private sealed class Clock : TimeProvider
    {
        public TimeSpan Now { get; set; }
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => Now.Ticks;
    }

    private static bool EnsureSoon(FanGuardKeeper keeper)
    {
        for (int i = 0; i < 200; i++) { if (keeper.Ensure()) return true; Thread.Sleep(10); }
        return false;
    }

    [Fact]
    public void EnsureReturnsAtOnceWhileTheGuardIsStillStarting()
    {
        using var release = new ManualResetEventSlim();
        var guard = new Guard();
        using var keeper = new FanGuardKeeper(() => { release.Wait(); return guard; });
        var watch = System.Diagnostics.Stopwatch.StartNew();
        Assert.False(keeper.Ensure());
        Assert.False(keeper.Ensure());
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(1));   // the fan loop never waits for a start
        keeper.Beat();                                           // no guard yet: nothing to beat, nothing thrown
        release.Set();
        Assert.True(EnsureSoon(keeper));
        keeper.Beat(); Assert.Equal(1, guard.Beats);
    }

    [Fact]
    public void DeadGuardIsReplacedWithoutBlocking()
    {
        var started = new List<Guard>();
        using var keeper = new FanGuardKeeper(() => { var g = new Guard(); lock (started) started.Add(g); return g; });
        Assert.True(EnsureSoon(keeper));
        Assert.True(keeper.Ensure());
        Assert.Single(started);
        started[0].HasExited = true;
        Assert.Throws<IOException>(() => keeper.Beat());
        Assert.True(EnsureSoon(keeper));
        Assert.Equal(2, started.Count); Assert.Equal(1, keeper.Restarts);
        for (int i = 0; i < 100 && !started[0].Disposed; i++) Thread.Sleep(10);
        Assert.True(started[0].Disposed);
        keeper.Beat(); Assert.Equal(1, started[1].Beats);
    }

    [Fact]
    public void StalledGuardIsDroppedSoTheCallerStopsWriting()
    {
        var started = new List<Guard>();
        using var keeper = new FanGuardKeeper(() => { var g = new Guard(); lock (started) started.Add(g); return g; });
        Assert.True(EnsureSoon(keeper));
        started[0].Stalled = true;
        Assert.False(keeper.Ensure());   // this tick: no guard, so no software fan writes
        Assert.Equal(1, keeper.Restarts);
        Assert.True(EnsureSoon(keeper));
        Assert.Equal(2, started.Count);
    }

    [Fact]
    public void GuardThatCannotStartIsRetriedAfterAPause()
    {
        int attempts = 0;
        var clock = new Clock();
        using var keeper = new FanGuardKeeper(() => { Interlocked.Increment(ref attempts); throw new IOException("Fan guard did not become ready."); }, clock);
        for (int i = 0; i < 200 && keeper.LastStartError is null; i++) { keeper.Ensure(); Thread.Sleep(10); }
        Assert.IsType<IOException>(keeper.LastStartError);
        Assert.False(keeper.Ensure()); Assert.False(keeper.Ensure());
        Assert.Equal(1, attempts);                 // no new start before the pause is over
        clock.Now += FanGuardKeeper.RetryAfterFailure;
        keeper.Ensure();
        for (int i = 0; i < 200 && Volatile.Read(ref attempts) < 2; i++) Thread.Sleep(10);
        Assert.Equal(2, attempts);
        keeper.Beat();                             // no guard: nothing to beat, nothing thrown
    }

    [Fact]
    public void AGuardStillStartingWhenTheKeeperIsDisposedIsStoppedOnceItIsUp()
    {
        using var release = new ManualResetEventSlim();
        var guard = new Guard();
        var keeper = new FanGuardKeeper(() => { release.Wait(); return guard; });
        Assert.False(keeper.Ensure());
        keeper.Dispose();
        release.Set();
        for (int i = 0; i < 200 && !guard.Disposed; i++) Thread.Sleep(10);
        Assert.True(guard.Disposed);
        Assert.False(keeper.Ensure());
    }
}
