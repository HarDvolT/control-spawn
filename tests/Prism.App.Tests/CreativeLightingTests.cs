using Prism.App.Services;
using Prism.App.ViewModels;
using Prism.Core;
using Prism.Core.Ipc;

namespace Prism.App.Tests;

public sealed class CreativeLightingTests
{
    [Fact]
    public async Task GalleryContainsEveryEffectAndSceneStoresEffectWithParameters()
    {
        await using var client = new MockPrismClient(); var vm = new LightingViewModel(client);
        vm.Apply(client.CurrentState);
        Assert.Equal(Enum.GetValues<LightingEffect>().Length, vm.Effects.Count);
        var parameters = new EffectParameters { Palette = "Ocean", Angle = 220, Scale = 2, Stops = [new(0, "#113355"), new(1, "#AAEEFF")] };
        await vm.SetParametersAsync(parameters);
        vm.Apply(client.CurrentState);
        Assert.Equal(220, vm.Parameters.Angle);
        await client.SetLightingAsync(LightingEffect.OceanRipple, "#112233", 0.5, 1);
        vm.SceneName = "Night"; await vm.SaveSceneAsync();
        await client.SetLightingAsync(LightingEffect.Off, "#000000", 1, 1);
        await client.SendAsync(Commands.SetEffectParams, new { parameters = new EffectParameters() });
        Assert.True((await client.ApplySceneAsync("Night")).Ok);
        Assert.Equal(LightingEffect.OceanRipple, client.CurrentState.Lighting.Effect);
        Assert.Equal(220, client.CurrentState.Lighting.Parameters!.Angle);
        Assert.Equal(2, client.CurrentState.Lighting.Parameters.Stops!.Count);
    }
    [Fact]
    public async Task MapAndEventsRoundTripAndInvalidParamsLeaveMockUnchanged()
    {
        await using var client = new MockPrismClient(); var vm = new LightingViewModel(client); vm.Apply(client.CurrentState);
        var map = new LightingMap([new("ram", 0.6, 0.4, 0.3, 270, true)], new(0.2, 0.4));
        await vm.SetMapAsync(map); await vm.SetEventsAsync(new(PingSpike: false, Notification: false));
        Assert.Equal(new(0.2, 0.4), client.CurrentState.Lighting.Map!.Lamp); Assert.False(client.CurrentState.Lighting.Events!.PingSpike);
        var before = client.CurrentState.Lighting.Parameters;
        Assert.False((await client.SendAsync(Commands.SetEffectParams, new { parameters = new { stops = new[] { new { position = 0.2, color = "red" } } } })).Ok);
        Assert.Same(before, client.CurrentState.Lighting.Parameters);
        Assert.False((await client.SendAsync(Commands.SetLightingMap, new { map = new { devices = new[] { new { device = "ram", x = 2, y = 0 } } } })).Ok);
    }
    [Fact]
    public async Task PickingAColourForAFollowingPartOnlyLoadsTheBrush()
    {
        await using var client = new MockPrismClient(); var vm = new LightingViewModel(client); vm.Apply(client.CurrentState);
        vm.PartTarget = "ram";
        vm.SelectColorCommand.Execute("#36D6FF");
        await Task.Delay(50);
        Assert.Empty(client.CurrentState.Lighting.PartLooks ?? new Dictionary<string, PartLook>());   // the part still follows the case
        vm.Apply(client.CurrentState);
        Assert.Equal("#36D6FF", vm.ColorHex);   // and the brush survives the next state push
        await vm.SetPartLookAsync("ram"); vm.Apply(client.CurrentState);   // once it has its own look, colours change that look
        vm.SelectColorCommand.Execute("#53E08E");
        await Task.Delay(50);
        Assert.Equal("#53E08E", client.CurrentState.Lighting.PartLooks!["ram"].Color);
    }
    [Fact]
    public async Task MockProjectsExactLayerActivationSeparatelyFromSavedColors()
    {
        await using var client = new MockPrismClient();
        await client.SendAsync(Commands.SetLedColors, new { device = "ram", layer = "custom", colors = Enumerable.Repeat("#FF0000", 10) });
        Assert.True(client.CurrentState.Lighting.Devices.First(d => d.Id == "ram").CustomEnabled);
        await client.SendAsync(Commands.SetLedColors, new { device = "ram", layer = "overlay", colors = Enumerable.Repeat("#00000000", 10) });
        Assert.False(client.CurrentState.Lighting.Devices.First(d => d.Id == "ram").CustomEnabled);
        Assert.NotNull(client.CurrentState.Lighting.Devices.First(d => d.Id == "ram").CustomColors);
    }
}
