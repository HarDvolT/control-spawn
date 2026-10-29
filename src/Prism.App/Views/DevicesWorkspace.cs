using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using Prism.App.Controls;
using Prism.App.ViewModels;
using Prism.Core.Ipc;

namespace Prism.App.Views;

public sealed class DevicesWorkspace : UserControl
{
    private readonly StackPanel root = new();
    private readonly StackPanel list = new();
    private readonly TextBlock banner = new() { FontSize = 22, Foreground = Brushes.WhiteSmoke, Margin = new(0, 0, 0, 12) };
    private readonly TokenImportPanel importer = new();
    private DevicesViewModel? model;
    private readonly Dictionary<string, (DeviceNameEditor Name, TextBlock Info, WrapPanel Buttons)> cards = new();
    public DevicesWorkspace()
    {
        root.Children.Add(new TextBlock { Text = "Devices", FontSize = 28, Foreground = Brushes.WhiteSmoke });
        root.Children.Add(banner);
        var row = new WrapPanel();
        var find = new Button { Content = "Find devices", Margin = new(0, 4, 12, 12) }; find.SetBinding(Button.CommandProperty, new Binding("FindCommand"));
        var import = new Button { Content = "Import tokens", Margin = new(0, 4, 0, 12) }; import.Click += (_, _) => importer.Visibility = Visibility.Visible;
        row.Children.Add(find); row.Children.Add(import); root.Children.Add(row);
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
        banner.Text = model.Banner;
        if (model.ImportRequested) importer.Visibility = Visibility.Visible;
        var next = string.Join(";", model.Groups.SelectMany(g => g.Devices).Select(d => $"{d.Id}:{d.Kind}"));
        if (next == signature) { UpdateCards(); return; } signature = next; list.Children.Clear(); cards.Clear();
        foreach (var group in model.Groups)
        {
            list.Children.Add(new TextBlock { Text = group.Kind, FontSize = 18, Foreground = Brushes.WhiteSmoke, Margin = new(0, 20, 0, 8) });
            foreach (var device in group.Devices)
            {
                var card = new StackPanel { Margin = new(12) };
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
                list.Children.Add(new Border { Background = new SolidColorBrush(Color.FromRgb(30, 30, 34)), CornerRadius = new(10), Margin = new(0, 0, 0, 8), Child = card });
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
                card.Info.Text = $"{device.Status} · {device.Source} · {device.Model ?? device.Id}\n{device.Ip}  {device.Mac}  {device.Vendor}";
                card.Buttons.Visibility = device.Configured || device.Dismissed ? Visibility.Collapsed : Visibility.Visible;
            }
    }
}
