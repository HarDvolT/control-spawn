using ControlSpawn.Core.Ipc;
using ControlSpawn.Service;
using Xunit;

namespace ControlSpawn.Service.Tests;

public sealed class StateSharingTests
{
    private static StateMessage Empty() => System.Text.Json.JsonSerializer.Deserialize<StateMessage>("""
        {"timestamp":"2026-11-03T00:00:00Z","system":{},"fans":[],"lighting":{"effect":"Static","color":"#000000","brightness":1,"speed":1,"devices":[]},"health":{"mock":true,"elevated":false,"warnings":[],"fanFailsafeActive":false}}
        """, PipeContract.Json)!;

    [Fact]
    public void ReadsWithinAQuarterSecondShareOneBuildAndACommandInvalidatesIt()
    {
        int builds = 0; var time = TimeSpan.Zero;
        var hub = new StateHub(() => { builds++; return Empty(); }, () => time);
        var first = hub.Read();
        for (int session = 0; session < 10; session++) Assert.Same(first, hub.Read());
        Assert.Equal(1, builds);
        hub.Publish();                      // a command changed something: the next reader must see it
        Assert.NotSame(first, hub.Read());
        Assert.Equal(2, builds);
        time += TimeSpan.FromMilliseconds(300);
        hub.Read(); hub.Read();
        Assert.Equal(3, builds);
    }

    [Fact]
    public async Task ConcurrentReadersWaitForTheBuildInProgressInsteadOfStartingTheirOwn()
    {
        int builds = 0;
        var hub = new StateHub(() => { Interlocked.Increment(ref builds); Thread.Sleep(100); return Empty(); });
        await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(hub.Read)));
        Assert.Equal(1, builds);
    }

    [Fact]
    public void ConnectionsAreCappedPerClientProcessAndFreedOnClose()
    {
        var limiter = new ConnectionLimiter(perClient: 2);
        Assert.True(limiter.TryOpen("1234")); Assert.True(limiter.TryOpen("1234"));
        Assert.False(limiter.TryOpen("1234"));
        Assert.True(limiter.TryOpen("5678"));   // another process has its own allowance
        limiter.Close("1234");
        Assert.True(limiter.TryOpen("1234"));
        limiter.Close("1234"); limiter.Close("1234"); limiter.Close("1234");
        Assert.Equal(0, limiter.Open("1234"));
        Assert.Equal(ConnectionLimiter.DefaultPerClient, 4);
    }
}
