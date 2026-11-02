using Prism.Core;
using Prism.Core.Ipc;
using Xunit;

namespace Prism.Lighting.Tests;

public sealed class SpatialEffectTests
{
    public static IEnumerable<object[]> Effects() => Enum.GetValues<LightingEffect>().Select(e => new object[] { e });
    private static readonly MapPoint[] Points = [new(0.2, 0.15), new(0.5, 0.4), new(0.8, 0.9)];
    [Theory, MemberData(nameof(Effects))]
    public void EveryEffectIsSeekSafeAndBrightnessZeroIsBlack(LightingEffect effect)
    {
        var scene = new Scene("Test", effect, new(230, 80, 30));
        var parameters = new EffectParameters { Palette = Palettes.ForEffect(effect) };
        var input = new ReactiveFrame(55, 80, 0.6, 60, 75, [0.2, 0.6, 0.9], 0.3, new(10, 30, 60), new(60, 20, 40), new(100, 20, 10));
        var a = SpatialEffects.Render(scene, parameters, 1.234, Points, kind: "RAM", input: input);
        _ = SpatialEffects.Render(scene, parameters, 100, Points, input: input);
        Assert.Equal(a, SpatialEffects.Render(scene, parameters, 1.234, Points, kind: "RAM", input: input));
        Assert.All(SpatialEffects.Render(scene with { Brightness = 0 }, parameters, 1.234, Points), c => Assert.Equal(RgbColor.Black, c));
        Assert.All(SpatialEffects.Render(scene, parameters with { Devices = new Dictionary<string, bool> { ["ram"] = false } }, 1, Points, "ram"), c => Assert.Equal(RgbColor.Black, c));
    }
    [Fact]
    public void CyclingEffectsLoopWithoutAJump()
    {
        // Spectrum ends on purple and starts on red: the loop must blend across, not snap.
        var stops = Palettes.BuiltIn["Spectrum"];
        static int Step(RgbColor a, RgbColor b) => Math.Max(Math.Abs(a.R - b.R), Math.Max(Math.Abs(a.G - b.G), Math.Abs(a.B - b.B)));
        for (double t = 0; t < 1; t += 0.005)
            Assert.True(Step(Palettes.SampleLoop(stops, t), Palettes.SampleLoop(stops, t + 0.005)) < 16, $"jump at {t:0.000}");
        Assert.Equal(Palettes.SampleLoop(stops, 0), Palettes.SampleLoop(stops, 1));
    }

    [Fact]
    public void SpeedChangesCarryOnFromTheSamePoint()
    {
        var clock = new PhaseClock();
        Assert.Equal(10, clock.Time("case", 10, 1), 9);
        Assert.Equal(12, clock.Time("case", 12, 1), 9);
        Assert.Equal(12, clock.Time("case", 12, 2), 9);   // twice as fast: no jump at the change
        Assert.Equal(14, clock.Time("case", 13, 2), 9);
        Assert.Equal(18, clock.Time("case", 15, 0), 9);   // stopped at 15 s, after two more seconds at double speed
        Assert.Equal(18, clock.Time("case", 20, 0), 9);   // and it holds there
        Assert.Equal(3, clock.Time("fans", 3, 1), 9);     // each look keeps its own clock
    }

