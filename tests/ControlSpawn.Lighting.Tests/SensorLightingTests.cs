using ControlSpawn.Core;
using ControlSpawn.Core.Ipc;
using Xunit;

namespace ControlSpawn.Lighting.Tests;

public sealed class SensorLightingTests
{
    private static readonly RgbColor[] Normal = Enumerable.Repeat(new RgbColor(10, 20, 30), 4).ToArray();
    private static SensorBinding Binding(SensorStyle style = SensorStyle.ColourGradient) => new("gpu.temperature", 0, 100, style, [new(0, "#0000FF"), new(1, "#FF0000")], 0);
    [Theory]
    [InlineData(-20, 0, 0, 255)] [InlineData(0, 0, 0, 255)] [InlineData(50, 128, 0, 128)] [InlineData(100, 255, 0, 0)] [InlineData(120, 255, 0, 0)]
    public void GradientInterpolatesAndClamps(double value, byte r, byte g, byte b) =>
        Assert.All(new SensorLighting().Render("gpu", Binding(), value, 0, Normal), c => Assert.Equal(new RgbColor(r, g, b), c));
    [Fact]
    public void FillHasDarkUnfilledPixelsAndBrightTip()
    {
        var engine = new SensorLighting(); var binding = Binding(SensorStyle.FillBar);
        Assert.Equal(new[] { new RgbColor(128, 0, 128), new(255, 255, 255), RgbColor.Black, RgbColor.Black }, engine.Render("gpu", binding, 50, 0, Normal));
        Assert.All(engine.Render("gpu", binding, 0, 1, Normal), c => Assert.Equal(RgbColor.Black, c));
        Assert.Equal(new RgbColor(255, 255, 255), engine.Render("gpu", binding, 100, 2, Normal)[^1]);
    }
    [Fact]
    public void BrightnessAndBreathingHaveFixedEndpoints()
    {
        var engine = new SensorLighting();
        Assert.Equal(new RgbColor(13, 0, 0), engine.Render("dim", Binding(SensorStyle.Brightness), 0, 0, Normal)[0]);
        Assert.Equal(new RgbColor(255, 0, 0), engine.Render("bright", Binding(SensorStyle.Brightness), 100, 0, Normal)[0]);
        Assert.Equal(RgbColor.Black, engine.Render("pulse", Binding(SensorStyle.BreathingSpeed), 100, 0, Normal)[0]);
        Assert.Equal(new RgbColor(255, 0, 0), engine.Render("pulse", Binding(SensorStyle.BreathingSpeed), 100, 1 / 3.3, Normal)[0]);
    }
    [Fact]
    public void ThresholdHasHysteresis()
    {
        var engine = new SensorLighting(); var b = Binding(SensorStyle.Threshold) with { Hysteresis = 5 };
        Assert.Equal(new RgbColor(0, 0, 255), engine.Render("gpu", b, 49, 0, Normal)[0]);
        Assert.Equal(new RgbColor(255, 0, 0), engine.Render("gpu", b, 50, 1, Normal)[0]);
        Assert.Equal(new RgbColor(255, 0, 0), engine.Render("gpu", b, 46, 2, Normal)[0]);
        Assert.Equal(new RgbColor(0, 0, 255), engine.Render("gpu", b, 44, 3, Normal)[0]);
    }
    [Fact]
    public void ExponentialSmoothingUsesElapsedTimeAndZeroIsImmediate()
    {
        var engine = new SensorLighting(); var b = Binding() with { Smoothing = 2 };
        engine.Observe("gpu", b, 0, 0);
        Assert.Equal(100 * (1 - Math.Exp(-0.5)), engine.Observe("gpu", b, 100, 1).Value!.Value, 8);
        Assert.Equal(100 * (1 - Math.Exp(-1)), engine.Observe("gpu", b, 100, 2).Value!.Value, 8);
        Assert.Equal(100d, engine.Observe("gpu", b with { Smoothing = 0 }, 100, 3).Value);
    }
    [Fact]
    public void AutoLearnTracksRawSessionExtremaAndResetsWhenSourceChanges()
    {
        var engine = new SensorLighting(); var b = Binding() with { AutoLearn = true };
        Assert.Equal(0.5, engine.Observe("gpu", b, 50, 0).Normalized);
        engine.Observe("gpu", b, 30, 1); var sample = engine.Observe("gpu", b, 80, 2);
        Assert.Equal(30, sample.Min); Assert.Equal(80, sample.Max); Assert.Equal(1, sample.Normalized);
        sample = engine.Observe("gpu", b with { Source = "cpu.load" }, 60, 3);
        Assert.Equal(60, sample.Min); Assert.Equal(60, sample.Max);
    }
    [Theory]
    [InlineData(null)] [InlineData(double.NaN)] [InlineData(double.PositiveInfinity)]
    public void MissingSensorsReturnExactNormalEffect(double? missing)
    {
        var engine = new SensorLighting(); var b = Binding() with { Alert = new(10) };
        Assert.Same(Normal, engine.Render("gpu", b, missing, 0, Normal));
        Assert.Contains("unavailable", engine.Observe("gpu", b, missing, 1).Reason);
    }
    [Fact]
    public void RawAlertBeatsSmoothingAndStyleButFanFailsafeBeatsAlert()
    {
        var engine = new SensorLighting(); var b = Binding(SensorStyle.FillBar) with { Smoothing = 5, Alert = new(95) };
        engine.Observe("gpu", b, 0, 0);
        var colors = engine.Render("gpu", b, 95, 0, Normal);
        Assert.All(colors, c => { Assert.True(c.R > 0); Assert.Equal(0, c.G); Assert.Equal(0, c.B); });
        var events = new EventEffects(); var points = Enumerable.Repeat(new MapPoint(0.5, 0.5), 4).ToArray();
        events.Trigger(LightingEventKind.Notification, 0);
        Assert.Equal(colors, events.Composite(colors, points, 0, new(), new HashSet<int> { 0, 1, 2, 3 }));
        events.Trigger(LightingEventKind.FanFailsafe, 0);
        Assert.All(events.Composite(colors, points, 0, new(), new HashSet<int> { 0, 1, 2, 3 }), c => Assert.Equal(new RgbColor(255, 20, 10), c));
    }
    public static IEnumerable<object[]> Invalid()
    {
        yield return [Binding() with { Source = "missing" }]; yield return [Binding() with { Source = null! }];
        yield return [Binding() with { Palette = [null!, null!] }]; yield return [Binding() with { Min = 100 }];
        yield return [Binding() with { Max = double.NaN }]; yield return [Binding() with { Smoothing = 6 }];
        yield return [Binding() with { Smoothing = -1 }]; yield return [Binding() with { Style = (SensorStyle)99 }];
        yield return [Binding() with { Alert = new(double.NaN) }]; yield return [Binding() with { Hysteresis = -1 }];
        yield return [Binding() with { Palette = [new(0, "blue"), new(1, "#FF0000")] }];
        yield return [Binding() with { Palette = [new(0, "#000000")] }];
        yield return [Binding() with { Palette = [new(0.5, "#000000"), new(1, "#FFFFFF")] }];
    }
    [Theory, MemberData(nameof(Invalid))]
    public void ValidationRejectsInvalidConfiguration(SensorBinding b) => Assert.Throws<ArgumentException>(() => SensorSources.Validate(b));
}
