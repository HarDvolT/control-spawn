using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Prism.App.Common;

namespace Prism.App.Controls;

/// <summary>The shell's dot grid and two drifting light fields, drawn without bitmap assets.</summary>
public sealed class GraphiteBackground : FrameworkElement
{
    private readonly TranslateTransform drift = new();
    public GraphiteBackground()
    {
        Loaded += (_, _) => UpdateMotion();
        Unloaded += (_, _) => drift.BeginAnimation(TranslateTransform.XProperty, null);
        SystemParameters.StaticPropertyChanged += PreferenceChanged;
    }
    private void PreferenceChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    { if (IsLoaded) Dispatcher.BeginInvoke(UpdateMotion); }
    private void UpdateMotion()
    {
        drift.BeginAnimation(TranslateTransform.XProperty, null);
        if (!Motion.ReduceAnimations)
            drift.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(0, 60, TimeSpan.FromSeconds(24))
            { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever });
    }
    protected override void OnRender(DrawingContext dc)
    {
        dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(7, 8, 10)), null, new Rect(RenderSize));
        var dots = new SolidColorBrush(Color.FromArgb(8, 255, 255, 255));
        for (double x = 0; x < ActualWidth; x += 24)
            for (double y = 0; y < ActualHeight; y += 24) dc.DrawEllipse(dots, null, new Point(x, y), 1, 1);
        dc.PushTransform(drift);
        Glow(dc, new Point(140, 80), 390, Color.FromArgb(28, 255, 77, 26));
        Glow(dc, new Point(ActualWidth - 100, 430), 465, Color.FromArgb(22, 54, 214, 255));
        dc.Pop();
    }
    private static void Glow(DrawingContext dc, Point at, double radius, Color color)
    {
        var brush = new RadialGradientBrush(color, Colors.Transparent);
        dc.DrawEllipse(brush, null, at, radius, radius);
    }
}

public sealed class LiveIndicator : FrameworkElement
{
    public static readonly DependencyProperty ColorProperty = DependencyProperty.Register(nameof(Color), typeof(Color), typeof(LiveIndicator),
        new FrameworkPropertyMetadata(Colors.MediumSeaGreen, FrameworkPropertyMetadataOptions.AffectsRender));
    public Color Color { get => (Color)GetValue(ColorProperty); set => SetValue(ColorProperty, value); }
    public LiveIndicator()
    {
        VerticalAlignment = VerticalAlignment.Center;
        Loaded += (_, _) =>
        {
            if (!Motion.ReduceAnimations) BeginAnimation(OpacityProperty, new DoubleAnimation(1, .35, TimeSpan.FromSeconds(.8))
            { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever });
        };
        Unloaded += (_, _) => BeginAnimation(OpacityProperty, null);
    }
    protected override void OnRender(DrawingContext dc)
    {
        dc.DrawEllipse(new SolidColorBrush(Color), null, new Point(ActualWidth / 2, ActualHeight / 2), ActualWidth / 2, ActualHeight / 2);
    }
}
