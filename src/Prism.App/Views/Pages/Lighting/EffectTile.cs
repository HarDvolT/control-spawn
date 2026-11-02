using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using Prism.App.Common;
using Prism.App.ViewModels;
using Prism.Core;
using Prism.Core.Ipc;
using Prism.Lighting;

namespace Prism.App.Views;

/// <summary>Which tuning controls an effect actually uses, so the studio only shows those.</summary>
[Flags]
public enum EffectControls { None = 0, Colour = 1, Speed = 2, Palette = 4, Direction = 8, Size = 16, Comets = 32, CpuBeat = 64 }

public sealed record EffectInfo(LightingEffect Effect, string Name, string Description, string Category, EffectControls Controls);

/// <summary>The effects the studio offers, grouped. Engine aliases (Sparkle, AudioSpectrum…) are left out so each look appears once.</summary>
public static class EffectCatalog
{
    private const EffectControls Flow = EffectControls.Palette | EffectControls.Speed;
    public static IReadOnlyList<string> Categories { get; } = ["All", "Simple", "Motion", "Nature", "Reactive"];
    public static IReadOnlyList<EffectInfo> All { get; } =
    [
        new(LightingEffect.Static, "Static", "One steady colour", "Simple", EffectControls.Colour),
        new(LightingEffect.Breathing, "Breathing", "Slowly fades in and out", "Simple", EffectControls.Colour | EffectControls.Speed),
        new(LightingEffect.Heartbeat, "Heartbeat", "A double pulse, like a heartbeat", "Simple", EffectControls.Colour | EffectControls.Speed | EffectControls.CpuBeat),
        new(LightingEffect.Strobing, "Strobe", "Fast on-off flashes", "Simple", EffectControls.Colour | EffectControls.Speed),
        new(LightingEffect.ColorCycle, "Colour cycle", "Everything steps through the palette", "Simple", Flow),
        new(LightingEffect.Off, "Off", "All case lights off", "Hidden", EffectControls.None),
        new(LightingEffect.RainbowWave, "Rainbow", "A wave that travels across the case", "Motion", Flow | EffectControls.Direction | EffectControls.Size),
        new(LightingEffect.GradientFlow, "Gradient flow", "A smooth gradient sweeping through", "Motion", Flow | EffectControls.Direction | EffectControls.Size),
        new(LightingEffect.Comet, "Comet", "Light chases around with a tail", "Motion", Flow | EffectControls.Direction | EffectControls.Size | EffectControls.Comets),
        new(LightingEffect.Scanner, "Scanner", "A beam sweeping back and forth", "Motion", Flow | EffectControls.Direction | EffectControls.Size),
        new(LightingEffect.Plasma, "Plasma", "Liquid swirls of colour", "Motion", Flow | EffectControls.Size),
        new(LightingEffect.Aurora, "Aurora", "Slow, drifting northern lights", "Nature", Flow | EffectControls.Size),
        new(LightingEffect.Fire, "Fire", "Flickering flames rising up", "Nature", Flow),
        new(LightingEffect.OceanRipple, "Ocean", "Ripples spreading from the centre", "Nature", Flow | EffectControls.Size),
        new(LightingEffect.Rain, "Rain", "Drops falling down the case", "Nature", Flow | EffectControls.Size),
        new(LightingEffect.Candle, "Candle", "A warm, gentle flicker", "Nature", Flow),
        new(LightingEffect.StarryNight, "Starry night", "Stars twinkling on your colour", "Nature", EffectControls.Colour | Flow),
        new(LightingEffect.Smart, "Temperature", "Cool blue to hot red with CPU heat", "Reactive", EffectControls.None),
        new(LightingEffect.LoadMeters, "Load meters", "CPU, GPU and RAM load as bars", "Reactive", EffectControls.None),
        new(LightingEffect.Music, "Music", "Dances to whatever is playing", "Reactive", EffectControls.Palette),
        new(LightingEffect.Adaptive, "Screen", "Matches the edges of your screen", "Reactive", EffectControls.None),
    ];
    public static EffectInfo For(LightingEffect effect) => All.FirstOrDefault(e => e.Effect == effect)
        ?? new(effect, Format.EffectName(effect), Format.EffectSubtitle(effect), "Simple", EffectControls.Speed);
}

