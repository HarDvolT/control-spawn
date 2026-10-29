using Prism.App.Navigation;
using Prism.App.Services;
using Prism.App.ViewModels;
using Prism.Core.Ipc;

namespace Prism.App.Tests;

public sealed class LedEditorTests
{
    [Fact]
    public async Task RealisticMockZonesResizeAndPadLayersWithoutHardware()
    {
        await using var client = new MockPrismClient();
        var devices = client.CurrentState.Lighting.Devices;
        Assert.Equal(new[] { 10, 10 }, devices.Where(d => d.Kind == "RAM").Select(d => d.LedCount));
        Assert.Equal(4, devices.Single(d => d.Kind == "GPU").LedCount);
        var board = devices.Single(d => d.Id == "board"); Assert.Equal(0, board.Zones![1].LedCount); Assert.Equal(120, board.Zones![1].MaxLeds);
        Assert.True((await client.SendAsync(Commands.SetLedColors, new { device = "board", layer = "overlay", colors = new[] { "#FF000080", "#00000000" } })).Ok);
        Assert.True((await client.SendAsync(Commands.ResizeZone, new { device = "board", zone = 1, size = 30 })).Ok);
        board = client.CurrentState.Lighting.Devices.Single(d => d.Id == "board");
        Assert.Equal(32, board.LedCount); Assert.Equal(32, board.OverlayColors!.Count); Assert.Equal("#00000000", board.OverlayColors[^1]);
        Assert.False((await client.SendAsync(Commands.ResizeZone, new { device = "board", zone = 1, size = 121 })).Ok);
    }
    [Fact]
    public async Task BrushDebouncesAndGradientEraserAndPresetRoundTrip()
    {
        var path = Path.Combine(Path.GetTempPath(), "prism-led-" + Guid.NewGuid() + ".json");
        try
        {
            await using var client = new MockPrismClient(); var editor = new LedEditorViewModel(client, new(path)); editor.Apply(client.CurrentState);
            editor.Color = "#FF0000"; editor.SecondColor = "#0000FF";
            editor.Paint(Enumerable.Range(0, 10), gradient: true);
            Assert.Equal("#FF0000", editor.Colors[0]); Assert.Equal("#0000FF", editor.Colors[9]);
            editor.SavePreset("Gradient");
            editor.Paint([0], erase: true); Assert.Equal("#000000", editor.Colors[0]);
            editor.LoadPreset("Gradient"); Assert.Equal("#FF0000", editor.Colors[0]);
            await Task.Delay(180);
            Assert.Equal("#FF0000", client.CurrentState.Lighting.Devices.Single(d => d.Id == "ram").CustomColors![0]);
            editor.Layer = "overlay"; editor.Paint([0]); editor.Paint([0], erase: true);
            await Task.Delay(180);
            Assert.Equal("#00000000", client.CurrentState.Lighting.Devices.Single(d => d.Id == "ram").OverlayColors![0]);
            await editor.ClearAsync(); Assert.All(editor.Colors, c => Assert.Equal("#00000000", c));
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }
    [Fact]
    public void EditLedsSearchNavigatesToEditor()
    {
        var result = new CommandSearch().Search("Edit LEDs").First();
        Assert.Equal("Edit LEDs", result.Title); Assert.Equal(SearchAction.Navigate, result.Action); Assert.Equal(PageKey.Lighting.ToString(), result.Key);
    }
    [Fact]
    public async Task ResizeDoesNotDropSelectedDeviceOrDraftPrefix()
    {
        await using var client = new MockPrismClient(); var editor = new LedEditorViewModel(client);
        editor.Apply(client.CurrentState); editor.Device = editor.Devices.Single(d => d.Id == "board");
        editor.Color = "#112233"; editor.Paint([0, 1]);
        await Task.Delay(180);
        await editor.ResizeAsync(1, 30);
        Assert.Equal("board", editor.Device!.Id); Assert.Equal(32, editor.Colors.Length);
        Assert.Equal("#112233", editor.Colors[0]); Assert.Equal("#000000", editor.Colors[^1]);
        editor.Paint([31]); // Resize must flush this debounced edit before changing the count.
        await editor.ResizeAsync(1, 0); Assert.Equal(2, editor.Colors.Length);
        await editor.ResizeAsync(1, 30);
        Assert.Equal("#000000", editor.Colors[^1]); // Removed LEDs must not reappear from a stale draft.
    }
}
