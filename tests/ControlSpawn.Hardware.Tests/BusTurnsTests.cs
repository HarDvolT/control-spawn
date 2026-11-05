using ControlSpawn.Core;
using Xunit;

namespace ControlSpawn.Hardware.Tests;

// BusTurns.Gpu is process-wide state, so these run one after another in this class.
public sealed class BusTurnsTests
{
    [Fact]
    public void WritesAreHeldDuringAndShortlyAfterARead()
    {
        bool duringRead = true;
        BusTurns.Gpu.Read(() => duringRead = BusTurns.Gpu.TryBeginWrite());
        Assert.False(duringRead);
        Assert.False(BusTurns.Gpu.TryBeginWrite());           // still inside ReadSettle
        Thread.Sleep(BusTurns.ReadSettle + TimeSpan.FromMilliseconds(30));
        Assert.True(BusTurns.Gpu.TryBeginWrite());
    }

    [Fact]
    public void AReadRightAfterAWriteWaitsForTheWriteToSettle()
    {
        Thread.Sleep(BusTurns.ReadSettle + TimeSpan.FromMilliseconds(30));
        Assert.True(BusTurns.Gpu.TryBeginWrite());
        var started = DateTime.UtcNow; var ranAt = DateTime.MinValue;
        BusTurns.Gpu.Read(() => ranAt = DateTime.UtcNow);
        Assert.True(ranAt - started >= BusTurns.WriteSettle - TimeSpan.FromMilliseconds(20));
    }
}

public sealed class BusTurnsRoutingTests
{
    [Fact]
    public void GpuAndRamUseTheirOwnSharedBusAndUsbDevicesUseNone()
    {
        Assert.Same(BusTurns.Gpu, BusTurns.ForDeviceKind("GPU"));
        Assert.Same(BusTurns.Smbus, BusTurns.ForDeviceKind("RAM"));
        Assert.Null(BusTurns.ForDeviceKind("Motherboard"));
        Assert.NotSame(BusTurns.Gpu, BusTurns.Smbus);
    }
}
