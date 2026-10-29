using Prism.Core;
using Xunit;

namespace Prism.Hardware.Tests;

// GpuBusTurns is process-wide state, so these run one after another in this class.
public sealed class GpuBusTurnsTests
{
    [Fact]
    public void WritesAreHeldDuringAndShortlyAfterARead()
    {
        bool duringRead = true;
        GpuBusTurns.Read(() => duringRead = GpuBusTurns.TryBeginWrite());
        Assert.False(duringRead);
        Assert.False(GpuBusTurns.TryBeginWrite());           // still inside ReadSettle
        Thread.Sleep(GpuBusTurns.ReadSettle + TimeSpan.FromMilliseconds(30));
        Assert.True(GpuBusTurns.TryBeginWrite());
    }

    [Fact]
    public void AReadRightAfterAWriteWaitsForTheWriteToSettle()
    {
        Thread.Sleep(GpuBusTurns.ReadSettle + TimeSpan.FromMilliseconds(30));
        Assert.True(GpuBusTurns.TryBeginWrite());
        var started = DateTime.UtcNow; var ranAt = DateTime.MinValue;
        GpuBusTurns.Read(() => ranAt = DateTime.UtcNow);
        Assert.True(ranAt - started >= GpuBusTurns.WriteSettle - TimeSpan.FromMilliseconds(20));
    }
}
