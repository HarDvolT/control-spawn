using System.Collections;
using System.Globalization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using ControlSpawn.App.Common;

namespace ControlSpawn.App.Controls;

/// <summary>Shared, bindable metric primitives with the same status scale and motion preference.</summary>
public abstract class MetricControl : FrameworkElement
{
    public bool UseStatusGlow { get; set; } = true;
    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(nameof(Value), typeof(double), typeof(MetricControl),
        new FrameworkPropertyMetadata(0d, FrameworkPropertyMetadataOptions.AffectsRender, Changed));
    public static readonly DependencyProperty MaximumProperty = DependencyProperty.Register(nameof(Maximum), typeof(double), typeof(MetricControl), new FrameworkPropertyMetadata(100d, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty SensorKindProperty = DependencyProperty.Register(nameof(SensorKind), typeof(SensorKind), typeof(MetricControl), new FrameworkPropertyMetadata(SensorKind.Load, FrameworkPropertyMetadataOptions.AffectsRender, (d, _) => ((MetricControl)d).UpdateGlow()));
    public static readonly DependencyProperty SuffixProperty = DependencyProperty.Register(nameof(Suffix), typeof(string), typeof(MetricControl), new FrameworkPropertyMetadata("", FrameworkPropertyMetadataOptions.AffectsRender));
    private static readonly DependencyProperty DisplayProperty = DependencyProperty.Register("Display", typeof(double), typeof(MetricControl), new FrameworkPropertyMetadata(0d, FrameworkPropertyMetadataOptions.AffectsRender));
    protected MetricControl()
    {
        Loaded += (_, _) => { UpdateGlow(); DisplayPreferences.Changed += PreferencesChanged; };
        Unloaded += (_, _) => DisplayPreferences.Changed -= PreferencesChanged;
    }
    private void PreferencesChanged(object? sender, EventArgs e) => InvalidateVisual();
    public double Value { get => (double)GetValue(ValueProperty); set => SetValue(ValueProperty, value); }
    public double Maximum { get => (double)GetValue(MaximumProperty); set => SetValue(MaximumProperty, value); }
    public SensorKind SensorKind { get => (SensorKind)GetValue(SensorKindProperty); set => SetValue(SensorKindProperty, value); }
    public string Suffix { get => (string)GetValue(SuffixProperty); set => SetValue(SuffixProperty, value); }
    protected double Display => (double)GetValue(DisplayProperty);
    protected double Fraction => double.IsFinite(Display) && Maximum > 0 ? Math.Clamp(Display / Maximum, 0, 1) : 0;
    protected Brush StatusBrush => Thresholds.Brush(Thresholds.Level(Value, SensorKind));
    protected static readonly Brush Track = new SolidColorBrush(Color.FromRgb(36, 36, 42));
    private static void Changed(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var c = (MetricControl)d;
        double value = double.IsFinite((double)e.NewValue) ? (double)e.NewValue : 0;
        double from = c.Display;
        c.BeginAnimation(DisplayProperty, null);
        c.SetValue(DisplayProperty, value);
        if (c.IsLoaded && Motion.Glides(from, value, 100)) { var glide = Motion.Glide(value); glide.From = from; c.BeginAnimation(DisplayProperty, glide); }
        c.UpdateGlow();
    }
    private StatusLevel? glowLevel;
    private void UpdateGlow()
    {
        if (!UseStatusGlow) { Effect = null; glowLevel = null; return; }
        var level = Thresholds.Level(Value, SensorKind);
        // A new effect every reading forced the glow to be rebuilt each second; it only changes with the status colour.
        if (level == glowLevel) return;
        glowLevel = level;
        Effect = Thresholds.Glow(level);
        BeginAnimation(OpacityProperty, null);
        if (level == StatusLevel.Critical && IsLoaded && !Motion.ReduceAnimations)
            BeginAnimation(OpacityProperty, Motion.Decorative(new DoubleAnimation(.65, 1, TimeSpan.FromMilliseconds(700)) { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever }, 20));
    }
    protected void Text(DrawingContext dc, string text, Point at, double size = 20)
    {
        var formatted = new FormattedText(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            Typefaces.Number(FontWeights.SemiBold), size, Brushes.White, VisualTreeHelper.GetDpi(this).PixelsPerDip);
        dc.DrawText(formatted, at);
    }
    protected string Number => double.IsFinite(Value) ? (Suffix.Contains('\u00b0') ? DisplayPreferences.Temperature(Display) : Display).ToString("0", CultureInfo.InvariantCulture) + (Suffix.Contains("\u00b0C") ? Suffix.Replace("\u00b0C", DisplayPreferences.Unit) : Suffix) : "\u2014";
    protected Point Polar(double angle, double radius, Point center) => new(center.X + Math.Cos(angle) * radius, center.Y + Math.Sin(angle) * radius);
    protected void Arc(DrawingContext dc, Point center, double radius, double start, double sweep, Brush brush, double width)
    {
        if (sweep <= 0 || radius <= 0) return;
        var g = new StreamGeometry();
        using (var c = g.Open())
        {
            c.BeginFigure(Polar(start, radius, center), false, false);
            c.ArcTo(Polar(start + sweep, radius, center), new Size(radius, radius), 0, sweep > Math.PI, SweepDirection.Clockwise, true, false);
        }
        dc.DrawGeometry(null, new Pen(brush, width) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round }, g);
    }
}

public class RadialGauge : MetricControl
{
    public RadialGauge() { MinWidth = 80; MinHeight = 80; }
    protected override void OnRender(DrawingContext dc)
    {
        var center = new Point(ActualWidth / 2, ActualHeight / 2);
        double r = Math.Max(0, Math.Min(ActualWidth, ActualHeight) / 2 - 12), start = Math.PI * .75, sweep = Math.PI * 1.5;
        Arc(dc, center, r, start, sweep, Track, 7);
        Arc(dc, center, r, start, sweep * Fraction, StatusBrush, 7);
        for (int i = 0; i <= 20; i++) dc.DrawLine(new Pen(i / 20d <= Fraction ? StatusBrush : Track, 1), Polar(start + sweep * i / 20, r - 9, center), Polar(start + sweep * i / 20, r - 13, center));
        Text(dc, Number, new Point(center.X - Number.Length * 6, center.Y - 14));
    }
}
public sealed class ArcSpeedometer : MetricControl
{
    public ArcSpeedometer() { MinWidth = 100; MinHeight = 80; }
    protected override void OnRender(DrawingContext dc)
    {
        var center = new Point(ActualWidth / 2, ActualHeight * .7);
        double r = Math.Max(0, Math.Min(ActualWidth / 2 - 12, ActualHeight * .65));
        Arc(dc, center, r, Math.PI, Math.PI, Track, 8);
        Arc(dc, center, r, Math.PI, Math.PI * Fraction, StatusBrush, 8);
        for (int i = 0; i <= 10; i++) dc.DrawLine(new Pen(StatusBrush, 1), Polar(Math.PI + i * Math.PI / 10, r - 9, center), Polar(Math.PI + i * Math.PI / 10, r - 14, center));
        dc.DrawLine(new Pen(StatusBrush, 2), center, Polar(Math.PI + Fraction * Math.PI, r - 18, center));
        dc.DrawEllipse(StatusBrush, null, center, 4, 4);
        Text(dc, Number, new Point(8, center.Y + 8), 15);
    }
}
public sealed class ThermalBar : MetricControl
{
    public ThermalBar() { MinHeight = 40; }
    protected override void OnRender(DrawingContext dc)
    {
        double w = Math.Max(0, ActualWidth - 2);
        var gradient = new LinearGradientBrush();
        foreach (var (level, offset) in new[] { (StatusLevel.Cool, 0d), (StatusLevel.Ok, .4), (StatusLevel.Warm, .7), (StatusLevel.Hot, 1d) }) gradient.GradientStops.Add(new GradientStop(Thresholds.Color(level), offset));
        gradient.Opacity = .25;
        dc.DrawRoundedRectangle(gradient, null, new Rect(0, 24, w, 10), 5, 5);
        dc.DrawRoundedRectangle(StatusBrush, null, new Rect(0, 24, w * Fraction, 10), 5, 5);
        var t = Thresholds.Table[SensorKind];
        foreach (double threshold in new[] { t.Warm, t.Hot })
        {
            double x = Math.Clamp(threshold / Math.Max(1, Maximum), 0, 1) * w;
            dc.DrawLine(new Pen(Brushes.White, 1), new Point(x, 20), new Point(x, 38));
        }
        Text(dc, Number, new Point(0, 0), 13);
    }
}
public sealed class SegmentedCapsule : MetricControl
{
    public SegmentedCapsule() { MinHeight = 36; }
    protected override void OnRender(DrawingContext dc)
    {
        double w = Math.Max(0, (ActualWidth - 19 * 3) / 20);
        for (int i = 0; i < 20; i++) dc.DrawRoundedRectangle(i < Math.Ceiling(Fraction * 20) ? StatusBrush : Track, null, new Rect(i * (w + 3), 24, w, 10), 3, 3);
        Text(dc, Number, new Point(0, 0), 13);
    }
}
public sealed class HeatGrid : FrameworkElement
{
    private readonly Dictionary<int, SolidColorBrush> tileBrushes = new();
    public static readonly DependencyProperty ValuesProperty = DependencyProperty.Register(nameof(Values), typeof(IEnumerable), typeof(HeatGrid), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));
    public IEnumerable? Values { get => (IEnumerable?)GetValue(ValuesProperty); set => SetValue(ValuesProperty, value); }
    public HeatGrid() { MinHeight = 80; Effect = Thresholds.Glow(StatusLevel.Cool); }
    protected override void OnRender(DrawingContext dc)
    {
        var values = Values?.Cast<object?>().ToArray() ?? [];
        int columns = Math.Max(1, Math.Min(8, values.Length)), rows = Math.Max(1, (values.Length + columns - 1) / columns);
        double w = Math.Max(0, (ActualWidth - (columns - 1) * 4) / columns), h = Math.Max(0, (ActualHeight - (rows - 1) * 4) / rows);
        for (int i = 0; i < values.Length; i++)
        {
            double? value = values[i] is double n && double.IsFinite(n) ? n : null;
            var rect = new Rect(i % columns * (w + 4), i / columns * (h + 4), w, h);
            var target = value.HasValue ? Thresholds.Color(Thresholds.Level(value.Value, SensorKind.Load)) : Colors.DimGray;
            if (!tileBrushes.TryGetValue(i, out var brush)) tileBrushes[i] = brush = new SolidColorBrush(target);
            if ((Color)brush.GetAnimationBaseValue(SolidColorBrush.ColorProperty) != target)
            {
                var from = brush.Color; brush.BeginAnimation(SolidColorBrush.ColorProperty, null); brush.Color = target;
                if (IsLoaded && !Motion.ReduceAnimations) brush.BeginAnimation(SolidColorBrush.ColorProperty, new ColorAnimation(from, target, TimeSpan.FromMilliseconds(380)));
            }
            dc.DrawRoundedRectangle(brush, null, rect, 4, 4);
            var text = new FormattedText(value.HasValue ? $"{i + 1} · {value:0}%" : $"{i + 1} · —", CultureInfo.InvariantCulture, FlowDirection.LeftToRight, new Typeface("Segoe UI"), 10, Brushes.Black, VisualTreeHelper.GetDpi(this).PixelsPerDip);
            dc.DrawText(text, rect.TopLeft + new Vector(4, 4));
        }
    }
}
