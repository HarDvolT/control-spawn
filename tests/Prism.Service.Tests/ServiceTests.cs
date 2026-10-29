using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using System.Security.AccessControl;
using System.Security.Principal;
using System.IO.Pipes;
using Prism.Core;
using Prism.Core.Ipc;
using Prism.Service;
using Prism.Hardware;
using Prism.Lighting;
using Prism.Router;
using Xunit;

namespace Prism.Service.Tests;

public sealed class ServiceTests
{
    private sealed class Runtime : IServiceRuntime
    {
        public ServiceSettings? Applied { get; private set; }
        public int SpeedTests { get; private set; }
        public TaskCompletionSource<SpeedTestResult?>? SpeedCompletion { get; set; }
        public StateMessage GetState(ServiceSettings settings) => StateAssembler.Assemble(new(DateTimeOffset.UnixEpoch, []), settings, [2, 6, 7], [], null, null, new(true, false, [], false));
        public Task ApplyAsync(ServiceSettings settings, CancellationToken token) { Applied = settings; return Task.CompletedTask; }
        public Task<SpeedTestResult?> StartSpeedTestAsync(CancellationToken token)
        {
            SpeedTests++;
            return SpeedCompletion is null ? Task.FromResult<SpeedTestResult?>(new(10, 1, 200, 30, 0)) : SpeedCompletion.Task.WaitAsync(token);
        }
    }
    private static Request Request(int id, string command, object? args = null) => new(id, command, args is null ? null : JsonSerializer.SerializeToElement(args, PipeContract.Json));

