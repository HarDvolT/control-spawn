using System.Globalization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Effects;

namespace Prism.App.Controls;

/// <summary>The reference's sixteen-cell meter and gradient temperature rail.</summary>
public sealed class SegmentMeter : FrameworkElement
{
    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(nameof(Value), typeof(double), typeof(SegmentMeter), new FrameworkPropertyMetadata(double.NaN, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty ColorProperty = DependencyProperty.Register(nameof(Color), typeof(Color), typeof(SegmentMeter), new FrameworkPropertyMetadata(Colors.DeepSkyBlue, FrameworkPropertyMetadataOptions.AffectsRender));
    public double Value { get => (double)GetValue(ValueProperty); set => SetValue(ValueProperty, value); }
    public Color Color { get => (Color)GetValue(ColorProperty); set => SetValue(ColorProperty, value); }
    public int Segments { get; set; } = 16;
    protected override void OnRender(DrawingContext dc)
    {
        int count = Math.Max(1, Segments);
        double w = Math.Max(0, (ActualWidth - (count - 1) * 3) / count);
        for (int i = 0; i < count; i++)
            dc.DrawRoundedRectangle(new SolidColorBrush(double.IsFinite(Value) && i < Math.Ceiling(Math.Clamp(Value, 0, 100) / 100 * count) ? Color : System.Windows.Media.Color.FromRgb(30, 32, 38)), null, new Rect(i * (w + 3), 0, w, ActualHeight), 3, 3);
    }
}

public sealed class TemperatureRail : MetricControl
{
    protected override void OnRender(DrawingContext dc)
    {
        var track = new LinearGradientBrush();
        track.GradientStops.Add(new(System.Windows.Media.Color.FromArgb(64, 54, 214, 255), 0));
        track.GradientStops.Add(new(System.Windows.Media.Color.FromArgb(64, 61, 220, 132), .33));
        track.GradientStops.Add(new(System.Windows.Media.Color.FromArgb(64, 245, 165, 36), .66));
        track.GradientStops.Add(new(System.Windows.Media.Color.FromArgb(64, 255, 77, 79), 1));
        dc.DrawRoundedRectangle(track, null, new Rect(0, ActualHeight / 2 - 4, ActualWidth, 8), 4, 4);
        if (double.IsFinite(Value)) dc.DrawRoundedRectangle(StatusBrush, null, new Rect(Math.Clamp(Fraction * ActualWidth - 3, 0, Math.Max(0, ActualWidth - 6)), ActualHeight / 2 - 8, 6, 16), 3, 3);
    }
}

public sealed class NeedleDial : MetricControl
{
    public NeedleDial() { UseStatusGlow = false; }
    public static readonly DependencyProperty IsPumpProperty = DependencyProperty.Register(nameof(IsPump), typeof(bool), typeof(NeedleDial), new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender));
    public bool IsPump { get => (bool)GetValue(IsPumpProperty); set => SetValue(IsPumpProperty, value); }
    protected override void OnRender(DrawingContext dc)
    {
        var center = new Point(ActualWidth / 2, ActualHeight - 10);
        double r = Math.Max(0, Math.Min(ActualWidth / 2 - 15, ActualHeight - 30));
        Arc(dc, center, r, Math.PI, Math.PI, Track, 8);
        Arc(dc, center, r, Math.PI, Math.PI * Fraction, new SolidColorBrush((Color)ColorConverter.ConvertFromString(IsPump ? "#3DDC84" : "#36D6FF")), 8);
        if (!double.IsFinite(Value)) return;
        dc.DrawLine(new Pen(new SolidColorBrush(System.Windows.Media.Color.FromRgb(244, 245, 247)), 3) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round }, center, Polar(Math.PI + Fraction * Math.PI, r - 10, center));
        dc.DrawEllipse(Brushes.WhiteSmoke, null, center, 6, 6);
    }
}

public sealed class ThreadTile : MetricControl
{
    public ThreadTile() { UseStatusGlow = false; }
    protected override void OnRender(DrawingContext dc)
    {
        dc.DrawRoundedRectangle(Track, null, new Rect(0, 0, ActualWidth, ActualHeight), 5, 5);
        if (double.IsFinite(Value))
        {
            double h = ActualHeight * Fraction;
            dc.DrawRoundedRectangle(StatusBrush, null, new Rect(0, ActualHeight - h, ActualWidth, h), 5, 5);
        }
    }
}

public sealed class SignalRail : MetricControl
{
    protected override void OnRender(DrawingContext dc)
    {
        string[] colours = ["#FF4D4F", "#F5A524", "#3DDC84", "#36D6FF"];
        double w = Math.Max(0, (ActualWidth - 9) / 4);
        for (int i = 0; i < 4; i++)
        {
            var c = (Color)ColorConverter.ConvertFromString(colours[i]);
            dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromArgb(80, c.R, c.G, c.B)), null, new Rect(i * (w + 3), ActualHeight / 2 - 4, w, 8), 2, 2);
        }
        if (double.IsFinite(Value)) dc.DrawRoundedRectangle(Brushes.WhiteSmoke, null, new Rect(Math.Clamp(Fraction * ActualWidth - 3, 0, Math.Max(0, ActualWidth - 6)), ActualHeight / 2 - 8, 6, 16), 3, 3);
    }
}

public sealed class SignalBars : FrameworkElement
{
    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(nameof(Value), typeof(double), typeof(SignalBars), new FrameworkPropertyMetadata(double.NaN, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty BarColorProperty = DependencyProperty.Register(nameof(BarColor), typeof(Color), typeof(SignalBars), new FrameworkPropertyMetadata(Color.FromRgb(245, 165, 36), FrameworkPropertyMetadataOptions.AffectsRender));
    public double Value { get => (double)GetValue(ValueProperty); set => SetValue(ValueProperty, value); }
    public Color BarColor { get => (Color)GetValue(BarColorProperty); set => SetValue(BarColorProperty, value); }
    public SignalBars() { Width = 60; Height = 44; }
    protected override void OnRender(DrawingContext dc)
    {
        double gap = ActualWidth / 15, width = Math.Max(0, (ActualWidth - gap * 4) / 5);
        for (int i = 0; i < 5; i++)
        {
            double h = ActualHeight * (i + 1) / 5;
            dc.DrawRoundedRectangle(new SolidColorBrush(double.IsFinite(Value) && i < Value ? BarColor : Color.FromRgb(30, 32, 38)), null, new Rect(i * (width + gap), ActualHeight - h, width, h), 3, 3);
        }
    }
}
