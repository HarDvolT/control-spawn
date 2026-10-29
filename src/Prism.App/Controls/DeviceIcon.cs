using System.Windows;
using System.Windows.Media;
using System.Windows.Controls;
using System.Windows.Data;

namespace Prism.App.Controls;

public enum DeviceIconKind { Ram, Gpu, Board, Strip, Fan, Lamp, Router, Fryer, Network }
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
            DeviceIconKind.Ram => "M2,9 L30,9 30,23 2,23Z M5,23 L5,27 M9,23 L9,27 M13,23 L13,27 M17,23 L17,27 M21,23 L21,27 M25,23 L25,27 M7,13 L12,13 12,19 7,19Z M19,13 L24,13 24,19 19,19Z",
            DeviceIconKind.Gpu => "M2,7 L29,7 29,24 2,24Z M5,24 L5,28 16,28 M8,11 A5,5 0 1 1 8,21 A5,5 0 1 1 8,11 M22,11 A5,5 0 1 1 22,21 A5,5 0 1 1 22,11",
            DeviceIconKind.Fan => "M16,3 A13,13 0 1 1 16,29 A13,13 0 1 1 16,3 M16,13 C1,1 3,20 13,17 M19,16 C31,1 12,3 15,13 M16,19 C31,31 29,12 19,15 M13,16 C1,31 20,29 17,19",
            DeviceIconKind.Lamp => "M10,22 C10,16 5,15 5,10 A11,10 0 0 1 27,10 C27,15 22,16 22,22Z M11,26 L21,26 M13,30 L19,30",
            DeviceIconKind.Router => "M3,18 L29,18 29,28 3,28Z M7,18 L4,3 M25,18 L28,3 M7,23 L10,23 M14,23 L17,23",
            DeviceIconKind.Fryer => "M7,3 L25,3 Q29,3 29,9 L29,28 3,28 3,9 Q3,3 7,3Z M3,13 L29,13 M13,17 L19,17 19,23 13,23Z M12,8 L20,8",
            DeviceIconKind.Strip => "M2,11 L30,11 30,21 2,21Z M6,14 L6,18 M12,14 L12,18 M18,14 L18,18 M24,14 L24,18",
            DeviceIconKind.Network => "M12,2 L20,2 20,10 12,10Z M2,22 L10,22 10,30 2,30Z M22,22 L30,22 30,30 22,30Z M16,10 L16,17 M6,22 L6,17 26,17 26,22",
            _ => "M3,2 L29,2 29,30 3,30Z M9,7 L21,7 21,19 9,19Z M7,24 L25,24 M25,6 L25,18 M7,27 L25,27"
        };
        dc.PushTransform(new ScaleTransform(ActualWidth / 32, ActualHeight / 32));
        dc.DrawGeometry(null, new Pen(new SolidColorBrush(Color), 1.5) { LineJoin = PenLineJoin.Round }, Geometry.Parse(path)); dc.Pop();
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
