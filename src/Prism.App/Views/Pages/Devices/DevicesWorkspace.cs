using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using Prism.App.Controls;
using Prism.App.Common;
using Prism.App.ViewModels;
using Prism.Core.Ipc;

namespace Prism.App.Views;

public sealed class DevicesWorkspace : UserControl
{
    private readonly StackPanel root = new();
    private readonly StackPanel list = new();
    private readonly TextBlock banner = new() { FontSize = 32, Foreground = Brushes.WhiteSmoke, Margin = new(0, 4, 0, 12) };
    private readonly TokenImportPanel importer = new();
    private DevicesViewModel? model;
    private readonly Dictionary<string, (DeviceNameEditor Name, TextBlock Info, WrapPanel Buttons)> cards = new();
    public DevicesWorkspace()
    {
        root.Children.Add(new TextBlock { Text = "DEVICES", FontSize = 11, Foreground = Brushes.WhiteSmoke });
        banner.SetResourceReference(TextBlock.FontFamilyProperty, "NumberFont");
        banner.FontWeight = FontWeights.SemiBold;
        var heading = new DockPanel(); root.Children.Add(heading);
        var row = new WrapPanel();
        var find = new Button { Content = "Find devices", Margin = new(0, 4, 12, 12) }; find.SetBinding(Button.CommandProperty, new Binding("FindCommand"));
        var import = new Button { Content = "Import tokens", Margin = new(0, 4, 0, 12) }; import.Click += (_, _) => importer.Visibility = Visibility.Visible;
        row.Children.Add(find); row.Children.Add(import); DockPanel.SetDock(row, Dock.Right); heading.Children.Add(row); heading.Children.Add(banner);
        var discoveries = new TextBlock { Foreground = Brushes.Silver, Margin = new(0, 0, 0, 10) };
        discoveries.SetBinding(TextBlock.TextProperty, new Binding("Banner")); root.Children.Add(discoveries);
        var error = new TextBlock { Foreground = Brushes.OrangeRed, TextWrapping = TextWrapping.Wrap }; error.SetBinding(TextBlock.TextProperty, new Binding("LastError")); root.Children.Add(error);
        root.Children.Add(importer); root.Children.Add(list); Content = root;
        DataContextChanged += (_, _) => { if (model is not null) model.PropertyChanged -= Update; model = DataContext as DevicesViewModel; if (model is not null) { model.PropertyChanged += Update; importer.Client = model.Client; } Rebuild(); };
        Unloaded += (_, _) => importer.Clear();
    }
    private string signature = "";
    private void Update(object? sender, PropertyChangedEventArgs e) => Rebuild();
    public void ShowImport() => importer.Visibility = Visibility.Visible;
    private void Rebuild()
    {
        if (model is null) return;
        banner.Text = $"{model.State?.Devices?.Count ?? 0} devices, all in one place";
        if (model.ImportRequested) importer.Visibility = Visibility.Visible;
        var next = string.Join(";", model.Groups.SelectMany(g => g.Devices).Select(d => $"{d.Id}:{d.Kind}"));
        if (next == signature) { UpdateCards(); return; } signature = next; list.Children.Clear(); cards.Clear();
        foreach (var group in model.Groups)
        {
            list.Children.Add(new TextBlock { Text = group.Kind + $" · {group.Devices.Count} devices", FontSize = 15, Foreground = Brushes.WhiteSmoke, Margin = new(0, 16, 0, 8) });
            var grid = new CardGrid(); list.Children.Add(grid);
            foreach (var device in group.Devices)
            {
                var card = new StackPanel();
                card.Children.Add(new DeviceIcon { Kind = device.Kind switch { DeviceKind.Lamp => DeviceIconKind.Lamp, DeviceKind.FanHeader => DeviceIconKind.Fan, DeviceKind.Appliance => DeviceIconKind.Fryer, DeviceKind.Router => DeviceIconKind.Router, _ => DeviceIconKind.Board }, Color = (Color)ColorConverter.ConvertFromString("#36D6FF"), HorizontalAlignment = HorizontalAlignment.Left, Margin = new(0, 0, 0, 12) });
                var name = new DeviceNameEditor { DeviceId = device.Id, DisplayName = device.Name, Client = model.Client };
                card.Children.Add(name);
                var info = new TextBlock { Foreground = Brushes.Silver, TextWrapping = TextWrapping.Wrap, Margin = new(0, 6, 0, 6) }; card.Children.Add(info);
                var buttons = new WrapPanel();
                { var setup = new Button { Content = "Set up", Margin = new(0, 0, 12, 0) }; var dismiss = new Button { Content = "Dismiss" };
                    setup.Click += async (_, _) => { var current = model.State?.Devices?.FirstOrDefault(d => d.Id == device.Id) ?? device; if (current.Status == DeviceStatus.NeedsToken) { importer.TargetDevice = current; importer.Visibility = Visibility.Visible; importer.FocusImport(); } else await model.SetupAsync(current); };
                    dismiss.Click += async (_, _) => await model.DismissAsync(device.Id);
                    buttons.Children.Add(setup); buttons.Children.Add(dismiss); card.Children.Add(buttons);
                }
                cards[device.Id] = (name, info, buttons);
                grid.Children.Add(new GlassCard { Span = 3, Content = card, MinHeight = 170, Padding = new(14) });
            }
        }
        UpdateCards();
    }
    private void UpdateCards()
    {
        foreach (var device in model?.State?.Devices ?? [])
            if (cards.TryGetValue(device.Id, out var card))
            {
                card.Name.DisplayName = device.Name;
                card.Info.Text = $"{FriendlyLabelConverter.Label(device.Status)} · {device.Model ?? FriendlyLabelConverter.Label(device.Source)}\n{device.Ip}";
                card.Buttons.Visibility = device.Configured || device.Dismissed ? Visibility.Collapsed : Visibility.Visible;
            }
    }
}
