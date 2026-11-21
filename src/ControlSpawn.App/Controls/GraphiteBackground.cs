using System.Windows;
using System.Windows.Media;

namespace ControlSpawn.App.Controls;

/// <summary>The shell's plain graphite ground.</summary>
public sealed class GraphiteBackground : FrameworkElement
{
    private static readonly Brush Ground = CreateGround();
    private static Brush CreateGround() { var b = new SolidColorBrush(Color.FromRgb(7, 8, 10)); b.Freeze(); return b; }
    // A plain graphite ground. The dot grid and the ember and ion light washes were decoration behind the content,
    // so colour now lives only where it means something: the live LEDs, the lamps and the status readings.
    protected override void OnRender(DrawingContext dc) => dc.DrawRectangle(Ground, null, new Rect(RenderSize));
}

public sealed class LiveIndicator : FrameworkElement
{
    public static readonly DependencyProperty ColorProperty = DependencyProperty.Register(nameof(Color), typeof(Color), typeof(LiveIndicator),
        new FrameworkPropertyMetadata(Colors.MediumSeaGreen, FrameworkPropertyMetadataOptions.AffectsRender));
    public Color Color { get => (Color)GetValue(ColorProperty); set => SetValue(ColorProperty, value); }
    public LiveIndicator()
    {
        VerticalAlignment = VerticalAlignment.Center;
        // A steady dot: the colour says the status. Pulsing every status dot cost far more CPU than it was worth.
    }
    protected override void OnRender(DrawingContext dc)
    {
        dc.DrawEllipse(new SolidColorBrush(Color), null, new Point(ActualWidth / 2, ActualHeight / 2), ActualWidth / 2, ActualHeight / 2);
    }
}
