using ControlSpawn.Service;
using Xunit;

namespace ControlSpawn.Service.Tests;

public sealed class FrameGuardTests
{
    [Fact]
    public void FailingDeviceIsSkippedOthersRenderAndLogsAreLimitedToOncePerMinute()
    {
        var logged = new List<(string, Type)>();
        var guard = new FrameGuard((key, e) => logged.Add((key, e.GetType())));
        int[] Broken() => throw new IndexOutOfRangeException();
        for (int frame = 0; frame < 120; frame++)   // two seconds at 60 fps
        {
            var now = TimeSpan.FromSeconds(frame / 60d);
            Assert.False(guard.TryRun("gpu", Broken, now, out _));
            Assert.True(guard.TryRun("ram", () => new[] { frame }, now, out var colors));
            Assert.Equal(frame, colors[0]);
        }
        Assert.Equal([("gpu", typeof(IndexOutOfRangeException))], logged);
        guard.TryRun("gpu", Broken, TimeSpan.FromSeconds(61), out _);
        Assert.Equal(2, logged.Count);
        Assert.Equal(121, guard.Failures);
    }

    [Fact]
    public void CancellationStillStopsTheLoop()
    {
        var guard = new FrameGuard((_, _) => { });
        Assert.Throws<OperationCanceledException>(() => guard.TryRun<int>("gpu", () => throw new OperationCanceledException(), TimeSpan.Zero, out _));
    }
}
