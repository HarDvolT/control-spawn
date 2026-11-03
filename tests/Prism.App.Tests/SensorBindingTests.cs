using Prism.App.Services;
using Prism.Core.Ipc;
using Prism.Lighting;

namespace Prism.App.Tests;
public sealed class SensorBindingTests
{
    [Fact]
    public async Task MockRoundTripsIndependentDeviceZoneAndLampBindingsAndRejectsInvalidDrafts()
    {
        await using var client = new MockPrismClient();
        foreach (var target in new[] { "gpu", "ram", "board/zone/0", "lamp:470875761" })
        {
            var result = await client.SendAsync(Commands.SetSensorBinding, new { target, source = "gpu.power", min = 15, max = 300, style = "Brightness", autoLearn = true });
            Assert.True(result.Ok, result.Error);
        }
        Assert.Equal(4, client.CurrentState.Lighting.Bindings!.Count);
        var before = client.CurrentState.Lighting;
        Assert.False((await client.SendAsync(Commands.SetSensorBinding, new { target = "gpu", source = "madeUp", min = 0, max = 100 })).Ok);
        Assert.Equal(before.Bindings, client.CurrentState.Lighting.Bindings);
        Assert.False((await client.SendAsync(Commands.SetSensorBinding, new { target = "gpu/zone/999", source = "gpu.load", min = 0, max = 100 })).Ok);
        Assert.True((await client.SendAsync(Commands.SetSensorBinding, new { target = "gpu", source = (string?)null })).Ok);
        Assert.Equal(3, client.CurrentState.Lighting.Bindings!.Count);
    }
    [Fact]
    public async Task ZoneOverridesDeviceAndMissingZoneFallsBackWithoutChangingOtherDevices()
    {
        await using var client = new MockPrismClient(); var state = client.CurrentState;
        var device = new RgbDeviceState("fixture", "GPU", "GPU", true, true, 4, [new("Zone", "Linear", 1, 2, 2, 2)]);
        var bindings = new Dictionary<string, SensorBinding> {
            ["fixture"] = new("gpu.load", 0, 100, Palette: [new(0, "#FF0000"), new(1, "#FF0000")], Smoothing: 0),
            ["fixture/zone/0"] = new("cpu.load", 0, 100, Palette: [new(0, "#0000FF"), new(1, "#0000FF")], Smoothing: 0)
        };
        var engine = new SensorLighting(); var normal = Enumerable.Repeat(new Prism.Core.RgbColor(0, 255, 0), 4).ToArray();
        var colors = engine.Composite(device, bindings, state, 0, normal, 1);
        Assert.Equal(new Prism.Core.RgbColor(255, 0, 0), colors[0]); Assert.Equal(new Prism.Core.RgbColor(0, 0, 255), colors[1]);
        state = state with { System = state.System with { CpuLoad = null } };
        colors = engine.Composite(device, bindings, state, 1, normal, 1);
        Assert.Equal(normal[1], colors[1]); Assert.Equal(new Prism.Core.RgbColor(255, 0, 0), colors[3]);
        Assert.Equal(normal, engine.Composite(device with { Id = "other" }, bindings, state, 1, normal, 1));
    }
    [Fact]
    public async Task ThePumpTakesItsOwnSensorColoursApartFromTheRadiator()
    {
        await using var client = new MockPrismClient(); var state = client.CurrentState;
        var board = new RgbDeviceState("board", "Board", "Motherboard", true, true, 50, [new("Logo", "Single", 0, 2, 2, 2), new("Header 1", "Linear", 2, 48, 0, 120)]);
        var pump = CaseMap.Pieces(board, 1)[0].Key;
        Assert.True(SensorSources.ValidTarget(pump, [board], []));
        Assert.False(SensorSources.ValidTarget(SensorSources.RangeTarget("board", 40, 20), [board], []));   // past the last LED
        var bindings = new Dictionary<string, SensorBinding> { [pump] = new("cpu.load", 0, 100, Palette: [new(0, "#0000FF"), new(1, "#0000FF")], Smoothing: 0) };
        var normal = Enumerable.Repeat(new Prism.Core.RgbColor(0, 255, 0), 50).ToArray();
        var colors = new SensorLighting().Composite(board, bindings, state, 0, normal, 1);
        Assert.All(colors.Skip(2).Take(12), c => Assert.Equal(new Prism.Core.RgbColor(0, 0, 255), c));   // the pump ring
        Assert.Equal(normal.Take(2).Concat(normal.Skip(14)), colors.Take(2).Concat(colors.Skip(14)));   // logo and radiator untouched
    }
    [Fact]
    public async Task SourcesHandleOptionalReadingsAndUnitsWithoutInventingValues()
    {
        await using var client = new MockPrismClient(); var state = client.CurrentState;
        foreach (var source in SensorSources.Available(state))
        {
            SensorSources.Validate(new(source.Id, source.Min, source.Max));
            var reading = SensorSources.Read(state, source.Id);
            Assert.True(reading is null || double.IsFinite(reading.Value));
        }
        state = state with { System = state.System with { GpuMemoryJunction = null, VramTotalGb = 0, RamTotalGb = 0, CpuCoreLoads = [], DimmTemps = [], Disks = [] }, Router = null };
        foreach (var id in new[] { "gpu.memoryJunction", "gpu.vram", "ram.use", "cpu.coreMax", "ram.temperature", "disk.temperature", "disk.activity", "router.ping", "router.signal", "fan.255" }) Assert.Null(SensorSources.Read(state, id));
    }
}
