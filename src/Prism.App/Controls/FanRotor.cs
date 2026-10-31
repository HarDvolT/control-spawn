using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Prism.App.Common;

namespace Prism.App.Controls;

/// <summary>Decorative rotor; the design's rotation period falls as live RPM rises.</summary>
public sealed class FanRotor : FrameworkElement
{
    public static readonly DependencyProperty SpinSecondsProperty = DependencyProperty.Register(
        nameof(SpinSeconds), typeof(double), typeof(FanRotor), new PropertyMetadata(2d, Restart));
    public static readonly DependencyProperty ColorProperty = DependencyProperty.Register(
        nameof(Color), typeof(Color), typeof(FanRotor), new FrameworkPropertyMetadata(ColorUtil.Accent, FrameworkPropertyMetadataOptions.AffectsRender));
    private readonly RotateTransform _rotation = new();
    public FanRotor()
    {
        Width = Height = 34;
        RenderTransformOrigin = new Point(0.5, 0.5);
        RenderTransform = _rotation;
        Loaded += (_, _) => Animate();
        Unloaded += (_, _) => _rotation.BeginAnimation(RotateTransform.AngleProperty, null);
    }
    public double SpinSeconds { get => (double)GetValue(SpinSecondsProperty); set => SetValue(SpinSecondsProperty, value); }
    public Color Color { get => (Color)GetValue(ColorProperty); set => SetValue(ColorProperty, value); }
    private static void Restart(DependencyObject d, DependencyPropertyChangedEventArgs e) => ((FanRotor)d).Animate();
    private void Animate()
    {
        _rotation.BeginAnimation(RotateTransform.AngleProperty, null);
        if (!IsLoaded || Motion.ReduceAnimations || !double.IsFinite(SpinSeconds) || SpinSeconds <= 0) return;
        _rotation.BeginAnimation(RotateTransform.AngleProperty, Motion.Decorative(new DoubleAnimation(0, 360,
            TimeSpan.FromSeconds(SpinSeconds)) { RepeatBehavior = RepeatBehavior.Forever }, 30));
    }
    protected override void OnRender(DrawingContext dc)
    {
        dc.PushTransform(new ScaleTransform(ActualWidth / 24, ActualHeight / 24));
        var pen = new Pen(ColorUtil.Brush(Color), 1.6) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
        dc.DrawEllipse(null, pen, new Point(12, 12), 2, 2);
        dc.DrawGeometry(null, pen, Geometry.Parse("M12 10 C12 6 13 3 16 3 C19 3 19 7 14 10 M14 12 C18 12 21 13 21 16 C21 19 17 19 14 14 M12 14 C12 18 11 21 8 21 C5 21 5 17 10 14 M10 12 C6 12 3 11 3 8 C3 5 7 5 10 10"));
        dc.Pop();
    }
}
