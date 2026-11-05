using System.Windows;
using System.Windows.Media;

namespace ControlSpawn.App.Controls;

/// <summary>A shaded lamp preview driven by the lamp's current colour.</summary>
public sealed class LampOrb : FrameworkElement
{
    public double RadiusInset { get; set; } = 12;
    public double GlowRadius { get; set; } = 40;
    public static readonly DependencyProperty ColorHexProperty = DependencyProperty.Register(
        nameof(ColorHex), typeof(string), typeof(LampOrb),
        new FrameworkPropertyMetadata("#FF4D1A", FrameworkPropertyMetadataOptions.AffectsRender));
    public string ColorHex { get => (string)GetValue(ColorHexProperty); set => SetValue(ColorHexProperty, value); }

    protected override void OnRender(DrawingContext dc)
    {
        var color = (Color)ColorConverter.ConvertFromString(ColorHex);
        var center = new Point(ActualWidth / 2, ActualHeight / 2);
        var radius = Math.Max(0, Math.Min(ActualWidth, ActualHeight) / 2 - RadiusInset);
        var glow = new RadialGradientBrush(Color.FromArgb(95, color.R, color.G, color.B), Color.FromArgb(0, color.R, color.G, color.B));
        dc.DrawEllipse(glow, null, center, radius + GlowRadius, radius + GlowRadius);
        var sphere = new RadialGradientBrush { GradientOrigin = new Point(.35, .28), Center = new Point(.35, .28), RadiusX = 1, RadiusY = 1 };
        sphere.GradientStops.Add(new GradientStop(Colors.White, 0));
        sphere.GradientStops.Add(new GradientStop(color, .42));
        sphere.GradientStops.Add(new GradientStop(Color.FromRgb((byte)(color.R * .25), (byte)(color.G * .25), (byte)(color.B * .25)), 1));
        dc.DrawEllipse(sphere, null, center, radius, radius);
    }
}
