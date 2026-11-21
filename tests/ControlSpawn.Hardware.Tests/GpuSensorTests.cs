using ControlSpawn.Core;
using ControlSpawn.Hardware;
using Xunit;

namespace ControlSpawn.Hardware.Tests;

public sealed class GpuSensorTests
{
    private static readonly TimeSpan ReadTimeout = TimeSpan.FromMilliseconds(1500);

    [Fact]
    public void DeadChildrenBackOffAtFiveTenTwentyFortyAndSixtySeconds()
    {
        var clock = new FakeClock();
        var children = new List<FakeChild>();
        using var client = new GpuSensorClient(clock, () =>
        {
            var child = new FakeChild { HasExited = true };
            children.Add(child);
            return child;
        });

        Assert.Null(client.TryRead(ReadTimeout));
        foreach (var seconds in new[] { 5, 10, 20, 40, 60, 60 })
        {
            var starts = children.Count;
            Assert.True(children[^1].Disposed);
            clock.Elapsed += TimeSpan.FromSeconds(seconds) - TimeSpan.FromMilliseconds(1);
            Assert.Null(client.TryRead(ReadTimeout));
            Assert.Equal(starts, children.Count);
            clock.Elapsed += TimeSpan.FromMilliseconds(1);
            Assert.Null(client.TryRead(ReadTimeout));
            Assert.Equal(starts + 1, children.Count);
        }
    }

    [Fact]
    public void SixtySecondsOfGoodReadsRestoreTheFiveSecondDelay()
    {
        var clock = new FakeClock();
        var first = new FakeChild { HasExited = true };
        var second = new FakeChild();
        var third = new FakeChild();
        var fourth = new FakeChild();
        var children = new Queue<FakeChild>([first, second, third, fourth]);
        using var client = new GpuSensorClient(clock, children.Dequeue);

        Assert.Null(client.TryRead(ReadTimeout));
        clock.Elapsed += TimeSpan.FromSeconds(5);
        Assert.Empty(client.TryRead(ReadTimeout)!);
        clock.Elapsed += TimeSpan.FromSeconds(59);
        Assert.Empty(client.TryRead(ReadTimeout)!);
        second.HasExited = true;
        Assert.Null(client.TryRead(ReadTimeout));
        clock.Elapsed += TimeSpan.FromSeconds(5);
        Assert.Null(client.TryRead(ReadTimeout)); // The 59-second streak did not reset the 10-second delay.
        clock.Elapsed += TimeSpan.FromSeconds(5);
        Assert.Empty(client.TryRead(ReadTimeout)!);
        for (int i = 0; i < 60; i++)
        {
            clock.Elapsed += TimeSpan.FromSeconds(1);
            Assert.Empty(client.TryRead(ReadTimeout)!);
        }

        third.HasExited = true;
        Assert.Null(client.TryRead(ReadTimeout));
        clock.Elapsed += TimeSpan.FromSeconds(5);
        Assert.Empty(client.TryRead(ReadTimeout)!);
        Assert.Empty(children);
    }

    [Fact]
    public void PendingStartupReturnsWithoutReadingAndReplyTimeoutDisposesTheChild()
    {
        var ready = new TaskCompletionSource<string?>();
        var neverReplies = new TaskCompletionSource<string?>();
        var child = new FakeChild { Ready = ready.Task, Reply = neverReplies.Task };
        using var client = new GpuSensorClient(new FakeClock(), () => child);

        Assert.Null(client.TryRead(ReadTimeout));
        Assert.Equal(0, child.Reads);
        Assert.False(child.Disposed);
        ready.SetResult("ready");
        Assert.Null(client.TryRead(TimeSpan.Zero));
        Assert.Equal(1, child.Reads);
        Assert.True(child.Disposed);
    }

    [Fact]
    public void ReadinessTimeoutDisposesTheChildAndDefersRestart()
    {
        var clock = new FakeClock();
        var ready = new TaskCompletionSource<string?>();
        var child = new FakeChild { Ready = ready.Task };
        var children = new Queue<FakeChild>([child, new FakeChild()]);
        using var client = new GpuSensorClient(clock, children.Dequeue);

        Assert.Null(client.TryRead(ReadTimeout));
        ready.SetResult(null);
        Assert.Null(client.TryRead(ReadTimeout));
        Assert.True(child.Disposed);
        Assert.Equal(0, child.Reads);
        Assert.Null(client.TryRead(ReadTimeout));
        Assert.Single(children);
        clock.Elapsed += TimeSpan.FromSeconds(5);
        Assert.Empty(client.TryRead(ReadTimeout)!);
    }

