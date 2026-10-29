using System.Windows;
using System.Windows.Media;

namespace Prism.App.Controls;

/// <summary>A shaded lamp preview driven by the lamp's current colour.</summary>
public sealed class LampOrb : FrameworkElement
{
    public static readonly DependencyProperty ColorHexProperty = DependencyProperty.Register(
        nameof(ColorHex), typeof(string), typeof(LampOrb),
        new FrameworkPropertyMetadata("#FF4D1A", FrameworkPropertyMetadataOptions.AffectsRender));
    public string ColorHex { get => (string)GetValue(ColorHexProperty); set => SetValue(ColorHexProperty, value); }

    protected override void OnRender(DrawingContext dc)
    {
        var color = (Color)ColorConverter.ConvertFromString(ColorHex);
        var center = new Point(ActualWidth / 2, ActualHeight / 2);
        var radius = Math.Max(0, Math.Min(ActualWidth, ActualHeight) / 2 - 12);
        var glow = new RadialGradientBrush(Color.FromArgb(95, color.R, color.G, color.B), Colors.Transparent);
        dc.DrawEllipse(glow, null, center, radius + 12, radius + 12);
        var sphere = new RadialGradientBrush { GradientOrigin = new Point(.3, .2), Center = new Point(.35, .3), RadiusX = .8, RadiusY = .8 };
        sphere.GradientStops.Add(new GradientStop(Colors.White, 0));
        sphere.GradientStops.Add(new GradientStop(color, .35));
        sphere.GradientStops.Add(new GradientStop(Color.FromRgb((byte)(color.R * .25), (byte)(color.G * .25), (byte)(color.B * .25)), 1));
        dc.DrawEllipse(sphere, null, center, radius, radius);
    }
}