    [Theory]
    [InlineData("Gaming", FanMode.Turbo, LightingEffect.Smart, true, 60, "own")]
    [InlineData("Work", FanMode.Standard, LightingEffect.Static, true, 90, "own")]
    [InlineData("Night", FanMode.Silent, LightingEffect.Breathing, false, 60, "own")]
    [InlineData("Movie", FanMode.Silent, LightingEffect.Adaptive, true, 60, "average")]
    public async Task SceneAppliesAllDomainsAndPersists(string name, FanMode mode, LightingEffect effect, bool on, int brightness, string follow)
    {
        var runtime = new Runtime(); var store = new MemorySettingsStore(); var processor = new CommandProcessor(store, runtime);
        Assert.True((await processor.HandleAsync(Request(7, Commands.ApplyScene, new { name }))).Ok);
        Assert.All(runtime.Applied!.Fans.Values, f => Assert.Equal(mode, f.Mode));
        Assert.Equal(effect, runtime.Applied.Lighting.Effect); Assert.Equal(on, runtime.Applied.Lamp.On);
        Assert.Equal(brightness, runtime.Applied.Lamp.Brightness); Assert.Equal(follow, runtime.Applied.Lamp.Follow);
        Assert.Equal(runtime.Applied, store.Load());
    }
    [Fact]
    public async Task AllCommandsAcceptContractArguments()
    {
        var runtime = new Runtime(); var processor = new CommandProcessor(new MemorySettingsStore(), runtime);
        var points = Enumerable.Range(0, 8).Select(i => new[] { 20d + i * 10, 20d + i * 10 }).ToArray();
        var requests = new[] {
            Request(1, Commands.GetState),
            Request(2, Commands.SetFanMode, new { group = "Radiator", mode = "Turbo" }),
            Request(3, Commands.SetFanCurve, new { group = "Case fans", points, source = "Maximum" }),
            Request(4, Commands.SetFanMode, new { group = "Case fans", mode = "Custom" }),
            Request(5, Commands.SetLighting, new { effect = "RainbowWave", color = "#AABBCC", brightness = 0.5, speed = 0 }),
            Request(6, Commands.SetDeviceSync, new { device = "0", enabled = false }),
            Request(7, Commands.SetLamp, new { on = true, brightness = 50, follow = "pc", color = "#112233" }),
            Request(8, Commands.StartSpeedTest), Request(9, Commands.ApplyScene, new { name = "Gaming" })
        };
        foreach (var request in requests) Assert.True((await processor.HandleAsync(request)).Ok, request.Command);
        Assert.Equal(1, runtime.SpeedTests); Assert.False(processor.Settings.SyncedDevices["0"]);
    }
    public static IEnumerable<object[]> InvalidCommands()
    {
        yield return [Commands.SetFanMode, new { group = "AIO pump", mode = "Silent" }];
        yield return [Commands.SetFanMode, new { group = "Radiator", mode = "Custom" }];
        yield return [Commands.SetFanMode, new { group = "Radiator", mode = "999" }];
        yield return [Commands.SetFanMode, new { group = "Radiator", mode = "turbo" }];
        yield return [Commands.SetFanCurve, new { group = "Radiator", points = new[] { new[] { 20, 30 } }, source = "Cpu" }];
        yield return [Commands.SetFanCurve, new { group = "Case fans", points = Enumerable.Range(0, 8).Select(i => new[] { 20 + 10 * i, 101 }).ToArray(), source = "Cpu" }];
        yield return [Commands.SetFanCurve, new { group = "Case fans", points = Enumerable.Repeat(new[] { 20, 30 }, 8).ToArray(), source = "Cpu" }];
        yield return [Commands.SetLighting, new { effect = "Unknown", color = "#FFFFFF", brightness = 0.5, speed = 1 }];
        yield return [Commands.SetLighting, new { effect = "Static", color = "white", brightness = 0.5, speed = 1 }];
        yield return [Commands.SetLighting, new { effect = "Static", color = "#FFFFFF", brightness = 2, speed = 1 }];
        yield return [Commands.SetLighting, new { effect = "Static", color = "#FFFFFF", brightness = 0.5, speed = -1 }];
        yield return [Commands.SetLamp, new { on = true, brightness = 0, follow = "own", color = "#FFFFFF" }];
        yield return [Commands.SetLamp, new { on = true, brightness = 100, follow = "other", color = "#FFFFFF" }];
        yield return [Commands.SetDeviceSync, new { device = "", enabled = true }];
        yield return [Commands.ApplyScene, new { name = "Unknown" }];
        yield return ["rebootRouter", new { }];
        yield return [Commands.SetLamp, new { on = "yes" }];
    }
    [Theory]
    [MemberData(nameof(InvalidCommands))]
    public async Task InvalidCommandDoesNotMutateOrApply(string command, object args)
    {
        var runtime = new Runtime(); var processor = new CommandProcessor(new MemorySettingsStore(), runtime); var before = processor.Settings;
        var response = await processor.HandleAsync(Request(42, command, args));
        Assert.Equal(42, response.Id); Assert.False(response.Ok); Assert.NotEmpty(response.Error!);
        Assert.Same(before, processor.Settings); Assert.Null(runtime.Applied);
    }
    [Fact]
    public void SnapshotProjectionPreservesMissingValuesAndZero()
    {
        var sensors = new[] {
            new SensorReading("/cpu/load/0", "CPU Total", SensorCategory.Cpu, "Load", "%", 0),
            new SensorReading("/cpu/load/1", "CPU Core #1", SensorCategory.Cpu, "Load", "%", null),
            new SensorReading("/cpu/temperature/0", "CPU Tctl", SensorCategory.Cpu, "Temperature", "C", 49),
            new SensorReading("/ram/data/0", "Memory Used", SensorCategory.Memory, "Data", "GiB", 12),
            new SensorReading("prism/ram/total", "Memory Total", SensorCategory.Memory, "Data", "GiB", 32),
            new SensorReading("/board/fan/1", "Fan #2", SensorCategory.Motherboard, "Fan", "rpm", null),
            new SensorReading("/board/fan/5", "Fan #6", SensorCategory.Motherboard, "Fan", "rpm", 0)
        };
        var state = StateAssembler.Assemble(new(DateTimeOffset.UnixEpoch, sensors), new(), [2, 6, 7], [], null, null, new(true, false, [], false));
        Assert.Equal(0, state.System.CpuLoad); Assert.Null(state.System.CpuClockMhz); Assert.Null(state.System.GpuTemp);
        Assert.Single(state.System.CpuCoreLoads); Assert.Null(state.System.CpuCoreLoads[0]);
        Assert.Equal(32, state.System.RamTotalGb); Assert.Null(state.Fans.Single(f => f.Header == 2).Rpm);
        Assert.Equal(0, state.Fans.Single(f => f.Header == 6).Rpm); Assert.Null(state.Lamp); Assert.Null(state.Router);
        var json = JsonSerializer.Serialize(state, PipeContract.Json);
        var roundTrip = JsonSerializer.Deserialize<StateMessage>(json, PipeContract.Json)!;
        Assert.Null(roundTrip.System.GpuTemp); Assert.Null(roundTrip.Fans[0].Rpm); Assert.Equal(0, roundTrip.System.CpuLoad);
    }
    [Fact]
    public void SettingsFileRoundTripsScenesCustomCurvesAndSync()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "settings-" + Guid.NewGuid() + ".json");
        try
        {
            var settings = new ServiceSettings(); settings.Fans["Radiator"] = new(FanMode.Custom, TemperatureSource.Maximum,
                Enumerable.Range(0, 8).Select(i => new FanPoint(20 + i * 10, 20 + i * 10)).ToArray());
            settings.SyncedDevices["ram"] = false;
            var store = new JsonSettingsStore(path); store.Save(settings); var restored = store.Load();
            Assert.Equal(4, restored.Scenes.Count); Assert.Equal(settings.Fans["Radiator"].Points, restored.Fans["Radiator"].Points);
            Assert.False(restored.SyncedDevices["ram"]); Assert.Equal(3500, restored.Scenes["Work"].Lamp.Kelvin);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }
    [Fact]
    public async Task MockRuntimeRunsAndStopsUsingOnlyFakes()
    {
        await using var runtime = new ServiceRuntime(true);
        var settings = new ServiceSettings(); await runtime.ApplyAsync(settings, default);
        using var stop = new CancellationTokenSource(TimeSpan.FromMilliseconds(150));
        try { await runtime.RunAsync(stop.Token); } catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
        var state = runtime.GetState(settings);
        Assert.True(state.Health.Mock); Assert.Equal(45, state.System.CpuTemp); Assert.Equal(2, state.Lighting.Devices.Count);
        Assert.Equal("mock", state.Lamp!.Transport); Assert.True(state.Router!.Connected);
    }

    [Fact]
    public async Task SceneReachesFanLightingAndLampFakesAndStopRestoresEveryHeader()
    {
        var controls = new FakeFanControlPort { Headers = [2, 6, 7] };
        var rgb = new FakeRgbBackend(); var lamp = new FakeLampClient();
        await using var runtime = new ServiceRuntime(true, new FakeSensorSource(), controls, new FakeHealthProbe(), rgb, lamp, new FakeRouterBackend());
        var processor = new CommandProcessor(new MemorySettingsStore(), runtime);
        Assert.True((await processor.HandleAsync(Request(1, Commands.ApplyScene, new { name = "Work" }))).Ok);
        using var stop = new CancellationTokenSource(); var run = runtime.RunAsync(stop.Token);
        // Initial synchronous snapshot + lighting/lamp setters have already reached the fakes.
        Assert.Equal(100f, controls.Speeds[7]);
        // FanController starts at 100% and ramps down conservatively toward the Standard curve.
        Assert.InRange(controls.Speeds[2], 99f, 100f);
        Assert.Equal(FanMode.Standard, runtime.GetState(processor.Settings).FanMode);
        Assert.Equal(new RgbColor(228, 228, 231), rgb.Colors[0][0]); Assert.True(lamp.Power);
        Assert.Equal(3500, processor.Settings.Lamp.Kelvin);
        stop.Cancel(); await run;
        Assert.All(new[] { 2, 6, 7 }, header => Assert.Contains(header, controls.Restored));
    }

    private sealed class FailingControls : IFanControlPort
    {
        public IReadOnlyList<int> Headers => new[] { 2, 6, 7 };
        public List<int> Restored { get; } = [];
        public void SetSoftware(int header, float percent) => throw new IOException("Injected fan write failure.");
        public void SetDefault(int header) => Restored.Add(header);
    }
    [Fact]
    public async Task WorkerFaultRestoresBiosControls()
    {
        var controls = new FailingControls();
        await using var runtime = new ServiceRuntime(true, new FakeSensorSource(), controls, new FakeHealthProbe(), new FakeRgbBackend(), new FakeLampClient(), new FakeRouterBackend());
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        await Assert.ThrowsAsync<IOException>(() => runtime.RunAsync(stop.Token));
        Assert.All(controls.Headers, header => Assert.Contains(header, controls.Restored));
    }

    [Fact]
    public void PipeAclAllowsInteractiveSystemAdminsAndDeniesNetwork()
    {
        var rules = PipeServer.CreateSecurity().GetAccessRules(true, true, typeof(SecurityIdentifier)).Cast<PipeAccessRule>().ToArray();
        Assert.Contains(rules, r => r.IdentityReference.Value == "S-1-5-2" && r.AccessControlType == AccessControlType.Deny);
        foreach (var sid in new[] { "S-1-5-4", "S-1-5-18", "S-1-5-32-544" })
            Assert.Contains(rules, r => r.IdentityReference.Value == sid && r.AccessControlType == AccessControlType.Allow);
        Assert.DoesNotContain(rules, r => r.IdentityReference.Value == "S-1-1-0");
    }

    [Fact]
    public async Task DuplexProtocolRequiresHelloChecksVersionAndReturnsMatchingIds()
    {
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var (client, server) = MemoryDuplex.Create();
        await using var ownedClient = client; await using var ownedServer = server;
        var processor = new CommandProcessor(new MemorySettingsStore(), new Runtime());
        var session = new PipeSession(processor).RunAsync(server, stop.Token);
        using var writer = new StreamWriter(client, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };
        using var reader = new StreamReader(client, leaveOpen: true);
        async Task<Response> Send(Request request)
        {
            await writer.WriteLineAsync(JsonSerializer.Serialize(request, PipeContract.Json));
            return JsonSerializer.Deserialize<Response>((await reader.ReadLineAsync(stop.Token))!, PipeContract.Json)!;
        }
        Assert.False((await Send(Request(1, Commands.GetState))).Ok);
        Assert.True((await Send(Request(2, Commands.Hello, new { version = 1, client = "app" }))).Ok);
        var response = await Send(Request(3, Commands.GetState));
        Assert.Equal(3, response.Id); Assert.True(response.Ok); Assert.Equal("state", response.Data!.Value.GetProperty("type").GetString());
        Assert.True((await Send(Request(4, Commands.Subscribe))).Ok);
        var push = JsonSerializer.Deserialize<StateMessage>((await reader.ReadLineAsync(stop.Token))!, PipeContract.Json)!;
        Assert.Equal("state", push.Type); Assert.True(push.Health.Mock);
        var mismatch = await Send(Request(5, Commands.Hello, new { version = 99, client = "app" }));
        Assert.False(mismatch.Ok); await session;
    }
    [Fact]
    public async Task MalformedJsonDoesNotEndSessionAndOversizedLinesAreBounded()
    {
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        var (client, server) = MemoryDuplex.Create();
        await using var a = client; await using var b = server;
        var session = new PipeSession(new(new MemorySettingsStore(), new Runtime())).RunAsync(server, stop.Token);
        using var writer = new StreamWriter(client, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };
        using var reader = new StreamReader(client, leaveOpen: true);
        await writer.WriteLineAsync("not json");
        Assert.False(JsonSerializer.Deserialize<Response>((await reader.ReadLineAsync(stop.Token))!, PipeContract.Json)!.Ok);
        await writer.WriteLineAsync(JsonSerializer.Serialize(Request(8, Commands.Hello, new { version = 1, client = "widget" }), PipeContract.Json));
        Assert.True(JsonSerializer.Deserialize<Response>((await reader.ReadLineAsync(stop.Token))!, PipeContract.Json)!.Ok);
        await writer.WriteLineAsync(new string('x', PipeSession.MaximumLineLength + 1));
        await Assert.ThrowsAsync<InvalidDataException>(() => session);
    }
    [Fact]
    public async Task FramingRejectsUnterminatedRequest()
    {
        using var reader = new StringReader("{\"id\":1}");
        await Assert.ThrowsAsync<InvalidDataException>(() => PipeSession.ReadLineAsync(reader, default));
    }

    [Fact]
    public async Task ClientsHaveIndependentHandshakeSubscriptionAndDisconnect()
    {
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var processor = new CommandProcessor(new MemorySettingsStore(), new Runtime());
        var (client1, server1) = MemoryDuplex.Create(); var (client2, server2) = MemoryDuplex.Create();
        await using var a = client1; await using var b = server1; await using var c = client2; await using var d = server2;
        var session1 = new PipeSession(processor).RunAsync(server1, stop.Token);
        var session2 = new PipeSession(processor).RunAsync(server2, stop.Token);
        using var writer1 = new StreamWriter(client1, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };
        using var writer2 = new StreamWriter(client2, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };
        using var reader1 = new StreamReader(client1, leaveOpen: true); using var reader2 = new StreamReader(client2, leaveOpen: true);
        await writer1.WriteLineAsync(JsonSerializer.Serialize(Request(1, Commands.Hello, new { version = 1, client = "app" }), PipeContract.Json));
        Assert.True(JsonSerializer.Deserialize<Response>((await reader1.ReadLineAsync(stop.Token))!, PipeContract.Json)!.Ok);
        await writer2.WriteLineAsync(JsonSerializer.Serialize(Request(2, Commands.Subscribe), PipeContract.Json));
        Assert.False(JsonSerializer.Deserialize<Response>((await reader2.ReadLineAsync(stop.Token))!, PipeContract.Json)!.Ok);
        await writer2.WriteLineAsync(JsonSerializer.Serialize(Request(3, Commands.Hello, new { version = 1, client = "widget" }), PipeContract.Json));
        Assert.True(JsonSerializer.Deserialize<Response>((await reader2.ReadLineAsync(stop.Token))!, PipeContract.Json)!.Ok);
        await client1.DisposeAsync(); await session1;
        await writer2.WriteLineAsync(JsonSerializer.Serialize(Request(4, Commands.Subscribe), PipeContract.Json));
        Assert.True(JsonSerializer.Deserialize<Response>((await reader2.ReadLineAsync(stop.Token))!, PipeContract.Json)!.Ok);
        Assert.Equal("state", JsonDocument.Parse((await reader2.ReadLineAsync(stop.Token))!).RootElement.GetProperty("type").GetString());
        await client2.DisposeAsync(); await session2;
    }

    [Fact]
    public async Task InvalidHelloTypesReturnErrorAndClose()
    {
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        var (client, server) = MemoryDuplex.Create(); await using var a = client; await using var b = server;
        var session = new PipeSession(new(new MemorySettingsStore(), new Runtime())).RunAsync(server, stop.Token);
        using var writer = new StreamWriter(client, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };
        using var reader = new StreamReader(client, leaveOpen: true);
        await writer.WriteLineAsync(JsonSerializer.Serialize(Request(99, Commands.Hello, new { version = "1", client = 7 }), PipeContract.Json));
        var response = JsonSerializer.Deserialize<Response>((await reader.ReadLineAsync(stop.Token))!, PipeContract.Json)!;
        Assert.Equal(99, response.Id); Assert.False(response.Ok); await session;
    }

    private sealed class FailingStore : ISettingsStore
    {
        public ServiceSettings Load() => new();
        public void Save(ServiceSettings settings) => throw new IOException("Injected disk failure.");
    }
    [Fact]
    public async Task PersistenceFailureLeavesRuntimeAndDesiredSettingsUnchanged()
    {
        var runtime = new Runtime(); var processor = new CommandProcessor(new FailingStore(), runtime); var before = processor.Settings;
        var response = await processor.HandleAsync(Request(1, Commands.ApplyScene, new { name = "Night" }));
        Assert.False(response.Ok); Assert.Same(before, processor.Settings); Assert.Null(runtime.Applied);
    }

    [Fact]
    public async Task LongSpeedTestReturnsResultWithoutBlockingSceneOnSameConnection()
    {
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var runtime = new Runtime { SpeedCompletion = new(TaskCreationOptions.RunContinuationsAsynchronously) };
        var (client, server) = MemoryDuplex.Create(); await using var a = client; await using var b = server;
        var session = new PipeSession(new(new MemorySettingsStore(), runtime)).RunAsync(server, stop.Token);
        using var writer = new StreamWriter(client, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };
        using var reader = new StreamReader(client, leaveOpen: true);
        await writer.WriteLineAsync(JsonSerializer.Serialize(Request(1, Commands.Hello, new { version = 1, client = "app" }), PipeContract.Json));
        await reader.ReadLineAsync(stop.Token);
        await writer.WriteLineAsync(JsonSerializer.Serialize(Request(2, Commands.StartSpeedTest), PipeContract.Json));
        await writer.WriteLineAsync(JsonSerializer.Serialize(Request(3, Commands.ApplyScene, new { name = "Night" }), PipeContract.Json));
        var scene = JsonSerializer.Deserialize<Response>((await reader.ReadLineAsync(stop.Token))!, PipeContract.Json)!;
        Assert.Equal(3, scene.Id); Assert.True(scene.Ok); Assert.False(runtime.Applied!.Lamp.On);
        runtime.SpeedCompletion.SetResult(new(10, 1, 200, 30, 0));
        var speed = JsonSerializer.Deserialize<Response>((await reader.ReadLineAsync(stop.Token))!, PipeContract.Json)!;
        Assert.Equal(2, speed.Id); Assert.True(speed.Ok); Assert.Equal(200, speed.Data!.Value.GetProperty("down").GetDouble());
        await client.DisposeAsync(); await session;
    }

    /// <summary>Two byte channels, fragmented reads; no OS pipe, sockets, services or hardware.</summary>
    private sealed class MemoryDuplex(Channel<byte> input, Channel<byte> output) : Stream
    {
        public static (Stream Client, Stream Server) Create()
        {
            var left = Channel.CreateUnbounded<byte>(); var right = Channel.CreateUnbounded<byte>();
            return (new MemoryDuplex(left, right), new MemoryDuplex(right, left));
        }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default)
        {
            if (buffer.Length == 0) return 0;
            if (!await input.Reader.WaitToReadAsync(token)) return 0;
            int count = 0;
            while (count < Math.Min(3, buffer.Length) && input.Reader.TryRead(out var value)) buffer.Span[count++] = value;
            return count;
        }
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken token = default)
        { token.ThrowIfCancellationRequested(); foreach (var value in buffer.Span) if (!output.Writer.TryWrite(value)) throw new IOException("Closed."); return ValueTask.CompletedTask; }
        public override void Write(byte[] buffer, int offset, int count) => WriteAsync(buffer.AsMemory(offset, count)).GetAwaiter().GetResult();
        public override int Read(byte[] buffer, int offset, int count) => ReadAsync(buffer.AsMemory(offset, count)).GetAwaiter().GetResult();
        public override void Flush() { }
        public override Task FlushAsync(CancellationToken token) => Task.CompletedTask;
        protected override void Dispose(bool disposing) { output.Writer.TryComplete(); base.Dispose(disposing); }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }
}
