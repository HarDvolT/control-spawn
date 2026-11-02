using Prism.Core;
using Prism.Service;
using Xunit;

namespace Prism.Service.Tests;

public sealed class DeviceFramePacerTests
{
    private static readonly RgbColor[] Orange = Enumerable.Repeat(new RgbColor(255, 77, 26), 4).ToArray();
    private static readonly RgbColor[] Blue = Enumerable.Repeat(new RgbColor(90, 167, 255), 4).ToArray();
    private static TimeSpan Ms(int ms) => TimeSpan.FromMilliseconds(ms);

    [Fact]
    public void GpuIsCappedAtSixFramesPerSecondEvenWhenColoursChange()
    {
        var pacer = new DeviceFramePacer();
        Assert.True(pacer.ShouldSend("gpu", "GPU", Orange, Ms(0))); pacer.Sent("gpu", Orange, Ms(0));
        Assert.False(pacer.ShouldSend("gpu", "GPU", Blue, Ms(33)));   // a new colour has to hold before the card gets it
        Assert.False(pacer.ShouldSend("gpu", "GPU", Blue, Ms(100)));
        Assert.True(pacer.ShouldSend("gpu", "GPU", Blue, Ms(170)));
    }

    [Fact]
    public void BoardAndStripKeepThirtyFramesPerSecondAndRamTwenty()
    {
        var pacer = new DeviceFramePacer();
        pacer.Sent("board", Orange, Ms(0)); pacer.Sent("ram", Orange, Ms(0));
        Assert.True(pacer.ShouldSend("board", "Motherboard", Blue, Ms(34)));
        Assert.False(pacer.ShouldSend("ram", "RAM", Blue, Ms(34)));
        Assert.True(pacer.ShouldSend("ram", "RAM", Blue, Ms(50)));
    }

    [Fact]
    public void InvisibleChangesAreSkippedButResentAsKeepAlive()
    {
        var pacer = new DeviceFramePacer();
        pacer.Sent("ram", Orange, Ms(0)); pacer.Sent("gpu", Orange, Ms(0));
        var almost = Orange.Select(c => new RgbColor((byte)(c.R - 2), (byte)(c.G + 1), c.B)).ToArray();
        Assert.False(pacer.ShouldSend("ram", "RAM", almost, Ms(500)));
        Assert.True(pacer.ShouldSend("ram", "RAM", almost, Ms(2000)));
        Assert.False(pacer.ShouldSend("gpu", "GPU", almost, Ms(2000)));   // the GPU is only refreshed every 10 s
        Assert.True(pacer.ShouldSend("gpu", "GPU", almost, Ms(10000)));
    }

    [Fact]
    public void AFadeAlwaysReachesBlack()
    {
        // The last steps of a heartbeat fade are 1-2 apart; skipping them left the fans on a dim, off-hue glow.
        var pacer = new DeviceFramePacer();
        RgbColor[] Dim(byte r, byte g) => Enumerable.Repeat(new RgbColor(r, g, 0), 4).ToArray();
        pacer.Sent("board", Dim(3, 1), Ms(0));
        Assert.True(pacer.ShouldSend("board", "Motherboard", Dim(0, 0), Ms(34)));
    }

    [Fact]
    public void GpuWaitsForAColourToSettle()
    {
        // Dragging brightness: the colour changes every tick, so nothing is written until it stops.
        var pacer = new DeviceFramePacer(); pacer.Sent("gpu", Orange, Ms(0));
        for (int t = 200; t < 2000; t += 33)
        {
            var dimmer = Orange.Select(c => new RgbColor((byte)(c.R - t / 20), c.G, c.B)).ToArray();
            Assert.False(pacer.ShouldSend("gpu", "GPU", dimmer, Ms(t)));
        }
        Assert.True(pacer.ShouldSend("gpu", "GPU", Blue, Ms(2000)) || pacer.ShouldSend("gpu", "GPU", Blue, Ms(2400)));
    }

    [Fact]
    public void ZoneChangesAreFoundPerZone()
    {
        RgbColor[] last = [new(255, 0, 0), new(0, 0, 255), new(0, 255, 0), new(10, 10, 10)];
        RgbColor[] next = [new(255, 0, 0), new(0, 0, 200), new(0, 254, 0), new(10, 10, 10)];
        Assert.False(DeviceFramePacer.ZoneChanged(last, next, 0, 1));
        Assert.True(DeviceFramePacer.ZoneChanged(last, next, 1, 1));
        Assert.False(DeviceFramePacer.ZoneChanged(last, next, 2, 2));
    }

    [Fact]
    public void LedCountChangeAndResetSendImmediately()
    {
        var pacer = new DeviceFramePacer();
        pacer.Sent("strip", Orange, Ms(0));
        Assert.True(pacer.ShouldSend("strip", "Motherboard", Orange.Concat(Orange).ToArray(), Ms(40)));
        Assert.Equal(4.8, DeviceFramePacer.FrameTime("GPU", 4.5), 6); Assert.Equal(4.4, DeviceFramePacer.FrameTime("RAM", 4.4));
        pacer.Sent("gpu", Orange, Ms(0)); pacer.Reset();
        Assert.True(pacer.ShouldSend("gpu", "GPU", Orange, Ms(1)));
    }
}
