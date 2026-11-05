using ControlSpawn.App.Common;
using ControlSpawn.App.Services;
using ControlSpawn.App.ViewModels;
using ControlSpawn.Core.Ipc;

namespace ControlSpawn.App.Tests;

[CollectionDefinition("Display preferences", DisableParallelization = true)]
public sealed class DisplayPreferencesCollection { }

[Collection("Display preferences")]
public sealed class SettingsAndTelemetryTests
{
    [Fact]
    public async Task General_preferences_persist_convert_display_and_reset_without_hardware_commands()
    {
        var folder = Path.Combine(Path.GetTempPath(), "ControlSpawnSettings", Guid.NewGuid().ToString("N"));
        await using var client = new MockControlSpawnClient();
        try
        {
            var store = new SettingsStore(Path.Combine(folder, "settings.json"));
            var vm = new SettingsViewModel(client, store, new InMemoryStartupRegistry(), new());
            vm.TemperatureUnit = "°F";
            vm.SetAccentCommand.Execute("#4D6BFF");
            vm.NotifyRunningHot = false;
            vm.NotifyDeviceOffline = false;
            vm.NotifyCookingFinished = false;
            vm.CheckForUpdates = true;
            Assert.Equal("77.0 °F", Format.TempUnit(25));
            Assert.Equal("—", Format.TempUnit(null));
            var saved = store.Load();
            Assert.Equal("°F", saved.TemperatureUnit);
            Assert.Equal("#4D6BFF", saved.AccentColor);
            Assert.False(saved.NotifyRunningHot);
            Assert.False(saved.NotifyDeviceOffline);
            Assert.False(saved.NotifyCookingFinished);
            Assert.True(saved.CheckForUpdates);
            Assert.Equal(48, client.CurrentState.System.CpuTemp); // display preferences never alter telemetry
            vm.ResetCommand.Execute(null);
            Assert.Equal("25.0 °C", Format.TempUnit(25));
            Assert.True(store.Load().NotifyRunningHot);
            Assert.Equal("#FF4D1A", store.Load().AccentColor);
        }
        finally
        {
            DisplayPreferences.Apply(new());
            if (Directory.Exists(folder)) Directory.Delete(folder, true);
        }
    }

    [Fact]
    public async Task Internet_projects_real_cells_clients_neighbors_and_service_history_without_duplicates()
    {
        await using var client = new MockControlSpawnClient();
        var state = client.CurrentState;
        var now = state.Timestamp;
        var device = new DeviceState("network:1", DeviceKind.NetworkDevice, "Measured client", "router", now, now, DeviceStatus.Ready, [], Ip: "192.0.2.5");
        state = state with {
            Devices = [device],
            Router = state.Router! with { Details = new(Nr: new("n78", "412", "cell", "636666", "100 MHz", -72),
                Lte: new("B3", "123", "lte", "1800", null, -60), Model: "Measured model", UptimeSeconds: 90000,
                Neighbors: [new("5G", "636666", "413", -100, 15)]) },
            History = [new(now.AddSeconds(-1), 10, 40, 20, 50, 12, 1, 2, DiskReadMBps: 120, Rsrp5g: -97, Sinr5g: 15),
                new(now, 11, 41, 21, 51, 20, 3, 4, DiskReadMBps: 80, Rsrp5g: -96, Sinr5g: 16)]
        };
        var vm = new ModemViewModel(client);
        vm.Apply(state); vm.Apply(state);
        Assert.Equal("412", vm.PciText);
        Assert.Equal("-72 dBm", vm.RadioMetrics.Last().Value);
        Assert.Equal("Measured client", Assert.Single(vm.Devices).Name);
        Assert.Single(vm.Neighbors);
        Assert.Equal(new double[] { 12, 20 }, vm.PingHistory);
        Assert.Equal(2, vm.Lanes[0].Values.Count);
        Assert.Equal("Measured model", vm.RouterModel);
        vm.SelectedRadio = "LTE anchor";
        Assert.Equal("123", vm.PciText);
        Assert.Equal("-60 dBm", vm.RadioMetrics.Last().Value);
    }

    [Fact]
    public async Task Notifications_follow_preferences_and_state_transitions()
    {
        await using var client = new MockControlSpawnClient();
        var settings = new AppSettings();
        var feed = new NotificationFeed(settings);
        var state = client.CurrentState;
        feed.Observe(state);
        var hot = state with { System = state.System with { CpuTemp = 90 } };
        feed.Observe(hot); feed.Observe(hot);
        Assert.Single(feed.Messages);
        settings.NotifyRunningHot = false;
        feed.Observe(state); feed.Observe(hot);
        Assert.Single(feed.Messages);
        var fryer = new ApplianceState("fryer", "Fryer", "model", true, "cooking", [], []);
        feed.Observe(state with { Appliances = [fryer] });
        feed.Observe(state with { Appliances = [fryer with { Status = "idle", RemainingMinutes = 0 }] });
        Assert.Contains(feed.Messages, message => message.Contains("finished cooking"));
    }
}
