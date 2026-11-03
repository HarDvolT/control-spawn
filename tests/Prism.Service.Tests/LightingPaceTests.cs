using System.Diagnostics;
using Prism.Service;
using Xunit;

namespace Prism.Service.Tests;

public sealed class LightingPaceTests
{
    private static TimeSpan S(double seconds) => TimeSpan.FromSeconds(seconds);

    [Fact]
    public void GoesIdleOnlyAfterThreeStillSecondsAndAnyChangeEndsItAtOnce()
    {
        var pace = new LightingPace();
        pace.Tick(false, S(0)); pace.Tick(false, S(2.9));
        Assert.False(pace.Idle);
        pace.Tick(false, S(3));
        Assert.True(pace.Idle);
        pace.Tick(true, S(3.1));
        Assert.False(pace.Idle);
        // The still time starts again from the change, not from the first still frame.
        pace.Tick(false, S(3.2)); pace.Tick(false, S(6.1));
        Assert.False(pace.Idle);
    }

    [Fact]
    public void IdleTimeCountsOnlyTheTimeSpentIdleAndResetsWhenTaken()
    {
        var pace = new LightingPace();
        pace.Tick(false, S(0)); pace.Tick(false, S(3));   // idle from 3 s
        pace.Tick(false, S(5));                             // 2 s idle
        pace.Tick(true, S(6));                              // 1 more idle second, then awake
        pace.Tick(true, S(7));
        Assert.Equal(S(3), pace.TakeIdleTime());
        Assert.Equal(TimeSpan.Zero, pace.TakeIdleTime());
    }

    [Fact]
    public void AnimatedFramesNeverGoIdle()
    {
        var pace = new LightingPace();
        for (int i = 0; i < 600; i++) pace.Tick(true, S(i / 60d));
        Assert.False(pace.Idle);
    }

    [Fact]
    public async Task WakeEndsTheIdleWaitEarlyAndTheNextTickIsFullSpeed()
    {
        var pace = new LightingPace();
        pace.Tick(false, S(0)); pace.Tick(false, S(5));
        Assert.True(pace.Idle);
        var wait = Stopwatch.StartNew();
        var idle = pace.WaitIdleAsync(CancellationToken.None);
        pace.Wake();
        await idle;
        Assert.True(wait.Elapsed < LightingPace.IdleTick);
        pace.Tick(false, S(5.1));
        Assert.False(pace.Idle);
    }

    [Fact]
    public async Task IdleWaitLastsAboutOneIdleTick()
    {
        var pace = new LightingPace();
        var wait = Stopwatch.StartNew();
        await pace.WaitIdleAsync(CancellationToken.None);
        Assert.True(wait.Elapsed >= LightingPace.IdleTick - TimeSpan.FromMilliseconds(20));
    }
}
