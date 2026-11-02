using System.Globalization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using Prism.App.Common;

namespace Prism.App.Controls;

/// <summary>Animated ring with a glow, a tweening number and an optional caption.</summary>
public class RingGauge : FrameworkElement
{
    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(
        nameof(Value), typeof(double), typeof(RingGauge),
        new FrameworkPropertyMetadata(0d, FrameworkPropertyMetadataOptions.AffectsRender, OnValueChanged));

    public static readonly DependencyProperty MaximumProperty = DependencyProperty.Register(
        nameof(Maximum), typeof(double), typeof(RingGauge),
        new FrameworkPropertyMetadata(100d, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty RingColorProperty = DependencyProperty.Register(
        nameof(RingColor), typeof(Color), typeof(RingGauge),
        new FrameworkPropertyMetadata(ColorUtil.Accent, FrameworkPropertyMetadataOptions.AffectsRender, OnColorChanged));

    public static readonly DependencyProperty TrackColorProperty = DependencyProperty.Register(
        nameof(TrackColor), typeof(Color), typeof(RingGauge),
        new FrameworkPropertyMetadata(ColorUtil.Track, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty ThicknessProperty = DependencyProperty.Register(
        nameof(Thickness), typeof(double), typeof(RingGauge),
        new FrameworkPropertyMetadata(8d, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty ValueFormatProperty = DependencyProperty.Register(
        nameof(ValueFormat), typeof(string), typeof(RingGauge),
        new FrameworkPropertyMetadata("F0", FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty SuffixProperty = DependencyProperty.Register(
        nameof(Suffix), typeof(string), typeof(RingGauge),
        new FrameworkPropertyMetadata(string.Empty, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty CaptionProperty = DependencyProperty.Register(
        nameof(Caption), typeof(string), typeof(RingGauge),
        new FrameworkPropertyMetadata(string.Empty, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty ShowValueProperty = DependencyProperty.Register(
        nameof(ShowValue), typeof(bool), typeof(RingGauge),
        new FrameworkPropertyMetadata(true, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty SweepAngleProperty = DependencyProperty.Register(
        nameof(SweepAngle), typeof(double), typeof(RingGauge),
        new FrameworkPropertyMetadata(360d, FrameworkPropertyMetadataOptions.AffectsRender));
    public double SweepAngle { get => (double)GetValue(SweepAngleProperty); set => SetValue(SweepAngleProperty, value); }
    public static readonly DependencyProperty TickCountProperty = DependencyProperty.Register(nameof(TickCount), typeof(int), typeof(RingGauge), new FrameworkPropertyMetadata(0, FrameworkPropertyMetadataOptions.AffectsRender));
    public int TickCount { get => (int)GetValue(TickCountProperty); set => SetValue(TickCountProperty, value); }
    public bool TickOnFill { get; set; }
    public static readonly DependencyProperty ValueFontSizeProperty = DependencyProperty.Register(nameof(ValueFontSize), typeof(double), typeof(RingGauge), new FrameworkPropertyMetadata(double.NaN, FrameworkPropertyMetadataOptions.AffectsRender));
    public double ValueFontSize { get => (double)GetValue(ValueFontSizeProperty); set => SetValue(ValueFontSizeProperty, value); }

    private static readonly DependencyProperty DisplayValueProperty = DependencyProperty.Register(
        nameof(DisplayValue), typeof(double), typeof(RingGauge),
        new FrameworkPropertyMetadata(0d, FrameworkPropertyMetadataOptions.AffectsRender));

    public RingGauge()
    {
        Loaded += (_, _) => DisplayPreferences.Changed += PreferencesChanged;
        Unloaded += (_, _) => DisplayPreferences.Changed -= PreferencesChanged;
        SnapsToDevicePixels = true;
        UseLayoutRounding = true;
        MinWidth = 48;
        MinHeight = 48;
        Loaded += (_, _) =>
        {
            if (Value > 0 && DisplayValue <= 0) SetDisplay(Value, animate: true);
        };
    }

    public double Value { get => (double)GetValue(ValueProperty); set => SetValue(ValueProperty, value); }
    public double Maximum { get => (double)GetValue(MaximumProperty); set => SetValue(MaximumProperty, value); }
    public Color RingColor { get => (Color)GetValue(RingColorProperty); set => SetValue(RingColorProperty, value); }
    public Color TrackColor { get => (Color)GetValue(TrackColorProperty); set => SetValue(TrackColorProperty, value); }
    public double Thickness { get => (double)GetValue(ThicknessProperty); set => SetValue(ThicknessProperty, value); }
    public string ValueFormat { get => (string)GetValue(ValueFormatProperty); set => SetValue(ValueFormatProperty, value); }
    public string Suffix { get => (string)GetValue(SuffixProperty); set => SetValue(SuffixProperty, value); }
    public string Caption { get => (string)GetValue(CaptionProperty); set => SetValue(CaptionProperty, value); }
    public bool ShowValue { get => (bool)GetValue(ShowValueProperty); set => SetValue(ShowValueProperty, value); }

    private double DisplayValue { get => (double)GetValue(DisplayValueProperty); set => SetValue(DisplayValueProperty, value); }

    private static void OnValueChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is RingGauge gauge) gauge.SetDisplay((double)e.NewValue, animate: true);
    }

    private static void OnColorChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not RingGauge gauge) return;
        gauge.Effect = new DropShadowEffect
        {
            Color = gauge.RingColor,
            BlurRadius = 12,
            ShadowDepth = 0,
            Opacity = 0.5
        };
    }

    private void SetDisplay(double value, bool animate)
    {
        value = double.IsFinite(value) ? value : 0;
        if (!animate || Motion.ReduceAnimations || !IsLoaded)
        {
            BeginAnimation(DisplayValueProperty, null);
            DisplayValue = value;
            return;
        }
        BeginAnimation(DisplayValueProperty, new DoubleAnimation(value, TimeSpan.FromMilliseconds(800))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        });
    }

    protected override void OnRender(DrawingContext dc)
    {
        var size = Math.Min(ActualWidth, ActualHeight);
        if (size <= 0) return;
        var thickness = Math.Max(1, Math.Min(Thickness, size / 4));
        var radius = size / 2 - thickness / 2 - 2;
        if (radius <= 0) return;
        var center = new Point(ActualWidth / 2, ActualHeight / 2);

        var angle = Math.Clamp(SweepAngle, 1, 360);
        var startAngle = angle < 360 ? 90 + (360 - angle) / 2 : -90;
        if (angle >= 360) dc.DrawEllipse(null, new Pen(ColorUtil.Brush(TrackColor), thickness), center, radius, radius);
        else DrawArc(TrackColor, angle);

        void DrawArc(Color color, double degrees)
        {
            var arc = new StreamGeometry();
            using (var context = arc.Open())
            {
                context.BeginFigure(PointOnCircle(center, radius, startAngle), false, false);
                context.ArcTo(PointOnCircle(center, radius, startAngle + degrees), new Size(radius, radius), 0,
                    degrees > 180, SweepDirection.Clockwise, true, false);
            }
            arc.Freeze();
            dc.DrawGeometry(null, new Pen(ColorUtil.Brush(color), thickness) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round }, arc);
        }

        var maximum = Maximum <= 0 ? 100 : Maximum;
        var ratio = Math.Clamp(DisplayValue / maximum, 0, 1);
        var sweep = ratio * angle;
        if (sweep > 0.4)
        {
            sweep = Math.Min(sweep, 359.9);
            var start = PointOnCircle(center, radius, startAngle);
            var end = PointOnCircle(center, radius, startAngle + sweep);
            var geometry = new StreamGeometry();
            using (var context = geometry.Open())
            {
                context.BeginFigure(start, false, false);
                context.ArcTo(end, new Size(radius, radius), 0, sweep > 180, SweepDirection.Clockwise, true, false);
            }
            geometry.Freeze();
            var pen = new Pen(ColorUtil.Brush(RingColor), thickness)
            {
                StartLineCap = PenLineCap.Round,
                EndLineCap = PenLineCap.Round
            };
            dc.DrawGeometry(null, pen, geometry);
        }

        if (TickCount > 0)
            for (int i = 0; i < TickCount; i++)
            {
                if (!TickOnFill && double.IsFinite(Value) && i / (double)TickCount < Math.Clamp(Value / Maximum, 0, 1)) continue;
                var degrees = startAngle + angle * i / TickCount;
                dc.DrawLine(new Pen(ColorUtil.Brush(TickOnFill ? Color.FromArgb(70, 7, 8, 10) : Color.FromArgb(18, 255, 255, 255)), 1),
                    PointOnCircle(center, radius - thickness / 2, degrees), PointOnCircle(center, radius + thickness / 2, degrees));
            }
        if (!ShowValue)
        {
            if (!string.IsNullOrWhiteSpace(Caption))
            {
                var label = new FormattedText(Caption, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                    Typefaces.Number(FontWeights.SemiBold),
                    size * .18, ColorUtil.Brush(ColorUtil.TextPrimary), VisualTreeHelper.GetDpi(this).PixelsPerDip);
                dc.DrawText(label, new Point(center.X - label.Width / 2, center.Y - label.Height / 2));
            }
            return;
        }
        var valueText = double.IsFinite(Value) ? FormatValue() + (Suffix.Contains("\u00b0C") ? Suffix.Replace("\u00b0C", DisplayPreferences.Unit) : Suffix) : "\u2014";
        var valueSize = double.IsFinite(ValueFontSize) ? ValueFontSize : Math.Max(10, size * 0.25);
        var valueBrush = ColorUtil.Brush(ColorUtil.TextPrimary);
        var typeface = Typefaces.Number(FontWeights.SemiBold);
        var formatted = new FormattedText(valueText, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            typeface, valueSize, valueBrush, VisualTreeHelper.GetDpi(this).PixelsPerDip);
        var y = center.Y - formatted.Height / 2;
        if (!string.IsNullOrWhiteSpace(Caption)) y -= valueSize * 0.28;
        dc.DrawText(formatted, new Point(center.X - formatted.Width / 2, y));

        if (!string.IsNullOrWhiteSpace(Caption))
        {
            var caption = new FormattedText(Caption, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal),
                Math.Max(7, size * 0.09), ColorUtil.Brush(ColorUtil.TextSecondary),
                VisualTreeHelper.GetDpi(this).PixelsPerDip);
            dc.DrawText(caption, new Point(center.X - caption.Width / 2, y + formatted.Height + 1));
        }
    }

    private static Point PointOnCircle(Point center, double radius, double degrees)
    {
        var radians = degrees * Math.PI / 180;
        return new Point(center.X + radius * Math.Cos(radians), center.Y + radius * Math.Sin(radians));
    }

    private void PreferencesChanged(object? sender, EventArgs e) => InvalidateVisual();

    private string FormatValue()
    {
        if (!double.IsFinite(DisplayValue)) return "—";
        try { return (Suffix.Contains('\u00b0') ? DisplayPreferences.Temperature(DisplayValue) : DisplayValue).ToString(string.IsNullOrWhiteSpace(ValueFormat) ? "F0" : ValueFormat, CultureInfo.InvariantCulture); }
        catch (FormatException) { return (Suffix.Contains('\u00b0') ? DisplayPreferences.Temperature(DisplayValue) : DisplayValue).ToString("F0", CultureInfo.InvariantCulture); }
    }
}
