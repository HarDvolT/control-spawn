using Prism.App.Navigation;
using Prism.App.Services;
using Prism.App.ViewModels;
using Prism.App.Widgets;
using Prism.Core.Ipc;
using Xunit;

namespace Prism.App.Tests;

public sealed class DevicesAndApplianceTests
{
    [Fact]
    public async Task DuplicateFanDisplayNamesRetainDistinctHeaderSelectionAndPumpSafety()
    {
        await using var client = new MockPrismClient(); var vm = new FansViewModel(client);
        var state = client.CurrentState with { Fans = client.CurrentState.Fans.Select(f => f with { Name = "Cooling" }).ToArray() };
        vm.Apply(state); vm.SelectFanCommand.Execute(vm.Fans.Single(f => f.Id == "fan:6"));
        Assert.Equal("fan:6", vm.SelectedFanId); Assert.False(vm.IsPumpSelected); Assert.Single(vm.Fans.Where(f => f.IsSelected));
        vm.Apply(state); Assert.Equal("fan:6", vm.SelectedFanId);
        vm.SelectFanCommand.Execute(vm.Fans.Single(f => f.Id == "fan:7")); Assert.True(vm.IsPumpSelected); Assert.Equal("fan:7", vm.SelectedFanId);
    }
    [Fact]
    public async Task MockDiscoveryRenameResetAndDismissAreReflectedAcrossViews()
    {
        await using var client = new MockPrismClient();
        Assert.True((await client.SendAsync(Commands.FindDevices)).Ok);
        var device = client.CurrentState.Devices!.First(d => d.Kind == DeviceKind.RgbController);
        Assert.True((await client.SendAsync(Commands.RenameDevice, new { id = device.Id, name = "  My memory  " })).Ok);
        Assert.Equal("My memory", client.CurrentState.Lighting.Devices.First(d => d.Id == device.Id).Name);
        Assert.True((await client.SendAsync(Commands.RenameDevice, new { id = device.Id, reset = true })).Ok);
        Assert.Equal(device.Name, client.CurrentState.Lighting.Devices.First(d => d.Id == device.Id).Name);
        var pending = client.CurrentState.NewDevices!.First();
        Assert.True((await client.SendAsync(Commands.DismissDevice, new { id = pending.Id })).Ok);
        await client.SendAsync(Commands.FindDevices);
        Assert.DoesNotContain(client.CurrentState.NewDevices!, d => d.Id == pending.Id);
        Assert.Contains(Pages.All, p => p.Key == PageKey.Devices && p.Group == "GENERAL");
    }
    [Fact]
    public async Task MockAndAppRequireConfirmationForStartAndWidgetOnlyCancels()
    {
        await using var client = new MockPrismClient(); var vm = new AppliancesViewModel(client); vm.Apply(client.CurrentState);
        var fryer = Assert.Single(vm.Appliances); var start = fryer.Actions.First(a => a.Name == "start-cook");
        Assert.False((await client.SendAsync(Commands.ApplianceAction, new { id = fryer.Id, siid = start.Siid, aiid = start.Aiid })).Ok);
        var nonce = await vm.PrepareAsync(fryer, start); Assert.NotNull(nonce);
        Assert.Equal("idle", Assert.Single(client.CurrentState.Appliances!).Status);
        await vm.ConfirmAsync(fryer, start, nonce!); Assert.False(vm.HasError);
        Assert.Equal("cooking", Assert.Single(client.CurrentState.Appliances!).Status);
        await vm.ConfirmAsync(fryer, start, nonce!); Assert.True(vm.HasError);
        var widget = new WidgetViewModel(new WidgetLayout { Kind = WidgetKind.AirFryer }, client); widget.Apply(client.CurrentState);
        widget.CancelCookingCommand.Execute(widget.Appliances[0]);
        Assert.Equal("idle", Assert.Single(client.CurrentState.Appliances!).Status);
        Assert.DoesNotContain(new CommandSearch().Search("start", 100), r => r.Key.Contains("cook", StringComparison.OrdinalIgnoreCase));
    }
    [Fact]
    public void CommandsFindDevicesAndImportTokensAreSearchable()
    {
        var search = new CommandSearch();
        Assert.Equal(SearchAction.FindDevices, search.Search("Find devices")[0].Action);
        Assert.Equal(SearchAction.ImportTokens, search.Search("Import tokens")[0].Action);
    }
}
