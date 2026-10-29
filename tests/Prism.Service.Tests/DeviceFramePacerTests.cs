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
    public void GpuIsCappedAtTenFramesPerSecondEvenWhenColoursChange()
    {
        var pacer = new DeviceFramePacer();
        Assert.True(pacer.ShouldSend("gpu", "GPU", Orange, Ms(0))); pacer.Sent("gpu", Orange, Ms(0));
        Assert.False(pacer.ShouldSend("gpu", "GPU", Blue, Ms(33)));   // a 30 fps tick: too soon for the GPU bus
        Assert.False(pacer.ShouldSend("gpu", "GPU", Blue, Ms(66)));
        Assert.True(pacer.ShouldSend("gpu", "GPU", Blue, Ms(100)));
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
    public void LedCountChangeAndResetSendImmediately()
    {
        var pacer = new DeviceFramePacer();
        pacer.Sent("strip", Orange, Ms(0));
        Assert.True(pacer.ShouldSend("strip", "Motherboard", Orange.Concat(Orange).ToArray(), Ms(40)));
        pacer.Sent("gpu", Orange, Ms(0)); pacer.Reset();
        Assert.True(pacer.ShouldSend("gpu", "GPU", Orange, Ms(1)));
    }
}
