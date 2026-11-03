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
        Motion.WhileVisible(this, UpdateMotion, () => drift.BeginAnimation(TranslateTransform.XProperty, null));
        SystemParameters.StaticPropertyChanged += PreferenceChanged;
    }
    private void PreferenceChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    { if (IsLoaded) Dispatcher.BeginInvoke(UpdateMotion); }
    private void UpdateMotion()
    {
        // The glows stay still: drifting them repainted the whole window (millions of pixels on a high-DPI screen)
        // ten times a second for a movement of 2.5 px a second that nobody could see.
        drift.BeginAnimation(TranslateTransform.XProperty, null);
    }
    protected override void OnRender(DrawingContext dc)
    {
        dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(7, 8, 10)), null, new Rect(RenderSize));
        // One tiled brush instead of thousands of separate dots keeps every redraw cheap.
        dc.DrawRectangle(DotGrid, null, new Rect(RenderSize));
        dc.PushTransform(drift);
        Glow(dc, new Point(140, 80), 390, Color.FromArgb(28, 255, 77, 26));
        Glow(dc, new Point(ActualWidth - 100, 430), 465, Color.FromArgb(22, 54, 214, 255));
        dc.Pop();
    }
    private static readonly DrawingBrush DotGrid = CreateDotGrid();
    private static DrawingBrush CreateDotGrid()
    {
        var dot = new GeometryDrawing(new SolidColorBrush(Color.FromArgb(8, 255, 255, 255)), null, new EllipseGeometry(new Point(1, 1), 1, 1));
        var tile = new DrawingGroup(); tile.Children.Add(new GeometryDrawing(Brushes.Transparent, null, new RectangleGeometry(new Rect(0, 0, 24, 24)))); tile.Children.Add(dot);
        var brush = new DrawingBrush(tile) { TileMode = TileMode.Tile, Viewport = new Rect(0, 0, 24, 24), ViewportUnits = BrushMappingMode.Absolute, Stretch = Stretch.None };
        brush.Freeze(); return brush;
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
        // A steady dot: the colour says the status. Pulsing every status dot cost far more CPU than it was worth.
    }
    protected override void OnRender(DrawingContext dc)
    {
        dc.DrawEllipse(new SolidColorBrush(Color), null, new Point(ActualWidth / 2, ActualHeight / 2), ActualWidth / 2, ActualHeight / 2);
    }
}