/// <summary>A strip of LEDs running the real effect in the user's colour and palette: a gentle 10 fps while the page is open, full speed when hovered or chosen.</summary>
public sealed class EffectTile : FrameworkElement
{
    private const int Columns = 14, Rows = 2;
    private static readonly MapPoint[] Positions = Enumerable.Range(0, Columns * Rows).Select(i => new MapPoint((i % Columns + 0.5) / Columns, (i / Columns + 0.5) / Rows * 0.6 + 0.2)).ToArray();
    private static readonly System.Diagnostics.Stopwatch Clock = System.Diagnostics.Stopwatch.StartNew();
    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromSeconds(1d / 20) };
    private int tick;
    public LightingEffect LightEffect { get; init; }
    public bool IsActive { get; set; }
    private bool Focused => IsActive || IsMouseOver || (Parent as FrameworkElement)?.IsMouseOver == true;

    public EffectTile()
    {
        Height = 34;
        timer.Tick += (_, _) => { if (Motion.ReduceAnimations) return; if (Focused || ++tick % 2 == 0) InvalidateVisual(); };
        Motion.WhileVisible(this, timer.Start, timer.Stop);
    }

    protected override void OnRender(DrawingContext dc)
    {
        if (DataContext is not LightingViewModel model || ActualWidth <= 0) return;
        bool moving = !Motion.ReduceAnimations;
        var state = model.State;
        var scene = new Scene("Tile", LightEffect, Palettes.Parse(model.ColorHex), Math.Max(0.4, model.Speed), 1);
        // A still tile shows the effect at its most recognisable moment: pulses at their peak, strobe while lit.
        double still = LightEffect switch { LightingEffect.Breathing => 0.8, LightingEffect.Heartbeat => 0.12, LightingEffect.Strobing => 0.1, _ => 1.7 * scene.Speed } / scene.Speed;
        double time = moving ? Clock.Elapsed.TotalSeconds : still;
        var parameters = model.Parameters with { Intensity = 1, Palette = model.Parameters.Stops is null ? Palettes.ForEffect(LightEffect) : model.Parameters.Palette };
        double wave = (Math.Sin(time * 3) + 1) / 2;
        var frame = new ReactiveFrame(state?.System.CpuLoad ?? 45, state?.System.GpuLoad ?? 60, 0.55, state?.System.CpuTemp ?? 62, state?.System.GpuTemp ?? 55,
            Enumerable.Range(0, 16).Select(i => 0.3 + 0.6 * Math.Abs(Math.Sin(time * 2.4 + i * 0.7)) * wave).ToArray(), wave,
            Palettes.Parse("#FF4D1A"), Palettes.Parse("#B26BFF"), Palettes.Parse("#36D6FF"));
        RgbColor[] colors;
        try { colors = SpatialEffects.Render(scene, parameters, time, Positions, "tile", "Strip", frame); }
        catch (ArgumentException) { colors = new RgbColor[Positions.Length]; }
        dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromRgb(13, 14, 18)), null, new Rect(0, 0, ActualWidth, ActualHeight), 9, 9);
        double cell = (ActualWidth - 12) / Columns, rowHeight = (ActualHeight - 10) / Rows;
        for (int i = 0; i < colors.Length; i++)
        {
            var c = colors[i]; var color = Color.FromRgb(c.R, c.G, c.B);
            var rect = new Rect(6 + i % Columns * cell + 1.5, 5 + i / Columns * rowHeight + 1.5, cell - 3, rowHeight - 3);
            dc.DrawRoundedRectangle(new SolidColorBrush(color), null, rect, 3, 3);
        }
    }
}