    [Fact]
    public void ErrorReplyCanRecoverOnTheSameChildAndDisposalStopsIt()
    {
        var child = new FakeChild { Reply = Task.FromResult<string?>("""{"error":"IOException"}""") };
        using (var client = new GpuSensorClient(new FakeClock(), () => child))
        {
            Assert.Null(client.TryRead(ReadTimeout));
            Assert.False(child.Disposed);
            child.Reply = Task.FromResult<string?>("[]");
            Assert.Empty(client.TryRead(ReadTimeout)!);
            Assert.Equal(2, child.Reads);
        }
        Assert.True(child.Disposed);
    }

    [Fact]
    public void ParsesGpuReadingsIncludingMissingValues()
    {
        var readings = GpuSensorClient.ParseReply("""
            [{"Id":"/gpu-amd/0/temperature/0","Name":"AMD GPU: GPU Core","Category":1,"Kind":"Temperature","Unit":"°C","Value":42.5},
             {"Id":"/gpu-amd/0/fan/0","Name":"AMD GPU: Fan","Category":1,"Kind":"Fan","Unit":"rpm","Value":null}]
            """);
        Assert.Collection(readings!,
            read => Assert.Equal(new SensorReading("/gpu-amd/0/temperature/0", "AMD GPU: GPU Core", SensorCategory.Gpu, "Temperature", "°C", 42.5), read),
            read => Assert.Equal(new SensorReading("/gpu-amd/0/fan/0", "AMD GPU: Fan", SensorCategory.Gpu, "Fan", "rpm", null), read));
        Assert.Empty(GpuSensorClient.ParseReply("[]")!);
    }

    [Theory]
    [InlineData("not JSON")]
    [InlineData("{\"error\":\"IOException\"}")]
    [InlineData("null")]
    [InlineData("[null]")]
    [InlineData("[{}]")]
    [InlineData("""[{"Id":"cpu","Name":"CPU","Category":0,"Kind":"Temperature","Unit":"C","Value":42}]""")]
    public void RejectsInvalidReplies(string line) => Assert.Null(GpuSensorClient.ParseReply(line));

    [Fact]
    public void MissingGpuFailsTheSnapshotAndSetsEveryFanToFullUntilGpuRecovers()
    {
        using var source = new MissingGpuSource();
        var port = new FakeFanControlPort();
        using var fans = new FanController(port);
        fans.Hold(2, 25);
        var safety = new FanSafety();
        var snapshot = SensorProjection.ReadSnapshot(source);

        Assert.False(snapshot.Success);
        Assert.Equal("GPU sensors stopped answering", snapshot.Error);
        Assert.Equal(45, snapshot.Temperature(SensorCategory.Cpu));
        fans.Apply(snapshot, TimeSpan.Zero);
        Assert.Equal(port.Headers.Order(), port.Speeds.Keys.Order());
        Assert.All(port.Speeds.Values, speed => Assert.Equal(100, speed));
        Assert.False(safety.Observe(snapshot));
        Assert.False(safety.Observe(snapshot));
        Assert.True(safety.Observe(snapshot));

        source.GpuSensorsMissing = false;
        snapshot = SensorProjection.ReadSnapshot(source);
        Assert.True(snapshot.Success);
        Assert.Null(snapshot.Error);
        Assert.False(safety.Observe(snapshot));
        fans.Apply(snapshot, TimeSpan.FromSeconds(100));
        Assert.Equal(25, port.Speeds[2]);
        Assert.True(port.Speeds[6] < 100);
    }

    private sealed class FakeClock : IClock
    {
        public TimeSpan Elapsed { get; set; }
        public Task DelayAsync(TimeSpan duration, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class FakeChild : IGpuSensorProcess
    {
        public bool HasExited { get; set; }
        public Task<string?> Ready { get; init; } = Task.FromResult<string?>("ready");
        public Task<string?> Reply { get; set; } = Task.FromResult<string?>("[]");
        public bool Disposed { get; private set; }
        public int Reads { get; private set; }
        public Task<string?> ReadAsync() { Reads++; return Reply; }
        public void Dispose() => Disposed = true;
    }

    private sealed class MissingGpuSource : ISensorSource
    {
        public bool GpuSensorsMissing { get; set; } = true;
        public IReadOnlyList<SensorReading> Read() => GpuSensorsMissing
            ? [new("cpu", "CPU", SensorCategory.Cpu, "Temperature", "°C", 45)]
            : [new("cpu", "CPU", SensorCategory.Cpu, "Temperature", "°C", 45), new("gpu", "GPU", SensorCategory.Gpu, "Temperature", "°C", 40)];
        public void Dispose() { }
    }
}
