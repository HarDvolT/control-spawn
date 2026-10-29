using System.Collections;
using System.Collections.Specialized;
using System.Globalization;
using System.Windows;
using System.Windows.Media;
using Prism.App.Common;

namespace Prism.App.Controls;

/// <summary>Smooth curve on a dotted grid; supports one or two series, optional area fill and glow.</summary>
public class LineChart : FrameworkElement
{
    public bool HorizontalGrid { get; set; }
    public bool Compact { get; set; }
    public bool Linear { get; set; }
    public static readonly DependencyProperty ValuesProperty = DependencyProperty.Register(
        nameof(Values), typeof(IEnumerable), typeof(LineChart),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender, OnValuesChanged));

    public static readonly DependencyProperty SecondaryValuesProperty = DependencyProperty.Register(
        nameof(SecondaryValues), typeof(IEnumerable), typeof(LineChart),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender, OnValuesChanged));

    public static readonly DependencyProperty ColorProperty = DependencyProperty.Register(
        nameof(Color), typeof(Color), typeof(LineChart),
        new FrameworkPropertyMetadata(ColorUtil.TextPrimary, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty SecondaryColorProperty = DependencyProperty.Register(
        nameof(SecondaryColor), typeof(Color), typeof(LineChart),
        new FrameworkPropertyMetadata(ColorUtil.Accent, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty MaximumProperty = DependencyProperty.Register(
        nameof(Maximum), typeof(double), typeof(LineChart),
        new FrameworkPropertyMetadata(100d, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty AutoScaleProperty = DependencyProperty.Register(
        nameof(AutoScale), typeof(bool), typeof(LineChart),
        new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty ShowGridProperty = DependencyProperty.Register(
        nameof(ShowGrid), typeof(bool), typeof(LineChart),
        new FrameworkPropertyMetadata(true, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty FillAreaProperty = DependencyProperty.Register(
        nameof(FillArea), typeof(bool), typeof(LineChart),
        new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty StrokeThicknessProperty = DependencyProperty.Register(
        nameof(StrokeThickness), typeof(double), typeof(LineChart),
        new FrameworkPropertyMetadata(2d, FrameworkPropertyMetadataOptions.AffectsRender));

    private static readonly Brush GridBrush = ColorUtil.Brush(Color.FromArgb(38, 255, 255, 255));
    private INotifyCollectionChanged? _valuesHook;
    private INotifyCollectionChanged? _secondaryHook;

    public LineChart()
    {
        SnapsToDevicePixels = true;
        UseLayoutRounding = true;
        MinHeight = 40;
    }

    public IEnumerable? Values { get => (IEnumerable?)GetValue(ValuesProperty); set => SetValue(ValuesProperty, value); }
    public IEnumerable? SecondaryValues { get => (IEnumerable?)GetValue(SecondaryValuesProperty); set => SetValue(SecondaryValuesProperty, value); }
    public Color Color { get => (Color)GetValue(ColorProperty); set => SetValue(ColorProperty, value); }
    public Color SecondaryColor { get => (Color)GetValue(SecondaryColorProperty); set => SetValue(SecondaryColorProperty, value); }
    public double Maximum { get => (double)GetValue(MaximumProperty); set => SetValue(MaximumProperty, value); }
    public bool AutoScale { get => (bool)GetValue(AutoScaleProperty); set => SetValue(AutoScaleProperty, value); }
    public bool ShowGrid { get => (bool)GetValue(ShowGridProperty); set => SetValue(ShowGridProperty, value); }
    public bool FillArea { get => (bool)GetValue(FillAreaProperty); set => SetValue(FillAreaProperty, value); }
    public double StrokeThickness { get => (double)GetValue(StrokeThicknessProperty); set => SetValue(StrokeThicknessProperty, value); }

    private static void OnValuesChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not LineChart chart) return;
        chart.Hook(e.OldValue as IEnumerable, e.NewValue as IEnumerable, secondary: e.Property == SecondaryValuesProperty);
        chart.InvalidateVisual();
    }

    private void Hook(IEnumerable? oldValues, IEnumerable? newValues, bool secondary)
    {
        var oldHook = secondary ? _secondaryHook : _valuesHook;
        if (oldHook is not null) CollectionChangedEventManager.RemoveHandler(oldHook, OnCollectionChanged);
        var hook = newValues as INotifyCollectionChanged;
        if (hook is not null) CollectionChangedEventManager.AddHandler(hook, OnCollectionChanged);
        if (secondary) _secondaryHook = hook; else _valuesHook = hook;
    }

    private void OnCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e) => InvalidateVisual();

    protected override void OnRender(DrawingContext dc)
    {
        var width = ActualWidth;
        var height = ActualHeight;
        if (width <= 1 || height <= 1) return;

        if (ShowGrid)
        {
            if (HorizontalGrid)
                for (int i = 1; i < 4; i++) dc.DrawLine(new Pen(ColorUtil.Brush(Color.FromArgb(12, 255, 255, 255)), 1), new Point(0, height * i / 4), new Point(width, height * i / 4));
            else
                for (var x = 8.0; x < width; x += 16)
                    for (var y = 8.0; y < height; y += 16)
                        dc.DrawEllipse(GridBrush, null, new Point(x, y), 0.8, 0.8);
        }

        var first = ToPoints(Values);
        var second = ToPoints(SecondaryValues);
        var maximum = Maximum <= 0 ? 100 : Maximum;
        if (AutoScale)
        {
            var peak = first.Concat(second).Where(double.IsFinite).DefaultIfEmpty(0).Max();
            maximum = peak <= 0 ? 1 : peak * 1.15;
        }

        DrawSeries(dc, first, Color, width, height, maximum, FillArea);
        DrawSeries(dc, second, SecondaryColor, width, height, maximum, false);
    }

    private void DrawSeries(DrawingContext dc, IReadOnlyList<double> points, Color color, double width, double height,
        double maximum, bool fill)
    {
        if (points.Any(p => !double.IsFinite(p)))
        {
            var run = new List<double>();
            int start = 0;
            for (int index = 0; index <= points.Count; index++)
            {
                var value = index < points.Count ? points[index] : double.NaN;
                if (double.IsFinite(value)) { if (run.Count == 0) start = index; run.Add(value); }
                else
                {
                    if (run.Count > 1)
                    {
                        dc.PushTransform(new TranslateTransform(width * start / Math.Max(1, points.Count - 1), 0));
                        DrawSeries(dc, run, color, width * (run.Count - 1) / Math.Max(1, points.Count - 1), height, maximum, fill);
                        dc.Pop();
                    }
                    run.Clear();
                }
            }
            return;
        }
        if (points.Count < 2) return;
        if (Compact || Linear)
        {
            double min = Compact ? points.Min() : 0, max = Compact ? points.Max() : maximum;
            var linearGeometry = new StreamGeometry();
            using (var context = linearGeometry.Open())
                for (int i = 0; i < points.Count; i++)
                {
                    double fraction = max == min ? .5 : Math.Clamp((points[i] - min) / (max - min), 0, 1);
                    var point = new Point(i * width / (points.Count - 1), height - (Compact ? 3 : 8) - fraction * Math.Max(0, height - (Compact ? 6 : 24)));
                    if (i == 0) context.BeginFigure(point, false, false); else context.LineTo(point, true, false);
                }
            dc.DrawGeometry(null, new Pen(ColorUtil.Brush(ColorUtil.WithAlpha(color, .12)), 8), linearGeometry);
            dc.DrawGeometry(null, new Pen(ColorUtil.Brush(color), StrokeThickness) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round }, linearGeometry);
            return;
        }
        var geometry = BuildGeometry(points, width, height, maximum);
        var thickness = Math.Max(1, StrokeThickness);

        var glowColor = ColorUtil.WithAlpha(color, 0.18);
        var glowPen = new Pen(ColorUtil.Brush(glowColor), thickness + 5)
        {
            StartLineCap = PenLineCap.Round,
            EndLineCap = PenLineCap.Round,
            LineJoin = PenLineJoin.Round
        };
        dc.DrawGeometry(null, glowPen, geometry);

        if (fill)
        {
            var area = BuildArea(points, width, height, maximum);
            dc.DrawGeometry(ColorUtil.Brush(ColorUtil.WithAlpha(color, 0.08)), null, area);
        }

        var pen = new Pen(ColorUtil.Brush(color), thickness)
        {
            StartLineCap = PenLineCap.Round,
            EndLineCap = PenLineCap.Round,
            LineJoin = PenLineJoin.Round
        };
        dc.DrawGeometry(null, pen, geometry);

        var lastY = height - 8 - (Math.Clamp(points[^1] / maximum, 0, 1) * (height - 24));
        dc.DrawEllipse(ColorUtil.Brush(color), null, new Point(width - 2, lastY), 2.4, 2.4);
    }

    private static StreamGeometry BuildGeometry(IReadOnlyList<double> points, double width, double height, double maximum)
    {
        var step = width / Math.Max(1, points.Count - 1);
        Point At(int index) => new(index * step, height - 8 - Math.Clamp(points[index] / maximum, 0, 1) * (height - 24));

        var geometry = new StreamGeometry();
        using (var context = geometry.Open())
        {
            var first = At(0);
            context.BeginFigure(first, false, false);
            for (var i = 1; i < points.Count; i++)
            {
                var previous = At(i - 1);
                var current = At(i);
                var mid = (previous.X + current.X) / 2;
                context.BezierTo(new Point(mid, previous.Y), new Point(mid, current.Y), current, true, false);
            }
        }
        geometry.Freeze();
        return geometry;
    }

    private static StreamGeometry BuildArea(IReadOnlyList<double> points, double width, double height, double maximum)
    {
        var step = width / Math.Max(1, points.Count - 1);
        Point At(int index) => new(index * step, height - 8 - Math.Clamp(points[index] / maximum, 0, 1) * (height - 24));

        var geometry = new StreamGeometry();
        using (var context = geometry.Open())
        {
            var first = At(0);
            context.BeginFigure(new Point(first.X, height), true, true);
            context.LineTo(first, true, false);
            for (var i = 1; i < points.Count; i++)
            {
                var previous = At(i - 1);
                var current = At(i);
                var mid = (previous.X + current.X) / 2;
                context.BezierTo(new Point(mid, previous.Y), new Point(mid, current.Y), current, true, false);
            }
            context.LineTo(new Point(At(points.Count - 1).X, height), true, false);
        }
        geometry.Freeze();
        return geometry;
    }

    private static List<double> ToPoints(IEnumerable? source)
    {
        var result = new List<double>();
        if (source is null) return result;
        foreach (var item in source)
        {
            result.Add(item switch
            {
                null => double.NaN,
                double d when !double.IsFinite(d) => double.NaN,
                double d when double.IsFinite(d) => d,
                float f when float.IsFinite(f) => f,
                int i => i,
                long l => l,
                _ => double.TryParse(System.Convert.ToString(item, CultureInfo.InvariantCulture),
                    NumberStyles.Any, CultureInfo.InvariantCulture, out var parsed) ? parsed : double.NaN
            });
        }
        return result;
    }
}

/// <summary>Small single-series line chart (same drawing code as <see cref="LineChart"/>).</summary>
public sealed class Sparkline : LineChart
{
    public Sparkline() { Compact = true; ShowGrid = false; Height = 32; MinHeight = 16; }
}
