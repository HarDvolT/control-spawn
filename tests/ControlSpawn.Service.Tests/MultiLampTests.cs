using System.Text.Json;
using ControlSpawn.Core.Ipc;
using ControlSpawn.Lighting;
using ControlSpawn.Router;
using ControlSpawn.Hardware;
using Xunit;

namespace ControlSpawn.Service.Tests;
public sealed class MultiLampTests
{
    private sealed class Runtime : IServiceRuntime
    {
        public Func<string, MiioDevice, Task>? Resolved;
        public void SetLampResolutionHandler(Func<string, MiioDevice, Task> handler) => Resolved = handler;
        public StateMessage GetState(ServiceSettings settings) => StateAssembler.Assemble(new(DateTimeOffset.UnixEpoch, []), settings, [], [], null, null, new(true, false, [], false));
        public Task ApplyAsync(ServiceSettings settings, CancellationToken token) => Task.CompletedTask;
        public Task<SpeedTestResult?> StartSpeedTestAsync(CancellationToken token) => Task.FromResult<SpeedTestResult?>(null);
        public Task<IReadOnlyList<MiioDevice>> DiscoverLampsAsync(CancellationToken token) => Task.FromResult<IReadOnlyList<MiioDevice>>([new("192.0.2.175", 0x1c10fe71, 1), new("192.0.2.246", 0x1c058697, 2)]);
    }
    private static Request Command(string command, object args) => new(1, command, JsonSerializer.SerializeToElement(args, PipeContract.Json));
    [Fact]
    public async Task ProvisionTwoIdsDiscoverRenameControlAndRemoveWithoutEchoingSecrets()
    {
        var runtime = new Runtime(); var store = new MemorySettingsStore(); var saved = new Dictionary<uint, byte[]>();
        var processor = new CommandProcessor(store, runtime, saveDeviceToken: (id, bytes) => saved[id] = bytes.ToArray(), removeDeviceToken: id => saved.Remove(id));
        const string token = "0123456789abcdef0123456789abcdef";
        Assert.True((await processor.HandleAsync(Command(Commands.SetLampToken, new { deviceId = "0x1c10fe71", name = "Room", token }))).Ok);
        Assert.True((await processor.HandleAsync(Command(Commands.SetLampToken, new { deviceId = "470124183", name = "Desk", token }))).Ok);
        Assert.Equal(2, store.Load().Lamps!.Count); Assert.Equal(2, saved.Count);
        var discovery = await processor.HandleAsync(new(2, Commands.DiscoverLamps));
        Assert.All(discovery.Data!.Value.EnumerateArray(), d => Assert.True(d.GetProperty("alreadyConfigured").GetBoolean()));
        Assert.True((await processor.HandleAsync(Command(Commands.SetLamp, new { lamp = "lamp:470124183", on = false, brightness = 35, follow = "map", color = "#112233", name = "Desk renamed" }))).Ok);
        Assert.True(processor.Settings.Lamps![0].On); Assert.False(processor.Settings.Lamps[1].On);
        Assert.Equal("Desk renamed", processor.Settings.Lamps[1].Name);
        Assert.True((await processor.HandleAsync(Command(Commands.SetLamp, new { on = false, brightness = 20, follow = "own", color = "#223344" }))).Ok);
        Assert.False(processor.Settings.Lamp.On);
        Assert.DoesNotContain(token, JsonSerializer.Serialize(new { settings = processor.Settings, discovery }, PipeContract.Json));
        Assert.True((await processor.HandleAsync(Command(Commands.RemoveLamp, new { lamp = "lamp:470124183" }))).Ok);
        Assert.Single(processor.Settings.Lamps!); Assert.Single(saved);
    }
    [Theory]
    [InlineData("-1")] [InlineData("0")] [InlineData("4294967296")] [InlineData("0xGG")] [InlineData("1.5")]
    public async Task InvalidIdsDoNotWriteOrMutate(string deviceId)
    {
        var processor = new CommandProcessor(new MemorySettingsStore(), new Runtime(), saveDeviceToken: (_, _) => throw new Exception("Unexpected write"));
        var before = processor.Settings;
        Assert.False((await processor.HandleAsync(Command(Commands.SetLampToken, new { deviceId, token = new string('a', 32) }))).Ok);
        Assert.Same(before, processor.Settings);
    }
    [Fact]
    public async Task ResolutionCachesHostWithoutChangingStableIdentityOrOtherLamp()
    {
        var store = new MemorySettingsStore(); store.Save(new() { Lamps = [new(DeviceId: 10, Id: "lamp:10"), new(DeviceId: 20, Id: "lamp:20")] });
        var runtime = new Runtime(); var processor = new CommandProcessor(store, runtime);
        await runtime.Resolved!("lamp:10", new("192.0.2.99", 10, 2));
        Assert.Equal("192.0.2.99", processor.Settings.Lamps![0].LastHost); Assert.Null(processor.Settings.Lamps[1].LastHost);
        await runtime.Resolved!("lamp:10", new("192.0.2.100", 20, 3));
        Assert.Equal("192.0.2.99", processor.Settings.Lamps[0].LastHost);
    }
    private sealed class Protector : ILampSecretProtector
    {
        public byte[] Protect(byte[] value) => value.Select(b => (byte)(b ^ 0x55)).ToArray();
        public byte[] Unprotect(byte[] value) => Protect(value);
    }
    [Fact]
    public async Task LegacyMigrationMovesEncryptedTokenOnlyAfterIdentityIsLearnedAndPreservesSettings()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "migration-" + Guid.NewGuid());
        var secrets = new LampTokenStore(path, new Protector());
        try
        {
            secrets.Save(Enumerable.Repeat((byte)8, 16).ToArray());
            var store = new MemorySettingsStore(); store.Save(new() { Lamp = new(Host: "192.0.2.8", Brightness: 35, Color: "#112233") });
            var runtime = new Runtime(); var processor = new CommandProcessor(store, runtime, lampTokens: secrets);
            Assert.Null(processor.Settings.Lamps); Assert.Equal("unresolved", processor.Settings.ConfiguredLamps[0].Id);
            await runtime.Resolved!("unresolved", new("192.0.2.8", 42, 2));
            var migrated = Assert.Single(processor.Settings.Lamps!);
            Assert.Equal("lamp:42", migrated.Id); Assert.Equal(35, migrated.Brightness); Assert.Equal("#112233", migrated.Color);
            Assert.Equal("192.0.2.8", migrated.LastHost); Assert.All(secrets.Load(42)!, b => Assert.Equal(8, b)); Assert.Null(secrets.Load());
        }
        finally { foreach (var file in new[] { path, path + ".42" }) if (File.Exists(file)) File.Delete(file); }
    }
    [Fact]
    public async Task LearnedLegacyIdentityMergesWithProvisionedLampWithoutOverwritingItsToken()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "merge-" + Guid.NewGuid());
        var secrets = new LampTokenStore(path, new Protector());
        try
        {
            secrets.Save(new byte[16]); secrets.Save(42, Enumerable.Repeat((byte)9, 16).ToArray());
            var store = new MemorySettingsStore(); store.Save(new() { Lamps = [new(Host: "192.0.2.8"), new(Id: "lamp:42", DeviceId: 42, Name: "Room")] });
            var runtime = new Runtime(); var processor = new CommandProcessor(store, runtime, lampTokens: secrets);
            await runtime.Resolved!("unresolved", new("192.0.2.8", 42, 1));
            Assert.Equal("Room", Assert.Single(processor.Settings.Lamps!).Name);
            Assert.All(secrets.Load(42)!, b => Assert.Equal(9, b)); Assert.Null(secrets.Load());
        }
        finally { foreach (var file in new[] { path, path + ".42" }) if (File.Exists(file)) File.Delete(file); }
    }
    private sealed class ManualClock : ControlSpawn.Core.IClock
    {
        public TimeSpan Elapsed { get; private set; }
        public void Advance(TimeSpan elapsed) => Elapsed += elapsed;
        public Task DelayAsync(TimeSpan duration, CancellationToken token) { token.ThrowIfCancellationRequested(); Advance(duration); return Task.CompletedTask; }
    }
    private sealed class DiscoveryTransport : IMiioDiscoveryTransport
    {
        public int Calls;
        public Task<IReadOnlyList<(string Ip, byte[] Packet)>> ScanAsync(byte[] packet, IReadOnlyList<string> targets, TimeSpan duration, CancellationToken token)
        { Calls++; return Task.FromResult<IReadOnlyList<(string, byte[])>>([]); }
    }
    [Fact]
    public async Task SharedDiscoveryScansAtMostOnceEveryThirtySeconds()
    {
        var transport = new DiscoveryTransport(); var clock = new ManualClock();
        await using var runtime = new ServiceRuntime(true, new FakeSensorSource(), new FakeFanControlPort(), new FakeHealthProbe(), new FakeRgbBackend(), null, new FakeRouterBackend(),
            miioDiscovery: new MiioDiscovery(transport, () => ["192.0.2.1"]), clock: clock);
        await Task.WhenAll(runtime.DiscoverLampsAsync(default), runtime.DiscoverLampsAsync(default)); Assert.Equal(1, transport.Calls);
        clock.Advance(TimeSpan.FromSeconds(29)); await runtime.DiscoverLampsAsync(default); Assert.Equal(1, transport.Calls);
        clock.Advance(TimeSpan.FromSeconds(1)); await runtime.DiscoverLampsAsync(default); Assert.Equal(2, transport.Calls);
    }
    [Fact]
    public async Task UnresolvedLegacyHostSurvivesAddingAnotherDeviceAndNumericIdsRoundTrip()
    {
        var store = new MemorySettingsStore(); store.Save(new() { Lamp = new(Host: "192.0.2.8", Brightness: 37) });
        var processor = new CommandProcessor(store, new Runtime(), saveDeviceToken: (_, _) => { });
        Assert.True((await processor.HandleAsync(Command(Commands.SetLampToken, new { deviceId = 470124183u, name = "Desk", token = new string('a', 32) }))).Ok);
        Assert.Equal(2, processor.Settings.Lamps!.Count); Assert.Equal("unresolved", processor.Settings.Lamps[0].Id);
        var restored = JsonSerializer.Deserialize<ServiceSettings>(JsonSerializer.Serialize(processor.Settings, PipeContract.Json), PipeContract.Json)!;
        restored.Validate(); Assert.Equal(37, restored.Lamps![0].Brightness); Assert.Equal(470124183u, restored.Lamps[1].DeviceId);
    }
    [Fact]
    public async Task MapPointsAndScenesPreserveIndependentIdentityAndRemovalPromotesNextLamp()
    {
        var store = new MemorySettingsStore(); store.Save(new() { Lamps = [new(DeviceId: 10, Id: "lamp:10"), new(DeviceId: 20, Id: "lamp:20")] });
        var processor = new CommandProcessor(store, new Runtime(), removeDeviceToken: _ => { });
        var map = new LightingMap([], Lamps: new Dictionary<string, MapPoint> { ["lamp:10"] = new(0.2, 0.3), ["lamp:20"] = new(0.7, 0.8) });
        Assert.True((await processor.HandleAsync(Command(Commands.SetLightingMap, new { map }))).Ok);
        Assert.Equal(new MapPoint(0.2, 0.3), processor.Settings.Lamps![0].MapPoint);
        Assert.Equal(new MapPoint(0.7, 0.8), CaseMap.LampPoint(processor.Settings.Map, [], "lamp:20", 1));
        Assert.True((await processor.HandleAsync(Command(Commands.ApplyScene, new { name = "Night" }))).Ok);
        Assert.All(processor.Settings.Lamps!, l => Assert.False(l.On)); Assert.Equal("lamp:10", processor.Settings.Lamps![0].Id);
        Assert.True((await processor.HandleAsync(Command(Commands.RemoveLamp, new { lamp = "lamp:10" }))).Ok);
        Assert.Equal("lamp:20", processor.Settings.Lamp.Id); Assert.False(processor.Settings.Map.Lamps!.ContainsKey("lamp:10"));
        Assert.True((await processor.HandleAsync(Command(Commands.SetLamp, new { on = true }))).Ok); Assert.True(processor.Settings.Lamps![0].On);
    }
    private sealed class FailedLamp : ControlSpawn.Core.ILampClient
    {
        public bool IsMusicMode => false;
        public Task SetPowerAsync(bool on, CancellationToken cancellationToken = default) => throw new IOException("Fixture failure");
        public Task SetColorAsync(ControlSpawn.Core.RgbColor color, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
    [Fact]
    public async Task OneFailedLampDoesNotStopSecondWorkerAndLegacyStateUsesFirst()
    {
        await using var runtime = new ServiceRuntime(true, new FakeSensorSource(), new FakeFanControlPort(), new FakeHealthProbe(), new FakeRgbBackend(), new FailedLamp(), new FakeRouterBackend());
        var settings = new ServiceSettings { Lamps = [new(Id: "one", Name: "One"), new(Id: "two", Name: "Two")] };
        await runtime.ApplyAsync(settings, default); using var stop = new CancellationTokenSource(); var run = runtime.RunAsync(stop.Token);
        var state = runtime.GetState(settings);
        Assert.False(state.Lamps![0].Connected); Assert.True(state.Lamps[1].Connected); Assert.Equal(state.Lamps[0], state.Lamp);
        stop.Cancel(); await run;
    }

    private sealed class GarbledLamp : ControlSpawn.Core.ILampClient
    {
        public bool IsMusicMode => false;
        public Task SetPowerAsync(bool on, CancellationToken cancellationToken = default) => throw new FormatException("Garbled reply");
        public Task SetColorAsync(ControlSpawn.Core.RgbColor color, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
    // #14: a lamp that answers nonsense is offline, not a crashed worker; a removed lamp's worker just stops.
    [Fact]
    public async Task GarbledLampRepliesShowOfflineWithoutFailingTheLampModule()
    {
        await using var runtime = new ServiceRuntime(true, new FakeSensorSource(), new FakeFanControlPort(), new FakeHealthProbe(), new FakeRgbBackend(), new GarbledLamp(), new FakeRouterBackend());
        var settings = new ServiceSettings { Lamps = [new(Id: "one", Name: "One"), new(Id: "two", Name: "Two")] };
        await runtime.ApplyAsync(settings, default); using var stop = new CancellationTokenSource(); var run = runtime.RunAsync(stop.Token);
        var state = runtime.GetState(settings);
        Assert.False(state.Lamps![0].Connected); Assert.Equal("Switched off or out of Wi-Fi range", state.Lamps[0].Error);
        Assert.True(state.Lamps[1].Connected);
        Assert.DoesNotContain(state.Health.Warnings, w => w.Contains("module failed", StringComparison.Ordinal));
        var fewer = settings with { Lamps = [settings.Lamps![1]] };
        await runtime.ApplyAsync(fewer, default);
        await Task.Delay(700);   // the lamp module checks for removed lamps every 500 ms
        Assert.DoesNotContain(runtime.GetState(fewer).Health.Warnings, w => w.Contains("module failed", StringComparison.Ordinal));
        stop.Cancel(); await run;
    }
}
