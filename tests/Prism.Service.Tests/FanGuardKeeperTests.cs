using Prism.Service;
using Xunit;

namespace Prism.Service.Tests;

public sealed class FanGuardKeeperTests
{
    private sealed class Guard : IFanGuard
    {
        public bool HasExited { get; set; }
        public bool Disposed { get; private set; }
        public int Beats { get; private set; }
        public void Beat() { if (HasExited) throw new IOException("Fan guard exited."); Beats++; }
        public void Dispose() => Disposed = true;
    }

    [Fact]
    public void DeadGuardIsDisposedAndReplacedOnTheNextEnsure()
    {
        var started = new List<Guard>();
        using var keeper = new FanGuardKeeper(() => { var g = new Guard(); started.Add(g); return g; });
        keeper.Ensure(); keeper.Ensure();
        Assert.Single(started);
        keeper.Beat(); Assert.Equal(1, started[0].Beats);
        started[0].HasExited = true;
        Assert.Throws<IOException>(keeper.Beat);
        keeper.Ensure();
        Assert.Equal(2, started.Count); Assert.True(started[0].Disposed); Assert.Equal(1, keeper.Restarts);
        keeper.Beat(); Assert.Equal(1, started[1].Beats);
    }

    [Fact]
    public void GuardThatCannotStartLeavesNoGuardSoTheCallerStopsWriting()
    {
        int attempts = 0;
        using var keeper = new FanGuardKeeper(() => { attempts++; throw new IOException("Fan guard did not become ready."); });
        Assert.Throws<IOException>(keeper.Ensure);
        Assert.Throws<IOException>(keeper.Ensure);
        Assert.Equal(2, attempts);   // each retry tries again rather than keeping a broken guard
        keeper.Beat();                // no guard: nothing to beat, nothing thrown
    }
}
