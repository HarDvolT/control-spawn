using ControlSpawn.Core;
using ControlSpawn.Service;
using Xunit;

namespace ControlSpawn.Service.Tests;

public sealed class StallWatchTests
{
    private static HardwareSnapshot Fans(params (int Header, double? Rpm, double? Duty)[] fans) => new(DateTimeOffset.UtcNow,
        fans.SelectMany(f => new[] {
            new SensorReading($"/lpc/nct6701d/0/fan/{f.Header - 1}", $"Fan #{f.Header}", SensorCategory.Motherboard, "Fan", "rpm", f.Rpm),
            new SensorReading($"/lpc/nct6701d/0/control/{f.Header - 1}", $"Fan #{f.Header}", SensorCategory.Motherboard, "Control", "%", f.Duty) }).ToArray());
    private static IReadOnlyList<int> Tick(StallWatch watch, HardwareSnapshot snapshot, double seconds, params int[] headers) =>
        watch.Observe(snapshot, headers.Length == 0 ? [2, 6, 7] : headers, TimeSpan.FromSeconds(seconds), _ => false, _ => 25);

    [Fact]
    public void PumpBelowFiveHundredRpmForFiveSecondsIsStalledOnceAndClearsWhenItSpinsAgain()
    {
        var watch = new StallWatch();
        Tick(watch, Fans((7, 2800, 100)), 0);
        Assert.Empty(Tick(watch, Fans((7, 300, 100)), 1));
        Assert.Empty(Tick(watch, Fans((7, 0, 100)), 5));
        Assert.False(watch.PumpStalled);
        Assert.Equal([7], Tick(watch, Fans((7, 0, 100)), 6));
        Assert.True(watch.PumpStalled);
        Assert.Empty(Tick(watch, Fans((7, 0, 100)), 7));   // reported once
        Tick(watch, Fans((7, 2750, 100)), 8);
        Assert.False(watch.PumpStalled);
    }

    [Fact]
    public void PumpThatNeverSpunOrVanishedReadingFollowTheSameRulesAsFans()
    {
        var never = new StallWatch();
        for (int t = 0; t < 10; t++) Tick(never, Fans((7, 0, 100)), t);
        Assert.False(never.PumpStalled);   // a header with nothing on it is not a stall
        var vanished = new StallWatch();
        Tick(vanished, Fans((7, 2800, 100)), 0);
        for (int t = 1; t <= 6; t++) Tick(vanished, Fans((7, null, null)), t);
        Assert.True(vanished.PumpStalled);
    }

    [Fact]
    public void FailedReadsAndFansBelowTheirFloorAreNotStalls()
    {
        var watch = new StallWatch();
        Tick(watch, Fans((2, 1200, 40), (7, 2800, 100)), 0);
        for (int t = 1; t <= 10; t++) Tick(watch, new HardwareSnapshot(DateTimeOffset.UtcNow, [], false), t);
        Assert.Empty(watch.Stalled);
        for (int t = 11; t <= 20; t++) Tick(watch, Fans((2, 0, 15), (7, 2800, 100)), t);
        Assert.Empty(watch.Stalled);   // 15% is below the 25% floor, the fan may stop there
        for (int t = 21; t <= 27; t++) Tick(watch, Fans((2, 0, 40), (7, 2800, 100)), t);
        Assert.Equal([2], watch.Stalled);
    }
}
