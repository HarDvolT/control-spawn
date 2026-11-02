using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using Prism.App.Controls;
using Prism.App.ViewModels;
using Prism.App.Widgets;

namespace Prism.App.Views;

/// <summary>The compact, glanceable v2 desktop cards, bound to the same widget model.
/// Nothing in them moves between updates: they sit on the desktop all day, so constant motion would cost CPU for nothing.</summary>
public sealed class WidgetMiniature : UserControl
{
    public WidgetMiniature(WidgetViewModel model)
    {
        DataContext = model;
        SetResourceReference(FontFamilyProperty, "BodyFont");
        MouseLeftButtonDown += (_, e) => { if (Window.GetWindow(this) is WidgetWindow window) { window.DragWidget(); e.Handled = true; } };
        var menu = new ContextMenu();
        var controls = new MenuItem { Header = "Controls" };
        controls.Items.Add(new MenuItem { Header = new WidgetContent { DataContext = model, Width = 250 }, StaysOpenOnClick = true });
        menu.Items.Add(controls); ContextMenu = menu;
        var body = new StackPanel();
        var border = new Border { CornerRadius = new(14), BorderThickness = new(1), BorderBrush = new SolidColorBrush(Color.FromArgb(30, 255, 255, 255)), Padding = new(18, 14, 18, 14), Child = body };
        border.SetBinding(Border.BackgroundProperty, new Binding(nameof(model.BackgroundBrush)));
        Content = border;
        TextBlock Label(string text, double size = 14) => new() { Text = text, FontSize = size, Foreground = new SolidColorBrush(Color.FromRgb(163, 166, 174)) };
        TextBlock Reading(string path, double size = 16)
        {
            var text = new TextBlock { FontSize = size, FontWeight = FontWeights.SemiBold }; text.SetBinding(TextBlock.TextProperty, new Binding(path)); return text;
        }
        switch (model.Kind)
        {
            case WidgetKind.SystemMonitor:
                body.Children.Add(Label("SYSTEM"));
                foreach (var (label, text, value, color) in new[] { ("CPU", "CpuTemperatureLabel", "CpuTemperature", "#3DDC84"), ("GPU", "GpuTemperatureLabel", "GpuTemperature", "#3DDC84"), ("Load", "CpuText", "CpuPercent", "#FF4D1A") })
                {
                    var row = new DockPanel { Margin = new(0, 8, 0, 0) }; var number = Reading(text); DockPanel.SetDock(number, Dock.Right); row.Children.Add(number); row.Children.Add(Label(label, 16)); body.Children.Add(row);
                    var meter = new GlowProgressBar { Height = 6, Margin = new(0, 5, 0, 0), Shimmer = false, BarColor = (Color)ColorConverter.ConvertFromString(color) }; meter.SetBinding(GlowProgressBar.ValueProperty, new Binding(value)); body.Children.Add(meter);
                }
                break;
            case WidgetKind.Fans:
                var heading = new DockPanel(); var pump = Reading("MiniPump", 13); DockPanel.SetDock(pump, Dock.Right); heading.Children.Add(pump); heading.Children.Add(Reading("MiniFanHeading", 14)); body.Children.Add(heading);
                var fans = new ItemsControl(); fans.SetBinding(ItemsControl.ItemsSourceProperty, new Binding("MiniFanRows")); fans.ItemsPanel = new ItemsPanelTemplate(new FrameworkElementFactory(typeof(System.Windows.Controls.Primitives.UniformGrid)));
                var template = new FrameworkElementFactory(typeof(StackPanel)); template.SetValue(StackPanel.OrientationProperty, Orientation.Horizontal); template.SetValue(MarginProperty, new Thickness(0, 10, 10, 0));
                var rotor = new FrameworkElementFactory(typeof(FanRotor)); rotor.SetValue(WidthProperty, 18d); rotor.SetValue(HeightProperty, 18d); rotor.SetValue(MarginProperty, new Thickness(0, 0, 8, 0)); rotor.SetValue(FanRotor.SpinSecondsProperty, 0d); template.AppendChild(rotor);
                var rpm = new FrameworkElementFactory(typeof(TextBlock)); rpm.SetValue(TextBlock.FontSizeProperty, 17d); rpm.SetValue(TextBlock.FontWeightProperty, FontWeights.SemiBold); rpm.SetBinding(TextBlock.TextProperty, new Binding("RpmText")); template.AppendChild(rpm);
                fans.ItemTemplate = new DataTemplate { VisualTree = template }; body.Children.Add(fans);
                break;
            case WidgetKind.Modem:
                var net = new DockPanel(); var ping = Reading("MiniModemPing", 14); DockPanel.SetDock(ping, Dock.Right); net.Children.Add(ping); net.Children.Add(Reading("ModemNetwork", 14)); body.Children.Add(net);
                var signalRow = new DockPanel { Margin = new(0, 12, 0, 0) }; var bars = new SignalBars { Width = 48, Height = 30, Margin = new(0, 0, 10, 0) }; bars.SetBinding(SignalBars.ValueProperty, new Binding("ModemBarCount")); DockPanel.SetDock(bars, Dock.Left); signalRow.Children.Add(bars); signalRow.Children.Add(Reading("MiniModemVerdict", 27)); body.Children.Add(signalRow);
                break;
            case WidgetKind.Lamp:
                body.Children.Add(Label("LAMPS"));
                var orbs = new ItemsControl(); orbs.SetBinding(ItemsControl.ItemsSourceProperty, new Binding("LampRows"));
                var orbPanel = new FrameworkElementFactory(typeof(StackPanel)); orbPanel.SetValue(StackPanel.OrientationProperty, Orientation.Horizontal); orbs.ItemsPanel = new ItemsPanelTemplate(orbPanel);
                var orb = new FrameworkElementFactory(typeof(LampOrb)); orb.SetValue(WidthProperty, 58d); orb.SetValue(HeightProperty, 58d); orb.SetBinding(LampOrb.ColorHexProperty, new Binding("Color")); var orbButton = new FrameworkElementFactory(typeof(Button)); orbButton.SetValue(StyleProperty, TryFindResource("TextAction")); orbButton.SetValue(Control.PaddingProperty, new Thickness(0)); orbButton.SetBinding(Button.CommandProperty, new Binding("DataContext.ToggleLampByIdCommand") { RelativeSource = new RelativeSource(RelativeSourceMode.FindAncestor, typeof(ItemsControl), 1) }); orbButton.SetBinding(Button.CommandParameterProperty, new Binding("Id")); orbButton.AppendChild(orb); orbs.ItemTemplate = new DataTemplate { VisualTree = orbButton }; body.Children.Add(orbs);
                break;
            case WidgetKind.AirFryer:
                body.Children.Add(Label("AIR FRYER"));
                foreach (var appliance in model.Appliances) body.Children.Add(Label(appliance.Name + " · " + appliance.Status, 16));
                break;
            default:
                Content = new WidgetContent { DataContext = model }; break;
        }
    }
}
