using ControlSpawn.Core;
using Xunit;

namespace ControlSpawn.Lighting.Tests;

public sealed class LampWhiteTests
{
    [Theory]
    [InlineData(0xFF, 0xE6, 0xC7, 5000)]   // the owner's "warm white"
    [InlineData(0xFF, 0xFF, 0xFF, 6500)]
    [InlineData(0xFF, 0xF3, 0xEF, 6000)]
    [InlineData(0xF0, 0xD7, 0xBB, 5000)]   // a dimmer pick of the same white keeps its temperature
    public void WhitesBecomeAColourTemperature(byte r, byte g, byte b, int kelvin) =>
        Assert.Equal(kelvin, LampWhite.Kelvin(new RgbColor(r, g, b)));

    [Theory]
    [InlineData(0xFF, 0x4D, 0x1A)]   // accent orange
    [InlineData(0x36, 0xD6, 0xFF)]   // cyan
    [InlineData(0xFF, 0xC0, 0xCB)]   // pale pink stays a colour
    [InlineData(0x30, 0x30, 0x30)]   // dark grey is not a room light
    public void ColoursStayRgb(byte r, byte g, byte b) => Assert.Null(LampWhite.Kelvin(new RgbColor(r, g, b)));
}
