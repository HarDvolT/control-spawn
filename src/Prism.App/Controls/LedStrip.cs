using System.Globalization;
using System.Windows;
using System.Windows.Media;
using Prism.App.Common;
using Prism.Core;
using Prism.Core.Ipc;
using Prism.Lighting;

namespace Prism.App.Controls;

/// <summary>
/// The animated LED strip used by the dashboard, the Lighting preview and the widgets.
/// Every effect in the mockups is reproduced natively from a phase clock.
/// </summary>
public class LedStrip : FrameworkElement
{
    public bool WaveHeights { get; set; }
    /// <summary>False holds one frame (desktop widgets: transparent windows redraw in software, so constant motion is costly).</summary>
    public bool Live { get; set; } = true;
    public static readonly DependencyProperty EffectKindProperty = DependencyProperty.Register(
        nameof(EffectKind), typeof(LightingEffect), typeof(LedStrip),
        new FrameworkPropertyMetadata(LightingEffect.Smart, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty ColorHexProperty = DependencyProperty.Register(
        nameof(ColorHex), typeof(string), typeof(LedStrip),
        new FrameworkPropertyMetadata("#FF4D1A", FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty BrightnessProperty = DependencyProperty.Register(
        nameof(Brightness), typeof(double), typeof(LedStrip),
        new FrameworkPropertyMetadata(0.8, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty SpeedProperty = DependencyProperty.Register(
        nameof(Speed), typeof(double), typeof(LedStrip),
        new FrameworkPropertyMetadata(1d, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty LedCountProperty = DependencyProperty.Register(
        nameof(LedCount), typeof(int), typeof(LedStrip),
        new FrameworkPropertyMetadata(24, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty GapProperty = DependencyProperty.Register(
        nameof(Gap), typeof(double), typeof(LedStrip),
        new FrameworkPropertyMetadata(4d, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty CornerRadiusProperty = DependencyProperty.Register(
        nameof(CornerRadius), typeof(double), typeof(LedStrip),
        new FrameworkPropertyMetadata(6d, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty TemperatureProperty = DependencyProperty.Register(
        nameof(Temperature), typeof(double), typeof(LedStrip),
        new FrameworkPropertyMetadata(45d, FrameworkPropertyMetadataOptions.AffectsRender));

    private double _phase;
    private TimeSpan _lastFrame;
    // A 20 fps timer instead of CompositionTarget.Rendering, which would keep the whole window redrawing at 60 fps.
    private readonly System.Windows.Threading.DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1d / 20) };

    public LedStrip()
    {
        SnapsToDevicePixels = true;
        UseLayoutRounding = true;
        _timer.Tick += OnFrame;
        Motion.WhileVisible(this, () => { if (Live) _timer.Start(); }, _timer.Stop);
    }

    public LightingEffect EffectKind { get => (LightingEffect)GetValue(EffectKindProperty); set => SetValue(EffectKindProperty, value); }
    public string ColorHex { get => (string)GetValue(ColorHexProperty); set => SetValue(ColorHexProperty, value); }
    public double Brightness { get => (double)GetValue(BrightnessProperty); set => SetValue(BrightnessProperty, value); }
    public double Speed { get => (double)GetValue(SpeedProperty); set => SetValue(SpeedProperty, value); }
    public int LedCount { get => (int)GetValue(LedCountProperty); set => SetValue(LedCountProperty, value); }
    public double Gap { get => (double)GetValue(GapProperty); set => SetValue(GapProperty, value); }
    public double CornerRadius { get => (double)GetValue(CornerRadiusProperty); set => SetValue(CornerRadiusProperty, value); }
    public double Temperature { get => (double)GetValue(TemperatureProperty); set => SetValue(TemperatureProperty, value); }

    private bool Animates => EffectKind is not (LightingEffect.Static or LightingEffect.Off or LightingEffect.Adaptive or LightingEffect.Custom);

    private void OnFrame(object? sender, EventArgs e)
    {
        if (Motion.ReduceAnimations || !IsVisible || !Animates) return;
        var now = TimeSpan.FromMilliseconds(Environment.TickCount64);
        if (_lastFrame == TimeSpan.Zero) { _lastFrame = now; return; }
        var delta = (now - _lastFrame).TotalSeconds;
        _lastFrame = now;
        if (delta is <= 0 or > 0.25) return;
        _phase = (_phase + delta * (0.35 + Math.Clamp(Speed, 0, 2) * 0.55)) % 1000;
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext dc)
    {
        var width = ActualWidth;
        var height = ActualHeight;
        if (width <= 0 || height <= 0) return;

        var count = Math.Clamp(LedCount, 1, 200);
        var gap = Math.Max(0, Gap);
        var ledWidth = (width - gap * (count - 1)) / count;
        if (ledWidth <= 0) return;
        var radius = Math.Max(1, Math.Min(CornerRadius, Math.Min(ledWidth, height) / 2));
        var brightness = double.IsFinite(Brightness) ? Math.Clamp(Brightness, 0, 1) : 0;

        RgbColor[]? spatial = null;
        if (EffectKind > LightingEffect.Custom)
        {
            var accent = ColorUtil.Parse(ColorHex, ColorUtil.Accent);
            spatial = SpatialEffects.Render(new("Strip", EffectKind, new(accent.R, accent.G, accent.B), Math.Clamp(Speed, 0, 2)),
                new() { Palette = Palettes.ForEffect(EffectKind) }, _phase,
                Enumerable.Range(0, count).Select(i => new MapPoint((double)i / Math.Max(1, count - 1), 0.5)).ToArray(),
                input: new(CpuTemp: Temperature));
        }
        for (var i = 0; i < count; i++)
        {
            var color = spatial is null ? ColorAt(i, count) : Color.FromRgb(spatial[i].R, spatial[i].G, spatial[i].B);
            if (brightness < 1) color = ColorUtil.Mix(Color.FromRgb(10, 10, 11), color, 0.25 + brightness * 0.75);
            double wave = WaveHeights ? .55 + .45 * Math.Abs(Math.Sin((i + _phase) * .35)) : 1;
            double barHeight = WaveHeights ? Math.Min(height, 20 + 30 * wave) : height;
            var rect = new Rect(i * (ledWidth + gap), height - barHeight, ledWidth, barHeight);
            if (WaveHeights)
            {
                dc.PushOpacity(wave);
                dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromArgb(35, color.R, color.G, color.B)), null, new Rect(rect.X - 3, rect.Y - 3, rect.Width + 6, rect.Height + 6), radius + 2, radius + 2);
            }
            dc.DrawRoundedRectangle(ColorUtil.Brush(color), null, rect, radius, radius);
            if (WaveHeights) dc.Pop();
        }
    }

    private Color ColorAt(int index, int count)
    {
        var accent = ColorUtil.Parse(ColorHex, ColorUtil.Accent);
        var t = _phase;
        switch (EffectKind)
        {
            case LightingEffect.Custom: return ColorUtil.TextTertiary;
            case LightingEffect.Off:
                return ColorUtil.Track;
            case LightingEffect.RainbowWave:
                return ColorUtil.Hsl(index * 360.0 / count + t * 30, 0.85, 0.56);
            case LightingEffect.ColorCycle:
                return ColorUtil.Hsl(t * 24, 0.85, 0.56);
            case LightingEffect.Breathing:
            {
                var amount = 0.2 + 0.8 * Math.Abs(Math.Sin(t * 0.55));
                return ColorUtil.Mix(Color.FromRgb(20, 20, 22), accent, amount);
            }
            case LightingEffect.Strobing:
                return ((int)(t * 1.4) % 2 == 0) ? ColorUtil.TextPrimary : ColorUtil.Track;
            case LightingEffect.StarryNight:
                return (index * 7 + (int)(t * 0.8)) % 5 == 0 ? ColorUtil.TextPrimary : ColorUtil.Track;
            case LightingEffect.Music:
                return Math.Abs(Math.Sin(index / 2.0 + t * 0.9)) > 0.5 ? accent : ColorUtil.Mix(Color.FromRgb(30, 14, 8), accent, 0.35);
            case LightingEffect.Static:
                return accent;
            case LightingEffect.Adaptive:
                return index < count / 2 ? Color.FromRgb(0x60, 0xA5, 0xFA) : ColorUtil.Fair;
            default:
            {
                var heat = ColorUtil.HeatAmount(Temperature);
                var amount = Math.Clamp(heat + Math.Sin((index + t) / 4.0) * 0.18, 0, 1);
                return ColorUtil.Mix(Color.FromRgb(0xE4, 0xE4, 0xE7), accent, amount);
            }
        }
    }
}

/// <summary>Small numeric label used inside the previews; keeps invariant formatting.</summary>
public static class StripText
{
    public static string Percent(double? value, int decimals = 0) =>
        value is { } v && double.IsFinite(v) ? v.ToString("F" + decimals, CultureInfo.InvariantCulture) + "%" : Format.Dash;
}