    [Fact]
    public void LegacyRenderAlsoAcceptsZeroSpeedAndFreezesPhase()
    {
        var clock = new ManualClock(); var input = new FakeInputs(); var engine = new EffectEngine(clock, input, input, input);
        var scene = new Scene("Frozen", LightingEffect.RainbowWave, new(255, 0, 0), Speed: 0);
        var before = engine.Render(scene, 10); clock.Elapsed = TimeSpan.FromSeconds(10);
        Assert.Equal(before, engine.Render(scene, 10));
    }
    [Fact]
    public void PaletteInterpolatesNonUniformStopsAndClampsEndpoints()
    {
        PaletteStop[] stops = [new(0.2, "#FF0000"), new(0.4, "#0000FF"), new(1, "#FFFFFF")];
        Assert.Equal(new RgbColor(128, 0, 128), Palettes.Sample(stops, 0.3));
        Assert.Equal(new RgbColor(255, 0, 0), Palettes.Sample(stops, -10));
        Assert.Equal(new RgbColor(255, 255, 255), Palettes.Sample(stops, 10));
    }
    [Fact]
    public void MapRotationFlipAndLampTrackStripAndExplicitPoint()
    {
        var device = new RgbDeviceState("ram", "RAM", "RAM", true, true, 3);
        var placement = new DevicePlacement("ram", 0.5, 0.5, 0.6, 90);
        var forward = CaseMap.Positions(device, placement); var flip = CaseMap.Positions(device, placement with { Flip = true });
        Assert.Equal(forward.Reverse(), flip);
        Assert.InRange(forward[0].Y, 0.1999, 0.2001);
        var horizontal = CaseMap.Positions(device, placement with { Rotation = 0 });
        Assert.InRange(horizontal[0].X, 0.1999, 0.2001);
        var strip = device with { Id = "strip", Kind = "strip" };
        LightingMap map = new([new("strip", 0.3, 0.2, 0.4, 0)]);
        Assert.Equal(new(0.64, 0.07), CaseMap.LampPoint(map, [strip]));
        Assert.Equal(new(0.7, 0.8), CaseMap.LampPoint(map with { Lamp = new(0.7, 0.8) }, [strip]));
        var scene = new Scene("Flow", LightingEffect.GradientFlow, new(255, 0, 0));
        Assert.Equal(SpatialEffects.Render(scene, new(), 1, [forward[1]])[0], SpatialEffects.Render(scene, new(), 1, [flip[1]])[0]);
    }
    [Fact]
    public void ResizableZoneTransformsIndependentlyAndMatrixPreservesEmptyCell()
    {
        var board = new RgbDeviceState("board", "Board", "Motherboard", true, true, 5,
            [new("Board", "Linear", 0, 2, 2, 2), new("ARGB", "Linear", 2, 3, 0, 120)]);
        var map = new LightingMap([new("board", 0.3, 0.4), new("board", 0.6, 0.3, 0.4, 0, true, 1)]);
        var positions = CaseMap.Positions(board, map.Devices[0], map);
        Assert.InRange(positions[2].X, 0.7999, 0.8001); Assert.InRange(positions[4].X, 0.3999, 0.4001);
        Assert.Equal(new(0.64, 0.07), CaseMap.LampPoint(map, [board]));
        var matrix = board with { LedCount = 3, Zones = [new("Grid", "Matrix", 0, 3, 3, 3, MatrixWidth: 2, MatrixHeight: 2, Matrix: [0, -1, 1, 2])] };
        positions = CaseMap.Positions(matrix, new("board", 0.5, 0.5, 0.4, 0));
        Assert.Equal(3, positions.Length); Assert.Equal(positions[0].X, positions[1].X); Assert.True(positions[2].X > positions[1].X);
    }
    [Fact]
    public void UnplacedHeadersBecomeAioChainAndCaseFans()
    {
        var board = new RgbDeviceState("board", "Board", "Motherboard", true, true, 206,
            [new("Board", "Linear", 0, 2, 2, 2), new("H1", "Linear", 2, 48, 0, 120), new("H2", "Linear", 50, 36, 0, 120), new("H3", "Linear", 86, 120, 0, 120)]);
        // A map saved before headers had places still holds the shared top line for every header.
        var map = new LightingMap([new("board", 0.25, 0.4), new("board", 0.5, 0.12, 0.66, 0, Zone: 1), new("board", 0.5, 0.12, 0.66, 0, Zone: 2)]);
        var p = CaseMap.Positions(board, map.Devices[0], map);
        var pump = p[2..14]; var radiator = p[14..50]; var caseFans = p[50..86];
        Assert.All(pump, q => Assert.InRange(Math.Sqrt(Math.Pow(q.X - 0.22, 2) + Math.Pow(q.Y - 0.47, 2)), 0.06, 0.07));
        Assert.True(pump[0].Y < pump[6].Y, "ring starts at the top");
        Assert.All(radiator, q => Assert.InRange(q.Y, 0, 0.2));
        Assert.True(radiator[0].X < 0.2 && radiator[35].X > 0.4, "radiator fans chained after the pump run across the top");
        Assert.All(caseFans, q => Assert.InRange(q.X, 0.7, 1));
        Assert.True(caseFans[0].Y < 0.3 && caseFans[30].Y > 0.8, "case fans run down the front");
        // A header the user placed keeps their placement.
        var placed = CaseMap.Positions(board, map.Devices[0], map with { Devices = [.. map.Devices, new("board", 0.1, 0.5, 0.2, 90, Zone: 3)] });
        Assert.All(placed[86..], q => Assert.InRange(q.X, 0.0999, 0.1001));
    }
    [Theory]
    [InlineData(LightingEffect.Comet)] [InlineData(LightingEffect.Scanner)] [InlineData(LightingEffect.Rain)] [InlineData(LightingEffect.Breathing)]
    public void MovingEffectsKeepAFaintGlowInsteadOfGoingDark(LightingEffect effect)
    {
        var line = Enumerable.Range(0, 40).Select(i => new MapPoint(0.5, i / 39d)).ToArray();
        var scene = new Scene("Glow", effect, new(255, 80, 20));
        for (double time = 0; time < 6; time += 0.37)
            Assert.All(SpatialEffects.Render(scene, new() { Palette = "Spectrum" }, time, line), c => Assert.True(c.R + c.G + c.B > 0));
    }
    [Fact]
    public void SpatialWavesSharePhaseAcrossDevicesAndDirectionChangesFlow()
    {
        var scene = new Scene("Flow", LightingEffect.GradientFlow, new(255, 0, 0));
        var p = new EffectParameters { Palette = "Spectrum", Angle = 0 };
        Assert.Equal(SpatialEffects.Render(scene, p, 2, Points, "ram"), SpatialEffects.Render(scene, p, 2, Points, "gpu"));
        Assert.NotEqual(SpatialEffects.Render(scene, p, 2, Points), SpatialEffects.Render(scene, p with { Angle = 90 }, 2, Points));
        Assert.Equal(SpatialEffects.Render(scene with { Speed = 0 }, p, 0, Points), SpatialEffects.Render(scene with { Speed = 0 }, p, 10, Points));
    }
    [Fact]
    public void LoadBarsUseCpuGpuAndMemoryAndHorizontalLedPositions()
    {
        var scene = new Scene("Meters", LightingEffect.LoadMeters, RgbColor.Black);
        MapPoint[] vertical = [new(0.5, 0.9), new(0.5, 0.5), new(0.5, 0.1)];
        MapPoint[] horizontal = [new(0.1, 0.5), new(0.5, 0.5), new(0.9, 0.5)];
        var input = new ReactiveFrame(CpuLoad: 100, GpuLoad: 0, RamUse: 0.5);
        var cpu = SpatialEffects.Render(scene, new(), 1, vertical, kind: "RAM", input: input);
        var gpu = SpatialEffects.Render(scene, new(), 1, horizontal, kind: "GPU", input: input);
        var memory = SpatialEffects.Render(scene, new(), 1, horizontal, kind: "strip", input: input);
        Assert.NotEqual(RgbColor.Black, cpu[2]); Assert.Equal(RgbColor.Black, gpu[2]);
        Assert.Equal(new(255, 255, 220), memory[1]); Assert.Equal(RgbColor.Black, memory[2]);
    }
    [Fact]
    public void EventsRespectPriorityDisableAndFadeBackToExactUnderlyingFrame()
    {
        var events = new EventEffects(); RgbColor[] source = [new(10, 30, 50)]; MapPoint[] point = [new(0.5, 0.5)];
        events.Trigger(LightingEventKind.Notification, 0); events.Trigger(LightingEventKind.FanFailsafe, 0);
        Assert.Equal(new(255, 20, 10), events.Composite(source, point, 0, new())[0]);
        Assert.Equal(new(255, 255, 255), events.Composite(source, point, 0, new(FanFailsafe: false))[0]);
        var faded = events.Composite(source, point, 2.8, new())[0];
        Assert.InRange(faded.R, 11, 254); Assert.Equal(source, events.Composite(source, point, 3, new()));
        events.Trigger(LightingEventKind.FanFailsafe, 4); Assert.Equal(source, events.Composite(source, point, 4, new(FanFailsafe: false, Notification: false)));
    }
    [Fact]
    public void TemperatureAndScreenZonesMapByDeviceAndPosition()
    {
        var input = new ReactiveFrame(CpuTemp: 40, GpuTemp: 85, ScreenLeft: new(255, 0, 0), ScreenCentre: new(0, 255, 0), ScreenRight: new(0, 0, 255));
        var scene = new Scene("Temp", LightingEffect.TemperatureFlow, RgbColor.Black);
        Assert.True(SpatialEffects.Render(scene, new(), 0, Points, kind: "GPU", input: input)[0].R > 200);
        Assert.True(SpatialEffects.Render(scene, new(), 0, Points, kind: "RAM", input: input)[0].B > 200);
        var screen = SpatialEffects.Render(scene with { Effect = LightingEffect.ScreenAmbient }, new(), 0, [new(0, 0), new(0.5, 0), new(1, 0)], input: input);
        Assert.Equal(new RgbColor[] { new(255, 0, 0), new(0, 255, 0), new(0, 0, 255) }, screen);
    }
    [Fact]
    public void SmoothingIsContinuousBetweenPollsAndIndependentOfFrameRate()
    {
        var a = new ReactiveSmoother(); var b = new ReactiveSmoother(); var target = new ReactiveFrame(CpuLoad: 100);
        a.Sample(target, 0); b.Sample(target, 0);
        var early = a.Sample(target, 0.1); var late = a.Sample(target, 0.2);
        Assert.True(early.CpuLoad > 0 && early.CpuLoad < late.CpuLoad && late.CpuLoad < 100);
        Assert.InRange(Math.Abs(late.CpuLoad - b.Sample(target, 0.2).CpuLoad), 0, 0.00001);
    }
    [Fact]
    public void SpectrumSeparatesLowAndHighFrequencyFixturesWithoutCapture()
    {
        var silence = AudioAnalysis.Spectrum(new double[1024], 48000); Assert.All(silence, x => Assert.Equal(0, x));
        var samples = Enumerable.Range(0, 4096).Select(i => Math.Sin(2 * Math.PI * 55 * i / 48000)).ToArray();
        var spectrum = AudioAnalysis.Spectrum(samples, 48000); Assert.True(spectrum[0] > spectrum[8]);
    }
    [Theory]
    [InlineData(-1, 1, 1)] [InlineData(361, 1, 1)] [InlineData(0, 0, 1)] [InlineData(0, 9, 1)] [InlineData(0, 1, -0.1)] [InlineData(0, 1, 1.1)]
    public void ParameterRangesRejectInvalidValues(double angle, double scale, double intensity) =>
        Assert.Throws<ArgumentException>(() => LightingValidation.Parameters(new() { Angle = angle, Scale = scale, Intensity = intensity }));
    [Fact]
    public void InvalidPaletteStopsAndMapAreRejected()
    {
        Assert.Throws<ArgumentException>(() => LightingValidation.Parameters(new() { Stops = [new(0.5, "#FF0000"), new(0.5, "#00FF00")] }));
        Assert.Throws<ArgumentException>(() => LightingValidation.Parameters(new() { Stops = [new(0, "red"), new(1, "#FFFFFF")] }));
        Assert.Throws<ArgumentException>(() => LightingValidation.Parameters(new() { Angle = double.NaN }));
        Assert.Throws<ArgumentException>(() => LightingValidation.Map(new([new("ram", 2, 0.5)])));
        Assert.Throws<ArgumentException>(() => LightingValidation.Map(new([new("ram", 0.5, 0.5), new("ram", 0.4, 0.4)])));
    }
}
