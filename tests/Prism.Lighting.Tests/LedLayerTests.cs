using System.Buffers.Binary;
using Prism.Core;
using Xunit;

namespace Prism.Lighting.Tests;

public sealed class LedLayerTests
{
    [Theory]
    [InlineData("00", 100, 150, 200)]
    [InlineData("FF", 200, 50, 0)]
    [InlineData("80", 150, 100, 100)]
    public void AlphaCompositesEveryChannel(string alpha, byte r, byte g, byte b)
    {
        var result = LedLayers.Composite([new(100, 150, 200)], null, ["#C83200" + alpha], false);
        Assert.Equal(new RgbColor(r, g, b), result[0]);
    }
    [Fact]
    public void ExactColoursIgnoreEffectAndBrightnessAndStillAcceptOverlay()
    {
        Assert.Equal(new RgbColor(10, 20, 30), LedLayers.Composite([new(200, 100, 50)], ["#0A141E"], null, true)[0]);
        Assert.Equal(new RgbColor(255, 0, 0), LedLayers.Composite([RgbColor.Black], ["#0A141E"], ["#FF0000FF"], true)[0]);
        Assert.Equal(RgbColor.Black, LedLayers.Composite([new(1, 2, 3)], null, null, true)[0]);
    }
    [Fact]
    public void ResizeKeepsPrefixPadsBlackOrTransparentAndHandlesZero()
    {
        Assert.Equal(new[] { "#112233", "#000000", "#000000" }, LedLayers.Resize(["#112233"], 3, false));
        Assert.Equal(new[] { "#11223344", "#00000000" }, LedLayers.Resize(["#11223344"], 2, true));
        Assert.Equal(new[] { "#112233" }, LedLayers.Resize(["#112233", "#445566"], 1, false));
        Assert.Empty(LedLayers.Resize(["#112233"], 0, false));
    }
    [Fact]
    public void FixtureProjectsZoneOffsetsNamesResizeLimitsAndMatrixHoles()
    {
        byte[] matrix = new byte[24];
        BinaryPrimitives.WriteUInt32LittleEndian(matrix, 2); // height
        BinaryPrimitives.WriteUInt32LittleEndian(matrix.AsSpan(4), 2); // width
        foreach (var (index, value) in new uint[] { 0, uint.MaxValue, 1, 2 }.Select((v, i) => (i, v)))
            BinaryPrimitives.WriteUInt32LittleEndian(matrix.AsSpan(8 + index * 4), value);
        var fixture = new OpenRgbController(0, "Board", "ASUS", "", "", "", "", 0, [],
            [new("Board", 1, 2, 2, 2, []), new("Matrix", 2, 3, 3, 3, matrix), new("ARGB", 1, 0, 120, 0, [])],
            Enumerable.Range(0, 5).Select(i => new OpenRgbLed("LED " + i, (uint)i)).ToArray(), Enumerable.Repeat(RgbColor.Black, 5).ToArray());
        var state = LedLayers.Project("fixture", fixture);
        Assert.Equal("Motherboard", state.Kind); Assert.Equal(5, state.LedCount);
        Assert.Equal(new[] { 0, 2, 5 }, state.Zones!.Select(z => z.FirstLed));
        var grid = state.Zones![1]; Assert.Equal("Matrix", grid.Type);
        Assert.Equal(2, grid.MatrixWidth); Assert.Equal(2, grid.MatrixHeight);
        Assert.Equal(new[] { 2, -1, 3, 4 }, grid.Matrix);
        Assert.Equal(new[] { "LED 2", "LED 3", "LED 4" }, grid.LedNames);
        Assert.Equal(120, state.Zones![2].MaxLeds); Assert.Equal(0, state.Zones![2].LedCount);
    }
}
