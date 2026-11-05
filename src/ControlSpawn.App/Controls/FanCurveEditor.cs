using System.Globalization;
using System.Collections.Specialized;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using ControlSpawn.App.Common;
using ControlSpawn.Core;
using ControlSpawn.Core.Ipc;

namespace ControlSpawn.App.Controls;

/// <summary>
/// Eight draggable curve points over 30–100 °C (the 9800X3D never idles below ~40 °C, and the safety guard owns 90 °C
/// and up), with the idle/gaming/overheat zones, where the temperature spent the last hour, the saved curve as a ghost
/// while editing, and rpm and noise labels once the fan has been measured. The bound list must be mutable.
/// </summary>
public class FanCurveEditor : FrameworkElement
{
    public const double TempMin = 30, TempMax = 100;
    private const double Left = 40, Right = 74, Top = 12, Bottom = 28;

    public static readonly DependencyProperty PointsProperty = DependencyProperty.Register(
        nameof(Points), typeof(IList<FanPoint>), typeof(FanCurveEditor),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender, OnPointsChanged));
    public static readonly DependencyProperty ReferenceProperty = DependencyProperty.Register(
        nameof(Reference), typeof(IReadOnlyList<FanPoint>), typeof(FanCurveEditor), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty NowTemperatureProperty = DependencyProperty.Register(
        nameof(NowTemperature), typeof(double), typeof(FanCurveEditor), new FrameworkPropertyMetadata(double.NaN, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty NowPercentProperty = DependencyProperty.Register(
        nameof(NowPercent), typeof(double), typeof(FanCurveEditor), new FrameworkPropertyMetadata(double.NaN, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty UsageLowProperty = DependencyProperty.Register(
        nameof(UsageLow), typeof(double), typeof(FanCurveEditor), new FrameworkPropertyMetadata(double.NaN, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty UsageHighProperty = DependencyProperty.Register(
        nameof(UsageHigh), typeof(double), typeof(FanCurveEditor), new FrameworkPropertyMetadata(double.NaN, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty RpmMapProperty = DependencyProperty.Register(
        nameof(RpmMap), typeof(FanRpmMap), typeof(FanCurveEditor), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty AccentColorProperty = DependencyProperty.Register(
        nameof(AccentColor), typeof(Color), typeof(FanCurveEditor), new FrameworkPropertyMetadata(ColorUtil.Accent, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty SelectedIndexProperty = DependencyProperty.Register(nameof(SelectedIndex), typeof(int), typeof(FanCurveEditor),
        new FrameworkPropertyMetadata(2, FrameworkPropertyMetadataOptions.AffectsRender | FrameworkPropertyMetadataOptions.BindsTwoWayByDefault));

    private int _dragIndex = -1;
    private void PreferencesChanged(object? sender, EventArgs e) => InvalidateVisual();

    public FanCurveEditor()
    {
        Loaded += (_, _) => DisplayPreferences.Changed += PreferencesChanged;
        Unloaded += (_, _) => DisplayPreferences.Changed -= PreferencesChanged;
        MinHeight = 200; Cursor = Cursors.Cross; Focusable = false; SnapsToDevicePixels = true;
    }

    public IList<FanPoint>? Points { get => (IList<FanPoint>?)GetValue(PointsProperty); set => SetValue(PointsProperty, value); }
    /// <summary>The saved curve, drawn faintly while the live one differs from it.</summary>
    public IReadOnlyList<FanPoint>? Reference { get => (IReadOnlyList<FanPoint>?)GetValue(ReferenceProperty); set => SetValue(ReferenceProperty, value); }
    public double NowTemperature { get => (double)GetValue(NowTemperatureProperty); set => SetValue(NowTemperatureProperty, value); }
    public double NowPercent { get => (double)GetValue(NowPercentProperty); set => SetValue(NowPercentProperty, value); }
    /// <summary>Where the temperature spent most of the last hour (10th–90th percentile).</summary>
    public double UsageLow { get => (double)GetValue(UsageLowProperty); set => SetValue(UsageLowProperty, value); }
    public double UsageHigh { get => (double)GetValue(UsageHighProperty); set => SetValue(UsageHighProperty, value); }
    public FanRpmMap? RpmMap { get => (FanRpmMap?)GetValue(RpmMapProperty); set => SetValue(RpmMapProperty, value); }
    public Color AccentColor { get => (Color)GetValue(AccentColorProperty); set => SetValue(AccentColorProperty, value); }
    public int SelectedIndex { get => (int)GetValue(SelectedIndexProperty); set => SetValue(SelectedIndexProperty, value); }
    public event EventHandler? PointsChanged;

    private static void OnPointsChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not FanCurveEditor editor) return;
        if (e.OldValue is INotifyCollectionChanged oldPoints) CollectionChangedEventManager.RemoveHandler(oldPoints, editor.CurveChanged);
        if (e.NewValue is INotifyCollectionChanged newPoints) CollectionChangedEventManager.AddHandler(newPoints, editor.CurveChanged);
        editor.InvalidateVisual();
    }
    private void CurveChanged(object? sender, NotifyCollectionChangedEventArgs e) => InvalidateVisual();

    private double PlotWidth => Math.Max(1, ActualWidth - Left - Right);
    private double PlotHeight => Math.Max(1, ActualHeight - Top - Bottom);
    private double X(double temperature) => Left + (Math.Clamp(temperature, TempMin, TempMax) - TempMin) / (TempMax - TempMin) * PlotWidth;
    private double Y(double percent) => Top + (1 - Math.Clamp(percent, 0, 100) / 100) * PlotHeight;
    private Point At(FanPoint p) => new(X(p.Temperature), Y(p.Percent));

    /// <summary>Rpm at a duty from the measured map (linear between steps), or null when the fan hasn't been measured.</summary>
    public static double? RpmAt(FanRpmMap? map, double percent)
    {
        if (map is not { Points.Count: > 1 } m) return null;
        var p = m.Points;
        if (percent <= p[0].Percent) return p[0].Rpm;
        for (int i = 1; i < p.Count; i++)
            if (percent <= p[i].Percent) return p[i - 1].Rpm + (p[i].Rpm - p[i - 1].Rpm) * (percent - p[i - 1].Percent) / Math.Max(1e-6, p[i].Percent - p[i - 1].Percent);
        return p[^1].Rpm;
    }
    /// <summary>How a 120 mm fan sounds at this speed, roughly.</summary>
    public static string Noise(double rpm) => rpm < 900 ? "silent" : rpm < 1300 ? "quiet" : rpm < 1700 ? "audible" : "loud";

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        var points = Points;
        if (points is null || points.Count == 0) return;
        var position = e.GetPosition(this);
        int best = -1; double bestDistance = 20;
        for (var i = 0; i < points.Count; i++)
        {
            var distance = (At(points[i]) - position).Length;
            if (distance < bestDistance) { bestDistance = distance; best = i; }
        }
        if (best < 0) return;
        _dragIndex = best; SelectedIndex = best;
        if (DataContext is ViewModels.FansViewModel model) model.BeginEdit();
        CaptureMouse(); e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (_dragIndex < 0 || Points is not { } points || _dragIndex >= points.Count) return;
        var position = e.GetPosition(this);
        var temperature = TempMin + (position.X - Left) / PlotWidth * (TempMax - TempMin);
        var percent = (1 - (position.Y - Top) / PlotHeight) * 100;
        var low = _dragIndex == 0 ? TempMin : points[_dragIndex - 1].Temperature + 1;
        var high = _dragIndex == points.Count - 1 ? TempMax : points[_dragIndex + 1].Temperature - 1;
        if (low > high) low = high = points[_dragIndex].Temperature;
        points[_dragIndex] = new FanPoint(Math.Clamp(Math.Round(temperature), low, high), Math.Clamp(Math.Round(percent), RpmMap?.StopsBelow ?? 0, 100));
        if (DataContext is ViewModels.FansViewModel model) model.OnCurveEdited();
        InvalidateVisual(); PointsChanged?.Invoke(this, EventArgs.Empty); e.Handled = true;
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        if (_dragIndex < 0) return;
        _dragIndex = -1; ReleaseMouseCapture(); e.Handled = true;
    }

    private FormattedText Text(string text, double size, Color color, bool bold = false) => new(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
        bold ? Typefaces.Number(FontWeights.SemiBold) : new Typeface("Segoe UI Variable"), size, ColorUtil.Brush(color), VisualTreeHelper.GetDpi(this).PixelsPerDip);

    protected override void OnRender(DrawingContext dc)
    {
        double width = ActualWidth, height = ActualHeight;
        if (width <= Left + Right + 4 || height <= Top + Bottom + 4) return;
        var plot = new Rect(Left, Top, PlotWidth, PlotHeight);
        var muted = Color.FromRgb(124, 128, 138);
        dc.DrawRoundedRectangle(ColorUtil.Brush(Color.FromRgb(13, 14, 18)), null, plot, 10, 10);
        dc.PushClip(new RectangleGeometry(plot, 10, 10));

        // Zones first, so the curve reads against what each part of the range means.
        void Zone(double from, double to, Color color, string label)
        {
            var rect = new Rect(new Point(X(from), Top), new Point(X(to), Top + PlotHeight));
            dc.DrawRectangle(ColorUtil.Brush(Color.FromArgb(16, color.R, color.G, color.B)), null, rect);
            var text = Text(label, 9.5, Color.FromArgb(150, color.R, color.G, color.B), true);
            if (text.Width + 8 < rect.Width) dc.DrawText(text, new Point(rect.X + 6, Top + PlotHeight - text.Height - 4));
        }
        Zone(40, 55, Color.FromRgb(54, 214, 255), "IDLE");
        Zone(60, 80, Color.FromRgb(245, 165, 36), "GAMING");
        Zone(90, TempMax, Color.FromRgb(255, 77, 79), "FULL SPEED");

        var grid = new Pen(ColorUtil.Brush(Color.FromArgb(14, 255, 255, 255)), 1);
        for (int i = 1; i < 4; i++) dc.DrawLine(grid, new Point(Left, Y(i * 25)), new Point(Left + PlotWidth, Y(i * 25)));
        for (double t = 40; t < TempMax; t += 10) dc.DrawLine(grid, new Point(X(t), Top), new Point(X(t), Top + PlotHeight));

        // Below the measured floor the fan stops: hatch it so a point dragged there reads as "off".
        if (RpmMap?.StopsBelow is { } stops)
        {
            var floor = new Rect(new Point(Left, Y(stops)), new Point(Left + PlotWidth, Top + PlotHeight));
            dc.DrawRectangle(ColorUtil.Brush(Color.FromArgb(28, 0, 0, 0)), null, floor);
            var hatch = new Pen(ColorUtil.Brush(Color.FromArgb(22, 255, 255, 255)), 1);
            for (double x = floor.Left - floor.Height; x < floor.Right; x += 10) dc.DrawLine(hatch, new Point(x, floor.Bottom), new Point(x + floor.Height, floor.Top));
            var text = Text($"fan stops below {stops:0}%", 9.5, muted);
            dc.DrawText(text, new Point(Left + PlotWidth - text.Width - 8, floor.Top + 3));
        }

        // Where the temperature actually was this past hour: the part of the curve that matters.
        if (double.IsFinite(UsageLow) && double.IsFinite(UsageHigh) && UsageHigh > TempMin)
        {
            double from = X(UsageLow), to = Math.Max(X(UsageHigh), from + 3);
            dc.DrawRoundedRectangle(ColorUtil.Brush(Color.FromArgb(70, 244, 244, 245)), null, new Rect(from, Top + PlotHeight - 5, to - from, 5), 2, 2);
            var label = Text("last hour", 9.5, Color.FromArgb(170, 244, 244, 245));
            if (to - from > label.Width + 6) dc.DrawText(label, new Point(from + (to - from - label.Width) / 2, Top + PlotHeight - 5 - label.Height - 1));
        }

        var points = Points is { Count: > 1 } list ? list.ToArray() : [];
        if (Reference is { Count: > 1 } saved && points.Length > 1 && !saved.SequenceEqual(points))
            dc.DrawGeometry(null, new Pen(ColorUtil.Brush(Color.FromArgb(110, 244, 244, 245)), 1.4) { DashStyle = new DashStyle([3, 3], 0) }, Line(saved, false));
        if (points.Length > 1)
        {
            dc.DrawGeometry(ColorUtil.Brush(ColorUtil.WithAlpha(AccentColor, 0.16)), null, Line(points, true));
            var line = Line(points, false);
            dc.DrawGeometry(null, new Pen(ColorUtil.Brush(ColorUtil.WithAlpha(AccentColor, 0.22)), 8) { LineJoin = PenLineJoin.Round }, line);
            dc.DrawGeometry(null, new Pen(ColorUtil.Brush(AccentColor), 2.4) { LineJoin = PenLineJoin.Round }, line);
        }
        dc.Pop();

        for (int i = 0; i < points.Length; i++)
        {
            var p = At(points[i]);
            dc.DrawEllipse(ColorUtil.Brush(ColorUtil.WithAlpha(AccentColor, 0.28)), null, p, 10, 10);
            dc.DrawEllipse(ColorUtil.Brush(Color.FromRgb(10, 10, 11)), new Pen(ColorUtil.Brush(AccentColor), 2.5), p, 6.5, 6.5);
            if (i == SelectedIndex) dc.DrawEllipse(null, new Pen(Brushes.WhiteSmoke, 3), p, 8, 8);
        }

        // Axes: percent (and noise, once measured) on the left and right, temperature underneath.
        foreach (var percent in new[] { 0, 25, 50, 75, 100 })
        {
            var text = Text($"{percent}%", 10.5, muted);
            dc.DrawText(text, new Point(Left - text.Width - 8, Math.Clamp(Y(percent) - text.Height / 2, 0, height - text.Height)));
        }
        foreach (var t in new[] { 30, 40, 50, 60, 70, 80, 90, 100 })
        {
            var text = Text(DisplayPreferences.Fahrenheit ? $"{t * 9 / 5 + 32:0}°" : $"{t}°", 10.5, muted);
            dc.DrawText(text, new Point(Math.Clamp(X(t) - text.Width / 2, Left - 4, width - Right - text.Width + 6), Top + PlotHeight + 7));
        }
        if (RpmMap is { } map)
        {
            // Noise bands up the right edge, from the measured rpm.
            string? last = null; double bandStart = 0;
            for (int percent = 0; percent <= 101; percent++)
            {
                string? noise = percent <= 100 && RpmAt(map, percent) is { } rpm && rpm > 0 ? Noise(rpm) : percent <= 100 ? "off" : null;
                if (noise == last) continue;
                if (last is not null && percent - bandStart >= 6)
                {
                    var text = Text(last, 10, last switch { "loud" => Color.FromRgb(255, 138, 138), "audible" => Color.FromRgb(245, 165, 36), _ => muted });
                    dc.DrawText(text, new Point(Left + PlotWidth + 10, Math.Clamp(Y((bandStart + percent) / 2d) - text.Height / 2, 0, height - text.Height)));
                    dc.DrawLine(new Pen(ColorUtil.Brush(Color.FromArgb(40, 255, 255, 255)), 1), new Point(Left + PlotWidth + 4, Y(bandStart)), new Point(Left + PlotWidth + 4, Y(percent)));
                }
                last = noise; bandStart = percent;
            }
        }

        // The selected point's values, and where the fan is now.
        if (SelectedIndex >= 0 && SelectedIndex < points.Length && _dragIndex >= 0)
            Chip(points[SelectedIndex].Temperature, points[SelectedIndex].Percent, At(points[SelectedIndex]), true);
        if (double.IsFinite(NowTemperature))
        {
            var x = X(NowTemperature);
            dc.DrawLine(new Pen(ColorUtil.Brush(Color.FromArgb(150, 244, 244, 245)), 1.4) { DashStyle = new DashStyle([4, 4], 0) }, new Point(x, Top), new Point(x, Top + PlotHeight));
            var percent = double.IsFinite(NowPercent) ? NowPercent : ViewModels.FansViewModel.DutyAt(points, NowTemperature);
            Chip(NowTemperature, percent, new Point(x, Top + 4), false);
        }

        void Chip(double temperature, double percent, Point anchor, bool nearPoint)
        {
            var rpm = RpmAt(RpmMap, percent);
            var label = $"{DisplayPreferences.Temperature(temperature).ToString("F0", CultureInfo.InvariantCulture)}° · {Math.Round(percent):0}%" + (rpm is { } r ? $" · {r:N0} rpm" : "");
            var text = Text(label, 11.5, Color.FromRgb(10, 10, 11), true);
            double chipWidth = text.Width + 18, chipHeight = text.Height + 10;
            double chipX = Math.Clamp(anchor.X + 8, Left, Math.Max(Left, Left + PlotWidth - chipWidth));
            double chipY = nearPoint ? Math.Clamp(anchor.Y - chipHeight - 12, Top, Top + PlotHeight - chipHeight) : anchor.Y;
            dc.DrawRoundedRectangle(ColorUtil.Brush(ColorUtil.TextPrimary), null, new Rect(chipX, chipY, chipWidth, chipHeight), chipHeight / 2, chipHeight / 2);
            dc.DrawText(text, new Point(chipX + 9, chipY + 5));
        }
    }

    private StreamGeometry Line(IReadOnlyList<FanPoint> points, bool area)
    {
        var geometry = new StreamGeometry();
        using (var context = geometry.Open())
        {
            // The curve holds its first and last values outside the points, as the service reads it.
            var first = new Point(Left, Y(points[0].Percent)); var last = new Point(Left + PlotWidth, Y(points[^1].Percent));
            context.BeginFigure(area ? new Point(Left, Top + PlotHeight) : first, area, area);
            if (area) context.LineTo(first, true, false);
            foreach (var point in points) context.LineTo(At(point), true, false);
            context.LineTo(last, true, false);
            if (area) context.LineTo(new Point(Left + PlotWidth, Top + PlotHeight), true, false);
        }
        geometry.Freeze();
        return geometry;
    }
}
