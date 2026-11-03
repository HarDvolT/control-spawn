using Prism.App.Services;
using Prism.App.ViewModels;

namespace Prism.App.Tests;

public sealed class FanSourceTests
{
    /// <summary>"Follows" used to wait for Save, so the next refresh quietly put the old source back (live, 2026-11-03).</summary>
    [Fact]
    public void PickingWhatAFanFollowsAppliesAtOnceAndSurvivesTheNextRefresh()
    {
        var client = new MockPrismClient();
        var model = new FansViewModel(client); model.Apply(client.CurrentState);
        model.SelectedPrimarySource = "Hottest";
        Assert.Contains(client.CurrentState.Fans, f => f.Configuration?.Source == Prism.Core.TemperatureSource.Maximum);
        model.Apply(client.CurrentState); model.Apply(client.CurrentState);
        Assert.Equal("Hottest", model.SelectedPrimarySource);
        Assert.False(model.HasUnsavedChanges);   // nothing left waiting for Save
    }
}

public sealed class LampWhiteTileTests
{
    /// <summary>A white or colour picked on a lamp following the PC used to change nothing (2026-11-03).</summary>
    [Fact]
    public async Task PickingAWhiteOrAColourMakesItTheLampsOwnLight()
    {
        var client = new MockPrismClient();
        await client.SendAsync(Prism.Core.Ipc.Commands.SetLamp, new { lamp = "lamp:470875761", follow = "pc" });
        var tile = new LampTileViewModel(client, "lamp:470875761"); tile.Apply(client.CurrentState);
        Prism.Core.Ipc.LampState Lamp() => client.CurrentState.Lamps!.First(l => l.Id == "lamp:470875761");
        tile.WhiteKelvin = 2730;   // the slider snaps to 100 K
        await Until(() => Lamp().Kelvin == 2700);
        Assert.Equal("own", Lamp().Follow);
        tile.Apply(client.CurrentState);
        Assert.Equal(("2700 K", 2700d), (tile.WhiteText, tile.WhiteKelvin));
        tile.ColorEntry = "#36D6FF";
        await Until(() => Lamp().Color == "#36D6FF");
        Assert.Equal(("own", (int?)null), (Lamp().Follow, Lamp().Kelvin));
    }

    private static async Task Until(Func<bool> done)
    {
        for (var until = DateTime.UtcNow.AddSeconds(5); !done() && DateTime.UtcNow < until;) await Task.Delay(10);
        Assert.True(done());
    }
}
