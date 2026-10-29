using System.Text.Json;
using Prism.Core;
using Prism.Core.Ipc;
using Prism.Lighting;
using Prism.Router;
using Prism.Hardware;
using Xunit;

namespace Prism.Service.Tests;

public sealed class DeviceRegistryTests
{
    private static DeviceState Device(string id = "lamp:123", string source = "miio", string? mac = null) =>
        new(id, DeviceKind.Lamp, "Detected lamp", source, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, DeviceStatus.NeedsToken, [], DeviceId: source == "miio" ? 123u : null, Mac: mac);
    [Fact]
    public void SameMacMergesSourcesWithoutLosingNameDismissalOrFirstSeen()
    {
        var first = DeviceRegistry.Observe([], [Device(mac: "28:6C:07:00:00:01")], DateTimeOffset.UnixEpoch);
        first = [DeviceRegistry.Rename(first[0], "  Desk  ") with { Dismissed = true }];
        var merged = DeviceRegistry.Observe(first, [Device("network:1", "router", "28-6c-07-00-00-01") with { Kind = DeviceKind.NetworkDevice }], DateTimeOffset.UnixEpoch.AddSeconds(60));
        var d = Assert.Single(merged); Assert.Equal("lamp:123", d.Id); Assert.Equal(DeviceKind.Lamp, d.Kind);
        Assert.Equal("Desk", d.Name); Assert.True(d.Dismissed); Assert.Empty(DeviceRegistry.NewDevices(merged));
        Assert.Equal(DateTimeOffset.UnixEpoch, d.FirstSeen); Assert.Equal(DateTimeOffset.UnixEpoch.AddSeconds(60), d.LastSeen);
        Assert.Contains("router", d.Sources!);
        Assert.Equal("Detected lamp", DeviceRegistry.Rename(d, null, true).Name);
    }
    [Fact]
    public void OfflineRequiresACompletedSourceAndReturnsOnlineWithoutResurfacingDismissedDevices()
    {
        var start = DateTimeOffset.UnixEpoch; var known = DeviceRegistry.Observe([], [Device() with { Dismissed = true }], start);
        Assert.Equal(DeviceStatus.NeedsToken, DeviceRegistry.Observe(known, [], start.AddSeconds(151), ["router"])[0].Status);
        var offline = DeviceRegistry.Observe(known, [], start.AddSeconds(151), ["miio"]);
        Assert.Equal(DeviceStatus.Offline, offline[0].Status);
        var online = DeviceRegistry.Observe(offline, [Device()], start.AddSeconds(160), ["miio"]);
        Assert.Equal(DeviceStatus.NeedsToken, online[0].Status); Assert.Empty(DeviceRegistry.NewDevices(online));
    }
    [Theory]
    [InlineData(0, false)] [InlineData(-1, false)] [InlineData(double.NaN, false)] [InlineData(0.1, true)] [InlineData(600, true)]
    public void NewFanThresholdIsStrictlyPositiveAndFinite(double rpm, bool expected)
    {
        var sensors = new[] { new SensorReading("/board/fan/0", "fixture", SensorCategory.Motherboard, "Fan", "RPM", rpm) };
        Assert.Equal(expected, FanMap.Discover(sensors, [1]).Any(f => f.Header == 1 && f.Rpm > 0 && double.IsFinite(f.Rpm)));
    }
    private sealed class Runtime : IServiceRuntime
    {
        public Func<IReadOnlyList<DeviceState>, IReadOnlyList<string>, Task>? Observed;
        public void SetDeviceObservationHandler(Func<IReadOnlyList<DeviceState>, IReadOnlyList<string>, Task> handler) => Observed = handler;
        public StateMessage GetState(ServiceSettings s) => StateAssembler.Assemble(new(DateTimeOffset.UnixEpoch, []), s, [], [new("rgb:1", "Detected RGB", "RAM", true, true, 10)], null, null, new(true, false, [], false));
        public Task ApplyAsync(ServiceSettings s, CancellationToken t) => Task.CompletedTask;
        public Task<SpeedTestResult?> StartSpeedTestAsync(CancellationToken t) => Task.FromResult<SpeedTestResult?>(null);
    }
    private static Request Cmd(string command, object args) => new(1, command, JsonSerializer.SerializeToElement(args, PipeContract.Json));
    [Fact]
    public async Task RegistryPersistsRenameDismissalAutoJoinAndNewFanCurve()
    {
        var runtime = new Runtime(); var store = new MemorySettingsStore();
        store.Save(new() { Discovery = new(AutoJoinNewRgb: false) }); var processor = new CommandProcessor(store, runtime);
        await runtime.Observed!([Device(), Device("rgb:1", "openrgb") with { DeviceId = null, Kind = DeviceKind.RgbController, Status = DeviceStatus.Ready }, Device("fan:1", "lhm") with { DeviceId = null, Kind = DeviceKind.FanHeader, Status = DeviceStatus.Ready }], ["miio", "openrgb", "lhm"]);
        Assert.False(processor.Settings.SyncedDevices["rgb:1"]); Assert.Equal(FanMode.Standard, processor.Settings.HeaderFans[1].Mode);
        Assert.True((await processor.HandleAsync(Cmd(Commands.RenameDevice, new { id = "rgb:1", name = " Memory " }))).Ok);
        Assert.Equal("Memory", processor.GetState().Lighting.Devices[0].Name);
        Assert.True((await processor.HandleAsync(Cmd(Commands.DismissDevice, new { id = "lamp:123" }))).Ok);
        var reopened = new CommandProcessor(store, new Runtime()); Assert.DoesNotContain(reopened.GetState().NewDevices!, d => d.Id == "lamp:123");
        Assert.True((await processor.HandleAsync(Cmd(Commands.SetupDevice, new { id = "rgb:1" }))).Ok);
        Assert.True(processor.Settings.SyncedDevices["rgb:1"]); Assert.Contains(processor.Settings.Map.Devices, d => d.Device == "rgb:1");
        Assert.True((await processor.HandleAsync(Cmd(Commands.SetFanMode, new { group = "fan:1", mode = "Turbo" }))).Ok);
        Assert.Equal(FanMode.Turbo, processor.Settings.HeaderFans[1].Mode);
        Assert.False((await processor.HandleAsync(Cmd(Commands.SetFanMode, new { group = "fan:7", mode = "Silent" }))).Ok);
    }
    [Theory]
    [InlineData("")] [InlineData("  ")] [InlineData("01234567890123456789012345678901234567890x")]
    public async Task InvalidRenameDoesNotPersist(string name)
    {
        var store = new MemorySettingsStore(); store.Save(new() { Devices = [Device()] }); var processor = new CommandProcessor(store, new Runtime());
        var before = store.Load(); Assert.False((await processor.HandleAsync(Cmd(Commands.RenameDevice, new { id = "lamp:123", name }))).Ok); Assert.Same(before, store.Load());
    }
    [Fact]
    public async Task ImportOnlyTicksMatchingDiscoveredIdsAndNeverPersistsOrEchoesSecret()
    {
        const string secret = "0123456789abcdef0123456789abcdef";
        var runtime = new Runtime(); var store = new MemorySettingsStore(); var saved = new List<uint>();
        var processor = new CommandProcessor(store, runtime, saveDeviceToken: (id, _) => saved.Add(id));
        await runtime.Observed!([Device("lamp:470481367") with { DeviceId = 470481367, Ip = "192.0.2.44" }], ["miio"]);
        var text = JsonSerializer.Serialize(new[] { new { id = 470481367, name = "Room", ip = "192.0.2.1", token = secret, model = "yeelink.light.color5" }, new { id = 470481368, name = "Desk", ip = "192.0.2.2", token = secret, model = "yeelink.light.color5" } });
        var response = await processor.HandleAsync(Cmd(Commands.ImportTokens, new { text, selected = new[] { 470481367 } }));
        Assert.True(response.Ok); Assert.Equal(new uint[] { 470481367 }, saved);
        var lamp = Assert.Single(processor.Settings.Lamps!); Assert.Equal("192.0.2.44", lamp.LastHost);
        Assert.DoesNotContain(secret, JsonSerializer.Serialize(new { state = processor.GetState(), settings = processor.Settings, response }, PipeContract.Json));
        Assert.DoesNotContain(processor.GetState().NewDevices!, d => d.DeviceId == 470481367);
    }
}
