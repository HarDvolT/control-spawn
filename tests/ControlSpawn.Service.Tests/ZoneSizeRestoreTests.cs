using ControlSpawn.Core.Ipc;
using Xunit;

namespace ControlSpawn.Service.Tests;

public sealed class ZoneSizeRestoreTests
{
    private static RgbZoneState Zone(int leds, int min = 0, int max = 500) => new("Header", "linear", 0, leds, min, max);
    private static RgbDeviceState Board(params RgbZoneState[] zones) => new("board", "Board", "Mainboard", true, true, 0, zones);

    [Fact]
    public void HeadersAFreshOpenRgbLeftAtZeroGetTheirSavedSize()
    {
        var saved = new Dictionary<string, Dictionary<int, int>> { ["board"] = new() { [1] = 48, [2] = 36, [3] = 0 } };
        Assert.Equal(new[] { (1, 48), (2, 36) }, ServiceRuntime.ForgottenZones(Board(Zone(2, 2, 2), Zone(0), Zone(0), Zone(0)), saved));
    }

    [Fact]
    public void ZonesAlreadyRightOrOutOfRangeAreLeftAlone()
    {
        var saved = new Dictionary<string, Dictionary<int, int>> { ["board"] = new() { [0] = 48, [1] = 900, [7] = 10 } };
        Assert.Empty(ServiceRuntime.ForgottenZones(Board(Zone(48), Zone(0)), saved));
        Assert.Empty(ServiceRuntime.ForgottenZones(Board(Zone(0)) with { Id = "other" }, saved));
    }
}
