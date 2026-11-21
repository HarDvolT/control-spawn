using ControlSpawn.Core.Ipc;
using ControlSpawn.Hardware;
using ControlSpawn.Lighting;
using Xunit;

namespace ControlSpawn.Service.Tests;

public sealed class ServiceLoopTests
{
    private sealed class NoDiscovery : IMiioDiscoveryTransport, IDiscoveryTransport
    {
        public Task<IReadOnlyList<(string Ip, byte[] Packet)>> ScanAsync(byte[] hello, IReadOnlyList<string> targets, TimeSpan duration, CancellationToken token) =>
            Task.FromResult<IReadOnlyList<(string, byte[])>>([]);
        public Task<IReadOnlyList<string>> SearchAsync(TimeSpan duration, CancellationToken token) =>
            Task.FromResult<IReadOnlyList<string>>([]);
    }

    private sealed class RecordingConnection : IConnectionFactory
    {
        public TaskCompletionSource Attempted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task<Stream> ConnectAsync(string host, int port, CancellationToken cancellationToken)
        {
            // Leave the handshake pending until the runtime is stopped.
            Attempted.TrySetResult();
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return Stream.Null;
        }
    }

    [Fact]
    public async Task SensorCyclesContinueWhileProcessSamplingIsBlocked()
    {
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var releaseSample = new ManualResetEventSlim();
        var sampling = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var load = new ProcessLoad(42, "fixture", 5);
        var discovery = new NoDiscovery();
        await using var runtime = new ServiceRuntime(false, new FakeSensorSource(), new FakeFanControlPort { Headers = [] },
            new FakeHealthProbe(), new FakeRgbBackend(), null, null,
            miioDiscovery: new(discovery, () => []), yeelightDiscovery: new(discovery))
        {
            Observe = true,
            SampleProcesses = () => { sampling.TrySetResult(); releaseSample.Wait(); return [load]; }
        };
        var settings = new ServiceSettings();
        var run = runtime.RunAsync(stop.Token);
        try
        {
            await sampling.Task.WaitAsync(stop.Token);
            while (runtime.GetState(settings).History!.Count < 2) await Task.Delay(25, stop.Token);
            Assert.Null(runtime.GetState(settings).System.TopProcesses);

            releaseSample.Set();
            while (runtime.GetState(settings).System.TopProcesses is null) await Task.Delay(25, stop.Token);
            Assert.Equal(load, Assert.Single(runtime.GetState(settings).System.TopProcesses!));
        }
        finally
        {
            releaseSample.Set();
            stop.Cancel();
            await run;
        }
    }

    [Fact]
    public async Task OpenRgbLaunchLeavesTheGateFreeAndConnectsOnALaterTick()
    {
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var releaseLaunch = new ManualResetEventSlim();
        var launching = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        int launches = 0;
        var connection = new RecordingConnection();
        var discovery = new NoDiscovery();
        await using var runtime = new ServiceRuntime(false, new FakeSensorSource(), new FakeFanControlPort { Headers = [] },
            new FakeHealthProbe(), new OpenRgbClient(connection), null, null,
            miioDiscovery: new(discovery, () => []), yeelightDiscovery: new(discovery))
        {
            SampleProcesses = () => null,
            StartOpenRgb = path => { Interlocked.Increment(ref launches); launching.TrySetResult(path); releaseLaunch.Wait(); }
        };
        var executable = Path.Combine(AppContext.BaseDirectory, "OpenRGB.exe");
        await runtime.ApplyAsync(new ServiceSettings { LaunchOpenRgb = true, OpenRgbPath = executable }, stop.Token);
        var run = runtime.RunAsync(stop.Token);
        try
        {
            Assert.Equal(executable, await launching.Task.WaitAsync(stop.Token));
            // This fails on the unknown device once it has acquired the RGB gate.
            await Assert.ThrowsAsync<InvalidOperationException>(() => runtime.ResizeZoneAsync("unknown", 0, 1, stop.Token));
            Assert.False(connection.Attempted.Task.IsCompleted);

            releaseLaunch.Set();
            await connection.Attempted.Task.WaitAsync(stop.Token);
            Assert.Equal(1, Volatile.Read(ref launches));
        }
        finally
        {
            releaseLaunch.Set();
            stop.Cancel();
            await run;
        }
    }
}
