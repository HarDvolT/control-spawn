using Prism.Core.Ipc;
using Prism.Lighting;
using Prism.Service;
using Xunit;

namespace Prism.Service.Tests;

public sealed class PositionCacheTests
{
    private static RgbDeviceState Board(int leds = 12) => new("board", "ROG STRIX B850-F", "Motherboard", true, true, leds);
    private static RgbDeviceState Ram() => new("ram", "Vengeance", "RAM", true, true, 10);

    [Fact]
    public void SameMapAndDevicesReuseTheSamePositionsAndTheyMatchADirectComputation()
    {
        var cache = new PositionCache(); RgbDeviceState[] all = [Board(), Ram()]; var map = CaseMap.Default(all);
        var first = cache.Get(map, all[0], all);
        for (int frame = 0; frame < 60; frame++) Assert.Same(first, cache.Get(map, all[0], all));
        Assert.Equal(CaseMap.Positions(all[0], CaseMap.Placement(map, all[0], all), map), first);
        cache.Get(map, all[1], all);
        Assert.Equal(2, cache.Computed);
    }

    [Fact]
    public void AnyEditToTheMapOrDeviceListRecomputes()
    {
        var cache = new PositionCache(); RgbDeviceState[] all = [Board(), Ram()]; var map = CaseMap.Default(all);
        var before = cache.Get(map, all[0], all);
        var moved = map with { Devices = map.Devices.Select(p => p.Device == "board" ? p with { X = 0.9, Rotation = 0 } : p).ToArray() };
        var after = cache.Get(moved, all[0], all);
        Assert.NotEqual(before, after);
        Assert.Equal(CaseMap.Positions(all[0], CaseMap.Placement(moved, all[0], all), moved), after);
        RgbDeviceState[] resized = [Board(30), Ram()];   // a header resize replaces the device list
        Assert.Equal(30, cache.Get(moved, resized[0], resized).Length);
        Assert.Equal(3, cache.Computed);
    }
}
