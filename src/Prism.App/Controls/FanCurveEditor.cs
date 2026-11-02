using System.Globalization;
using System.Collections.Specialized;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using Prism.App.Common;
using Prism.Core;

namespace Prism.App.Controls;

/// <summary>
/// Eight draggable curve points with a live "now" marker, matching Fans.dc.html.
/// The bound list must be mutable (an ObservableCollection in the view-model).
/// </summary>
public class FanCurveEditor : FrameworkElement
{
    public static readonly DependencyProperty PointsProperty = DependencyProperty.Register(
        nameof(Points), typeof(IList<FanPoint>), typeof(FanCurveEditor),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender, OnPointsChanged));

    public static readonly DependencyProperty NowTemperatureProperty = DependencyProperty.Register(
        nameof(NowTemperature), typeof(double), typeof(FanCurveEditor),
        new FrameworkPropertyMetadata(double.NaN, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty NowPercentProperty = DependencyProperty.Register(
        nameof(NowPercent), typeof(double), typeof(FanCurveEditor),
        new FrameworkPropertyMetadata(double.NaN, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty AccentColorProperty = DependencyProperty.Register(
        nameof(AccentColor), typeof(Color), typeof(FanCurveEditor),
        new FrameworkPropertyMetadata(ColorUtil.Accent, FrameworkPropertyMetadataOptions.AffectsRender));

    private static readonly IReadOnlyList<FanPoint> Fallback =
    [
        new(20, 20), new(30, 26), new(40, 34), new(50, 45),
        new(60, 60), new(67, 80), new(70, 100), new(100, 100)
    ];

    private int _dragIndex = -1;
    public static readonly DependencyProperty SelectedIndexProperty = DependencyProperty.Register(nameof(SelectedIndex), typeof(int), typeof(FanCurveEditor), new FrameworkPropertyMetadata(2, FrameworkPropertyMetadataOptions.AffectsRender | FrameworkPropertyMetadataOptions.BindsTwoWayByDefault));
    public int SelectedIndex { get => (int)GetValue(SelectedIndexProperty); set => SetValue(SelectedIndexProperty, value); }
    private const double Pad = 12;

    private void PreferencesChanged(object? sender, EventArgs e) => InvalidateVisual();

    public FanCurveEditor()
    {
        Loaded += (_, _) => DisplayPreferences.Changed += PreferencesChanged;
        Unloaded += (_, _) => DisplayPreferences.Changed -= PreferencesChanged;
        MinHeight = 160;
        Cursor = Cursors.Cross;
        Focusable = false;
        SnapsToDevicePixels = true;
    }

    public IList<FanPoint>? Points
    {
        get => (IList<FanPoint>?)GetValue(PointsProperty);
        set => SetValue(PointsProperty, value);
    }

    public double NowTemperature
    {
        get => (double)GetValue(NowTemperatureProperty);
        set => SetValue(NowTemperatureProperty, value);
    }

    public double NowPercent
    {
        get => (double)GetValue(NowPercentProperty);
        set => SetValue(NowPercentProperty, value);
    }

    public Color AccentColor
    {
        get => (Color)GetValue(AccentColorProperty);
        set => SetValue(AccentColorProperty, value);
    }

    public event EventHandler? PointsChanged;

    private static void OnPointsChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not FanCurveEditor editor) return;
        if (e.OldValue is INotifyCollectionChanged oldPoints) CollectionChangedEventManager.RemoveHandler(oldPoints, editor.CurveChanged);
        if (e.NewValue is INotifyCollectionChanged newPoints) CollectionChangedEventManager.AddHandler(newPoints, editor.CurveChanged);
        editor.InvalidateVisual();
    }

    private void CurveChanged(object? sender, NotifyCollectionChangedEventArgs e) => InvalidateVisual();

    private IReadOnlyList<FanPoint> Current =>
        Points is { Count: > 0 } list ? list.ToArray() : Fallback;

    private Point Position(FanPoint point, double width, double height)
    {
        var x = Pad + Math.Clamp(point.Temperature, 0, 100) / 100 * Math.Max(1, width - Pad * 2);
        var y = Pad + (1 - Math.Clamp(point.Percent, 0, 100) / 100) * Math.Max(1, height - Pad * 2);
        return new Point(x, y);
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        var points = Points;
        if (points is null || points.Count == 0) return;
        var position = e.GetPosition(this);
        var best = -1;
        var bestDistance = 20.0;
        for (var i = 0; i < points.Count; i++)
        {
            var p = Position(points[i], ActualWidth, ActualHeight);
            var distance = (p - position).Length;
            if (distance < bestDistance) { bestDistance = distance; best = i; }
        }
        if (best < 0) return;
        _dragIndex = best;
        SelectedIndex = best;
        CaptureMouse();
        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (_dragIndex < 0) return;
        var points = Points;
        if (points is null || _dragIndex >= points.Count) return;

        var position = e.GetPosition(this);
        var width = Math.Max(1, ActualWidth - Pad * 2);
        var height = Math.Max(1, ActualHeight - Pad * 2);
        var temperature = Math.Clamp((position.X - Pad) / width * 100, 0, 100);
        var percent = Math.Clamp((1 - (position.Y - Pad) / height) * 100, 0, 100);

        var low = _dragIndex == 0 ? 0 : points[_dragIndex - 1].Temperature + 1;
        var high = _dragIndex == points.Count - 1 ? 100 : points[_dragIndex + 1].Temperature - 1;
        if (low > high) { low = high = points[_dragIndex].Temperature; }
        temperature = Math.Clamp(Math.Round(temperature), low, high);

        points[_dragIndex] = new FanPoint(temperature, Math.Round(percent));
        if (DataContext is Prism.App.ViewModels.FansViewModel model) model.OnCurveEdited();
        InvalidateVisual();
        PointsChanged?.Invoke(this, EventArgs.Empty);
        e.Handled = true;
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        if (_dragIndex < 0) return;
        _dragIndex = -1;
        ReleaseMouseCapture();
        e.Handled = true;
    }

    protected override void OnRender(DrawingContext dc)
    {
        var width = ActualWidth;
        var height = ActualHeight;
        if (width <= 4 || height <= 4) return;

        dc.DrawRoundedRectangle(ColorUtil.Brush(Color.FromRgb(13, 14, 18)), null, new Rect(Pad, Pad, Math.Max(0, width - Pad * 2), Math.Max(0, height - Pad * 2)), 12, 12);
        var grid = new Pen(ColorUtil.Brush(Color.FromArgb(12, 255, 255, 255)), 1);
        for (int i = 0; i <= 4; i++)
        {
            double gridX = Pad + i / 4d * (width - Pad * 2), y = Pad + i / 4d * (height - Pad * 2);
            dc.DrawLine(grid, new Point(Pad, y), new Point(width - Pad, y));
            dc.DrawLine(grid, new Point(gridX, Pad), new Point(gridX, height - Pad));
        }
        var points = Current;
        var geometry = BuildCurve(points, width, height, out var area);
        dc.DrawGeometry(ColorUtil.Brush(ColorUtil.WithAlpha(AccentColor, 0.18)), null, area);
        dc.DrawGeometry(null, new Pen(ColorUtil.Brush(ColorUtil.WithAlpha(AccentColor, 0.22)), 8)
        {
            LineJoin = PenLineJoin.Round,
            StartLineCap = PenLineCap.Round,
            EndLineCap = PenLineCap.Round
        }, geometry);
        dc.DrawGeometry(null, new Pen(ColorUtil.Brush(AccentColor), 2.4)
        {
            LineJoin = PenLineJoin.Round,
            StartLineCap = PenLineCap.Round,
            EndLineCap = PenLineCap.Round
        }, geometry);

        foreach (var point in points)
        {
            var p = Position(point, width, height);
            dc.DrawEllipse(ColorUtil.Brush(ColorUtil.WithAlpha(AccentColor, 0.28)), null, p, 10, 10);
            dc.DrawEllipse(ColorUtil.Brush(Color.FromRgb(10, 10, 11)), new Pen(ColorUtil.Brush(AccentColor), 2.5), p, 6.5, 6.5);
            if (SelectedIndex >= 0 && SelectedIndex < points.Count && points[SelectedIndex] == point) dc.DrawEllipse(null, new Pen(Brushes.WhiteSmoke, 3), p, 8, 8);
        }

        if (!double.IsFinite(NowTemperature)) return;
        var x = Pad + Math.Clamp(NowTemperature, 0, 100) / 100 * (width - Pad * 2);
        var dash = new Pen(ColorUtil.Brush(Color.FromArgb(150, 244, 244, 245)), 1.4)
        {
            DashStyle = new DashStyle([4, 4], 0)
        };
        dc.DrawLine(dash, new Point(x, Pad), new Point(x, height - Pad));

        var percent = double.IsFinite(NowPercent) ? NowPercent : PercentAt(points, NowTemperature);
        var text = $"{DisplayPreferences.Temperature(NowTemperature).ToString("F1", CultureInfo.InvariantCulture)}° · {Math.Round(percent).ToString("F0", CultureInfo.InvariantCulture)}%";
        var formatted = new FormattedText(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            Typefaces.Number(FontWeights.SemiBold),
            11.5, ColorUtil.Brush(Color.FromRgb(10, 10, 11)), VisualTreeHelper.GetDpi(this).PixelsPerDip);
        var chipWidth = formatted.Width + 18;
        var chipHeight = formatted.Height + 10;
        var chipX = Math.Clamp(x + 8, Pad, Math.Max(Pad, width - chipWidth - Pad));
        var chipY = Pad + 4;
        dc.DrawRoundedRectangle(ColorUtil.Brush(ColorUtil.TextPrimary), null,
            new Rect(chipX, chipY, chipWidth, chipHeight), chipHeight / 2, chipHeight / 2);
        dc.DrawText(formatted, new Point(chipX + 9, chipY + 5));
    }

    private static double PercentAt(IReadOnlyList<FanPoint> points, double temperature)
    {
        if (points.Count == 0) return 0;
        if (temperature <= points[0].Temperature) return points[0].Percent;
        for (var i = 1; i < points.Count; i++)
        {
            if (temperature > points[i].Temperature) continue;
            var a = points[i - 1];
            var b = points[i];
            var span = b.Temperature - a.Temperature;
            return span <= 0 ? b.Percent : a.Percent + (b.Percent - a.Percent) * (temperature - a.Temperature) / span;
        }
        return points[^1].Percent;
    }

    private static StreamGeometry BuildCurve(IReadOnlyList<FanPoint> points, double width, double height, out StreamGeometry area)
    {
        Point At(FanPoint p) => new(
            Pad + Math.Clamp(p.Temperature, 0, 100) / 100 * Math.Max(1, width - Pad * 2),
            Pad + (1 - Math.Clamp(p.Percent, 0, 100) / 100) * Math.Max(1, height - Pad * 2));

        var curve = new StreamGeometry();
        using (var context = curve.Open())
        {
            var first = At(points[0]);
            context.BeginFigure(first, false, false);
            for (var i = 1; i < points.Count; i++)
            {
                context.LineTo(At(points[i]), true, false);
            }
        }
        curve.Freeze();

        area = new StreamGeometry();
        using (var context = area.Open())
        {
            var first = At(points[0]);
            var last = At(points[^1]);
            context.BeginFigure(new Point(first.X, height - Pad), true, true);
            context.LineTo(first, true, false);
            for (var i = 1; i < points.Count; i++)
            {
                context.LineTo(At(points[i]), true, false);
            }
            context.LineTo(new Point(last.X, height - Pad), true, false);
        }
        area.Freeze();
        return curve;
    }
}
