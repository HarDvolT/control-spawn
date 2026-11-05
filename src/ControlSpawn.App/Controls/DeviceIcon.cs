using System.Windows;
using System.Windows.Media;
using System.Windows.Controls;
using System.Windows.Data;

namespace ControlSpawn.App.Controls;

public enum DeviceIconKind { Ram, Gpu, Board, Strip, Fan, Lamp, Router, Fryer, Network, Pump, Activity }
public sealed class DeviceIcon : FrameworkElement
{
    public static readonly DependencyProperty KindProperty = DependencyProperty.Register(nameof(Kind), typeof(DeviceIconKind), typeof(DeviceIcon), new FrameworkPropertyMetadata(DeviceIconKind.Board, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty ColorProperty = DependencyProperty.Register(nameof(Color), typeof(Color), typeof(DeviceIcon), new FrameworkPropertyMetadata(Colors.LightGray, FrameworkPropertyMetadataOptions.AffectsRender));
    public DeviceIconKind Kind { get => (DeviceIconKind)GetValue(KindProperty); set => SetValue(KindProperty, value); }
    public Color Color { get => (Color)GetValue(ColorProperty); set => SetValue(ColorProperty, value); }
    public DeviceIcon() { Width = 32; Height = 32; }
    protected override void OnRender(DrawingContext dc)
    {
        string path = Kind switch
        {
            DeviceIconKind.Ram => "M4 7h16v9H4zM7 16v3M11 16v3M15 16v3M7 10h2M11 10h2M15 10h2",
            DeviceIconKind.Gpu => "M3 7h16v10H3zM19 10h2v4h-2M7 12a3 3 0 1 0 6 0a3 3 0 1 0-6 0",
            DeviceIconKind.Fan => "M12 12m-2 0a2 2 0 1 0 4 0a2 2 0 1 0-4 0M12 10c0-4 1-7 4-7s3 4-2 7M14 12c4 0 7 1 7 4s-4 3-7-2M12 14c0 4-1 7-4 7s-3-4 2-7M10 12c-4 0-7-1-7-4s4-3 7 2",
            DeviceIconKind.Pump => "M12 3c3 4 6 7 6 11a6 6 0 0 1-12 0c0-4 3-7 6-11z",
            DeviceIconKind.Lamp => "M9 18h6M10 21h4M12 3a6 6 0 0 0-4 10.5c.8.8 1 1.5 1 2.5h6c0-1 .2-1.7 1-2.5A6 6 0 0 0 12 3z",
            DeviceIconKind.Router => "M4 15h16v5H4zM8 15V9M16 15V9M12 15V5M7 18h1M11 18h1",
            DeviceIconKind.Fryer => "M5 6h14v14H5zM8 3h8M9 10h6M12 13v4",
            DeviceIconKind.Strip => "M3 12h18M5 10v4M9 10v4M13 10v4M17 10v4",
            DeviceIconKind.Network => "M3 12a9 9 0 0 1 18 0M6 12a6 6 0 0 1 12 0M9 12a3 3 0 0 1 6 0M12 12v6",
            DeviceIconKind.Activity => "M3 12h4l3-8 4 16 3-8h4",
            _ => "M4 4h16v16H4zM8 8h4v4H8zM15 8h2M15 12h2M8 15h9"
        };
        dc.PushTransform(new ScaleTransform(ActualWidth / 24, ActualHeight / 24));
        dc.DrawGeometry(null, new Pen(new SolidColorBrush(Color), 1.8) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round }, Geometry.Parse(path)); dc.Pop();
    }
}
public sealed record IconChoice(string Key, string Label, DeviceIconKind Icon);
public sealed class IconSegmented : SegmentedControl
{
    public IconSegmented()
    {
        SetResourceReference(StyleProperty, typeof(SegmentedControl));
        SelectedValuePath = nameof(IconChoice.Key);
        var panel = new FrameworkElementFactory(typeof(StackPanel)); panel.SetValue(StackPanel.OrientationProperty, Orientation.Horizontal);
        var icon = new FrameworkElementFactory(typeof(DeviceIcon)); icon.SetBinding(DeviceIcon.KindProperty, new Binding(nameof(IconChoice.Icon))); icon.SetValue(WidthProperty, 20d); icon.SetValue(HeightProperty, 20d); icon.SetValue(MarginProperty, new Thickness(0, 0, 8, 0)); panel.AppendChild(icon);
        var label = new FrameworkElementFactory(typeof(TextBlock)); label.SetBinding(TextBlock.TextProperty, new Binding(nameof(IconChoice.Label))); label.SetValue(VerticalAlignmentProperty, VerticalAlignment.Center); panel.AppendChild(label);
        ItemTemplate = new DataTemplate { VisualTree = panel };
    }
}
