using Prism.Core;
using Xunit;

namespace Prism.Lighting.Tests;

public sealed class EffectTests
{
    [Theory]
    [InlineData(LightingEffect.Static)] [InlineData(LightingEffect.Breathing)] [InlineData(LightingEffect.Strobing)]
    [InlineData(LightingEffect.ColorCycle)] [InlineData(LightingEffect.RainbowWave)] [InlineData(LightingEffect.StarryNight)]
    [InlineData(LightingEffect.Smart)] [InlineData(LightingEffect.Music)] [InlineData(LightingEffect.Adaptive)] [InlineData(LightingEffect.Off)] [InlineData(LightingEffect.Custom)]
    public void AllEffectsAreDeterministicWithInjectedTime(LightingEffect effect)
    {
        var clock = new ManualClock { Elapsed = TimeSpan.FromSeconds(1.234) }; var input = new FakeInputs { Level = 0.5 };
        var engine = new EffectEngine(clock, input, input, input); var scene = new Scene("test", effect, new(100, 200, 50));
        var a = engine.Render(scene, 20); var b = engine.Render(scene, 20);
        Assert.Equal(20, a.Count); Assert.Equal(a, b);
    }
    [Fact]
    public void SmartUsesCoolHotEndpointsAndInvalidTemperatureIsHot()
    {
        var input = new FakeInputs(); var engine = new EffectEngine(new ManualClock(), input, input, input);
        var scene = new Scene("smart", LightingEffect.Smart, RgbColor.Black);
        Assert.Equal(new RgbColor(0, 128, 255), engine.Render(scene, 1)[0]);
        input.Temperature = 85; Assert.Equal(new RgbColor(255, 32, 0), engine.Render(scene, 1)[0]);
        input.Temperature = double.NaN; Assert.Equal(new RgbColor(255, 32, 0), engine.Render(scene, 1)[0]);
    }
    [Fact]
    public void AudioScreenOffAndBrightnessProduceExpectedColors()
    {
        var input = new FakeInputs { Level = 0.5, Average = new(12, 34, 56) };
        var engine = new EffectEngine(new ManualClock(), input, input, input);
        var scene = new Scene("test", LightingEffect.Music, new(100, 200, 40));
        Assert.Equal(new RgbColor(50, 100, 20), engine.Render(scene, 1)[0]);
        Assert.Equal(input.Average, engine.Render(scene with { Effect = LightingEffect.Adaptive }, 1)[0]);
        Assert.Equal(RgbColor.Black, engine.Render(scene with { Effect = LightingEffect.Off }, 1)[0]);
        Assert.Equal(new RgbColor(25, 50, 10), engine.Render(scene with { Brightness = 0.5 }, 1)[0]);
    }
    [Fact]
    public void BreathingStrobeRainbowAndStarsVaryAsDesigned()
    {
        var clock = new ManualClock(); var input = new FakeInputs(); var engine = new EffectEngine(clock, input, input, input);
        var scene = new Scene("test", LightingEffect.Breathing, new(100, 200, 40));
        Assert.Equal(RgbColor.Black, engine.Render(scene, 1)[0]);
        clock.Elapsed = TimeSpan.FromSeconds(1); Assert.Equal(scene.Color, engine.Render(scene, 1)[0]);
        clock.Elapsed = TimeSpan.FromSeconds(0.3); Assert.Equal(RgbColor.Black, engine.Render(scene with { Effect = LightingEffect.Strobing }, 1)[0]);
        Assert.True(engine.Render(scene with { Effect = LightingEffect.RainbowWave }, 12).Distinct().Count() > 6);
        Assert.True(engine.Render(scene with { Effect = LightingEffect.StarryNight }, 40).Distinct().Count() > 1);
    }
    [Fact]
    public async Task OutputLoopSchedulesThirtyFramesPerSecond()
    {
        var clock = new ManualClock(); var input = new FakeInputs(); var engine = new EffectEngine(clock, input, input, input);
        using var cancel = new CancellationTokenSource(); var times = new List<TimeSpan>();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => engine.RunAsync(() => new("test", LightingEffect.Off, RgbColor.Black), 1,
            (_, _) => { times.Add(clock.Elapsed); if (times.Count == 31) cancel.Cancel(); return Task.CompletedTask; }, cancel.Token));
        Assert.InRange(times[^1].TotalSeconds, 0.9999, 1.0001);
    }
}
