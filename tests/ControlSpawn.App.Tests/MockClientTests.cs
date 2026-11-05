using ControlSpawn.App.Services;
using ControlSpawn.Core;
using ControlSpawn.Core.Ipc;

namespace ControlSpawn.App.Tests;

public class MockClientTests
{
    [Fact]
    public async Task Mock_produces_realistic_hardware_values()
    {
        await using var client = new MockControlSpawnClient();
        var state = client.CurrentState;

        Assert.Equal(16, state.System.CpuCoreLoads.Count);
        Assert.InRange(state.System.CpuLoad ?? -1, 0, 100);
        Assert.InRange(state.System.CpuTemp ?? -1, 30, 90);
        Assert.Equal(32, state.System.RamTotalGb);
        Assert.Equal(16, state.System.VramTotalGb);
        Assert.Equal(2, state.System.Disks.Count);

        var pump = Assert.Single(state.Fans, f => f.IsPump);
        Assert.Equal("AIO pump", pump.Name);
        Assert.Equal(100, pump.Percent);
        Assert.False(pump.Controllable);

        Assert.Contains(state.Fans, f => f.Name == "Radiator" && f.Rpm is > 0);
        Assert.NotNull(state.Router);
        Assert.NotNull(state.Lamp);
        Assert.True(state.Health.Mock);
        Assert.Equal("Smart", state.Lighting.Effect.ToString());
    }

    [Fact]
    public async Task Fan_mode_command_changes_the_state()
    {
        await using var client = new MockControlSpawnClient();
        await client.SetFanModeAsync("Radiator", FanMode.Turbo);

        Assert.Equal(FanMode.Turbo, client.CurrentState.FanMode);
        var radiator = client.CurrentState.Fans.First(f => f.Name == "Radiator");
        Assert.Equal(70, radiator.Percent);
    }

    [Fact]
    public async Task Lighting_command_changes_effect_and_colour()
    {
        await using var client = new MockControlSpawnClient();
        var response = await client.SetLightingAsync(LightingEffect.Static, "#3DDC84", 0.5, 1.5);

        Assert.True(response.Ok);
        Assert.Equal(LightingEffect.Static, client.CurrentState.Lighting.Effect);
        Assert.Equal("#3DDC84", client.CurrentState.Lighting.Color);
        Assert.Equal(0.5, client.CurrentState.Lighting.Brightness, 3);
    }

    [Fact]
    public async Task Device_sync_can_be_switched_off()
    {
        await using var client = new MockControlSpawnClient();
        await client.SetDeviceSyncAsync("ram", false);

        var ram = client.CurrentState.Lighting.Devices.First(d => d.Id == "ram");
        Assert.False(ram.Synced);
    }

    [Fact]
    public async Task Lamp_command_changes_power_and_follow_mode()
    {
        await using var client = new MockControlSpawnClient();
        await client.SetLampAsync(false, 25, "own", "#F5A524");

        var lamp = client.CurrentState.Lamp!;
        Assert.False(lamp.On);
        Assert.Equal(25, lamp.Brightness);
        Assert.Equal("own", lamp.Follow);
        Assert.Equal("#F5A524", lamp.Color);
    }

    [Fact]
    public async Task Applying_the_night_scene_sets_lighting_fans_and_lamp()
    {
        await using var client = new MockControlSpawnClient();
        var response = await client.ApplySceneAsync("Night");

        Assert.True(response.Ok);
        var state = client.CurrentState;
        Assert.Equal(FanMode.Silent, state.FanMode);
        Assert.Equal(LightingEffect.Breathing, state.Lighting.Effect);
        Assert.False(state.Lamp!.On);
        Assert.Equal("Night", client.ActiveScene);
    }

    [Fact]
    public async Task GetState_returns_a_parseable_snapshot()
    {
        await using var client = new MockControlSpawnClient();
        var state = await client.GetStateAsync();

        Assert.NotNull(state);
        Assert.Equal(StateType, state!.Type);
        Assert.NotEmpty(state.Fans);
    }

    [Fact]
    public async Task Unknown_commands_are_rejected()
    {
        await using var client = new MockControlSpawnClient();
        var response = await client.SendAsync("doSomethingDangerous");

        Assert.False(response.Ok);
        Assert.Contains("Unknown command", response.Error);
    }

    [Fact]
    public async Task Starting_connects_and_pushes_state()
    {
        await using var client = new MockControlSpawnClient();
        var received = new TaskCompletionSource<StateMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        client.StateReceived += (_, state) => received.TrySetResult(state);

        await client.StartAsync();

        var pushed = await received.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal(ServiceConnection.Connected, client.Connection);
        Assert.True(client.IsConnected);
        Assert.NotNull(pushed.Router);
    }

    private const string StateType = "state";
}
