using Prism.App.Services;
using Prism.App.ViewModels;
using Prism.Core.Ipc;

namespace Prism.App.Tests;
public sealed class MultiLampTests
{
    [Fact]
    public async Task SelectedLampCommandsPreserveOtherLampAndDiscoveryFillsIdWithoutExposingToken()
    {
        await using var client = new MockPrismClient();
        var vm = new LampViewModel(client); vm.Apply(client.CurrentState);
        Assert.Equal(2, vm.Lamps.Count);
        vm.SelectedLamp = vm.Lamps[1]; vm.On = false;
        Assert.True(client.CurrentState.Lamps![0].On); Assert.False(client.CurrentState.Lamps[1].On);
        vm.LampName = "Desk edited"; vm.DeviceIdText = "123"; vm.Apply(client.CurrentState);
        Assert.Equal("Desk edited", vm.LampName); Assert.Equal("123", vm.DeviceIdText);
        vm.FindCommand.Execute(null); Assert.Equal(2, vm.Discovered.Count);
        vm.SelectedFound = vm.Discovered[0]; Assert.Equal(vm.Discovered[0].DeviceId.ToString(), vm.DeviceIdText);
        vm.RemoveCommand.Execute(null); Assert.Single(client.CurrentState.Lamps!);
        Assert.Equal(client.CurrentState.Lamps![0], client.CurrentState.Lamp);
    }
    [Fact]
    public async Task RemovingFirstLampPromotesRemainingLampForLegacyCommandsAndSummariesShowBoth()
    {
        await using var client = new MockPrismClient();
        var dashboard = new DashboardViewModel(client); dashboard.Apply(client.CurrentState);
        Assert.Equal(2, dashboard.LampRows.Count); Assert.Equal("Desk lamp", dashboard.LampRows[1].Name);
        await client.SendAsync(Commands.RemoveLamp, new { lamp = "lamp:470875761" });
        Assert.Equal("lamp:470124183", client.CurrentState.Lamp!.Id);
        await client.SetLampAsync(false, 30, "own", "#112233");
        Assert.False(client.CurrentState.Lamp!.On); Assert.Equal(30, client.CurrentState.Lamp.Brightness);
    }
    [Fact]
    public async Task ClickingADashboardLampFlipsOnlyThatLampAndAClickBackTurnsItOn()
    {
        await using var client = new MockPrismClient();
        var dashboard = new DashboardViewModel(client); dashboard.Apply(client.CurrentState);
        Assert.True(dashboard.LampRows[1].On);
        dashboard.ToggleLampRowCommand.Execute(dashboard.LampRows[1]);
        Assert.False(dashboard.LampRows[1].On); // flipped at once, before the service confirms
        Assert.False(client.CurrentState.Lamps![1].On); Assert.True(client.CurrentState.Lamps[0].On);
        dashboard.Apply(client.CurrentState);
        dashboard.ToggleLampRowCommand.Execute(dashboard.LampRows[1]);
        Assert.True(client.CurrentState.Lamps![1].On);
    }
    [Fact]
    public async Task DecimalAndHexProvisioningUpdateExistingLampWithoutDuplicates()
    {
        await using var client = new MockPrismClient();
        Assert.True((await client.SendAsync(Commands.SetLampToken, new { deviceId = "0x1c058697", name = "Desk", token = new string('a', 32) })).Ok);
        Assert.Equal(2, client.CurrentState.Lamps!.Count);
        Assert.Equal("Desk", client.CurrentState.Lamps[1].Name);
        Assert.True((await client.SendAsync(Commands.SetLampToken, new { deviceId = "470124183", name = "Desk two", token = new string('b', 32) })).Ok);
        Assert.Equal(2, client.CurrentState.Lamps!.Count);
        Assert.Equal("Desk two", client.CurrentState.Lamps[1].Name);
    }
}
