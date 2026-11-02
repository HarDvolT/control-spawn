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
    private readonly StackPanel discoveries = new();
    private DevicesViewModel? model;
    private readonly Dictionary<string, (DeviceNameEditor Name, TextBlock Info, WrapPanel Buttons, TextBlock Status, TextBlock Reading, Border Swatch)> cards = new();
    public DevicesWorkspace()
    {
        root.Children.Add(new TextBlock { Text = "DEVICES", FontSize = 11, Foreground = new SolidColorBrush(ColorUtil.TextTertiary) });
        banner.SetResourceReference(TextBlock.FontFamilyProperty, "NumberFont");
        banner.FontWeight = FontWeights.SemiBold;
        var heading = new DockPanel(); root.Children.Add(heading);
        var row = new WrapPanel();
        var find = new Button { Content = "Scan again", Margin = new(0, 4, 12, 12) }; find.SetBinding(Button.CommandProperty, new Binding("FindCommand"));
        var import = new Button { Content = "Import tokens", Margin = new(0, 4, 0, 12) }; import.Click += (_, _) => importer.Visibility = Visibility.Visible;
        row.Children.Add(find); row.Children.Add(import); DockPanel.SetDock(row, Dock.Right); heading.Children.Add(row); heading.Children.Add(banner);
        root.Children.Add(discoveries);
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
        discoveries.Children.Clear();
        // RGB devices already work through OpenRGB, so they never need a setup prompt.
        var pending = model.NewDevices.Where(d => !d.Dismissed && !IsRgb(d)).ToList();
        var pendingIds = pending.Select(d => d.Id).ToHashSet();
        foreach (var found in pending)
        {
            var body = new DockPanel();
            var actions = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            var setup = new Button { Content = "Set up", Margin = new(0, 0, 16, 0) }; setup.SetResourceReference(StyleProperty, "PrimaryButton");
            setup.Click += async (_, _) => { if (found.Status == DeviceStatus.NeedsToken) { importer.TargetDevice = found; importer.Visibility = Visibility.Visible; importer.FocusImport(); } else await model.SetupAsync(found); };
            var dismiss = new Button { Content = "Not now" }; dismiss.Click += async (_, _) => await model.DismissAsync(found.Id);
            actions.Children.Add(setup); actions.Children.Add(dismiss); DockPanel.SetDock(actions, Dock.Right); body.Children.Add(actions);
            var icon = new Border { Width = 52, Height = 52, CornerRadius = new(16), Background = new SolidColorBrush(Color.FromRgb(30, 32, 38)), Margin = new(0, 0, 16, 0), Child = new DeviceIcon { Kind = Icon(found), Width = 24, Height = 24, Color = ColorUtil.Accent } };
            DockPanel.SetDock(icon, Dock.Left); body.Children.Add(icon);
            var copy = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            copy.Children.Add(new TextBlock { Text = "New device found: " + found.Name, FontSize = 15, FontWeight = FontWeights.SemiBold });
            copy.Children.Add(new TextBlock { Text = $"{found.Model ?? FriendlyLabelConverter.Label(found.Source)} {found.Ip} \u00b7 Add it to control your device.", FontSize = 13, Foreground = Brushes.Silver, TextWrapping = TextWrapping.Wrap, Margin = new(0, 4, 16, 0) });
            body.Children.Add(copy);
            discoveries.Children.Add(new Border { Child = body, CornerRadius = new(20), Padding = new(20, 16, 20, 16), Margin = new(0, 6, 0, 0), BorderThickness = new(1), BorderBrush = new SolidColorBrush(Color.FromArgb(100, 255, 77, 26)), Background = new LinearGradientBrush(Color.FromArgb(40, 255, 77, 26), Color.FromArgb(6, 255, 77, 26), 0) });
        }
        if (model.ImportRequested) importer.Visibility = Visibility.Visible;
        var next = string.Join(";", model.Groups.SelectMany(g => g.Devices).Select(d => $"{d.Id}:{d.Kind}")) + "|" + string.Join(",", pendingIds);
        if (next == signature) { UpdateCards(); return; } signature = next; list.Children.Clear(); cards.Clear();
        foreach (var group in model.Groups)
        {
            // A device waiting in a "New device found" banner shows there only, not twice.
            var shown = group.Devices.Where(d => !pendingIds.Contains(d.Id)).ToList();
            if (shown.Count == 0) continue;
            list.Children.Add(new TextBlock { Text = (group.Kind == "PC" ? "This PC" : group.Kind == "Lamps" ? "Home" : group.Kind) + $" · {shown.Count} device{(shown.Count == 1 ? "" : "s")}", FontSize = 15, Foreground = Brushes.WhiteSmoke, Margin = new(0, 16, 0, 8) });
            var grid = new CardGrid(); list.Children.Add(grid);
            foreach (var device in shown)
            {
                var card = new StackPanel();
                var top = new DockPanel { Margin = new(0, 0, 0, 16) };
                var status = new TextBlock { FontSize = 11, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Right };
                DockPanel.SetDock(status, Dock.Right); top.Children.Add(status);
                var accent = Accent(device);
                top.Children.Add(new Border { Width = 46, Height = 46, CornerRadius = new(14), HorizontalAlignment = HorizontalAlignment.Left, BorderThickness = new(1), BorderBrush = new SolidColorBrush(Color.FromArgb(55, accent.R, accent.G, accent.B)), Background = new SolidColorBrush(Color.FromArgb(16, accent.R, accent.G, accent.B)), Child = new DeviceIcon { Kind = Icon(device), Width = 22, Height = 22, Color = accent } });
                card.Children.Add(top);
                var name = new DeviceNameEditor { DeviceId = device.Id, DisplayName = device.Name, Client = model.Client, FontSize = 15, FontWeight = FontWeights.SemiBold };
                card.Children.Add(name);
                var info = new TextBlock { Foreground = new SolidColorBrush(ColorUtil.TextTertiary), FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new(0, 4, 0, 10) }; card.Children.Add(info);
                var buttons = new WrapPanel();
                { var setup = new Button { Content = "Set up", Margin = new(0, 0, 12, 0) }; var dismiss = new Button { Content = "Dismiss" };
                    setup.Click += async (_, _) => { var current = model.State?.Devices?.FirstOrDefault(d => d.Id == device.Id) ?? device; if (current.Status == DeviceStatus.NeedsToken) { importer.TargetDevice = current; importer.Visibility = Visibility.Visible; importer.FocusImport(); } else await model.SetupAsync(current); };
                    dismiss.Click += async (_, _) => await model.DismissAsync(device.Id);
                    buttons.Children.Add(setup); buttons.Children.Add(dismiss); card.Children.Add(buttons);
                }
                var footer = new DockPanel();
                var swatch = new Border { Width = 22, Height = 22, CornerRadius = new(11), HorizontalAlignment = HorizontalAlignment.Right };
                DockPanel.SetDock(swatch, Dock.Right); footer.Children.Add(swatch);
                var reading = new TextBlock { FontSize = 17, FontWeight = FontWeights.SemiBold }; reading.SetResourceReference(TextBlock.FontFamilyProperty, "NumberFont"); footer.Children.Add(reading);
                card.Children.Add(new Border { BorderThickness = new(0, 1, 0, 0), BorderBrush = new SolidColorBrush(Color.FromArgb(15, 255, 255, 255)), Padding = new(0, 10, 0, 0), Child = footer });
                cards[device.Id] = (name, info, buttons, status, reading, swatch);
                grid.Children.Add(new GlassCard { Span = 3, Content = card, MinHeight = 180, Padding = new(16) });
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
                bool online = (device.OnlineStatus ?? device.Status) == DeviceStatus.Ready;
                card.Status.Text = "\u25cf " + (online ? "Online" : FriendlyLabelConverter.Label(device.Status));
                card.Status.Foreground = new SolidColorBrush(online ? ColorUtil.Parse("#3DDC84", Colors.Green) : ColorUtil.Parse("#F5A524", Colors.Orange));
                card.Reading.Text = Reading(device);
                var lamp = model?.State?.Lamps?.FirstOrDefault(l => l.Id == device.Id);
                var rgb = model?.State?.Lighting.Devices.FirstOrDefault(d => d.Id == device.Id);
                card.Swatch.Visibility = lamp is not null || rgb is not null ? Visibility.Visible : Visibility.Collapsed;
                card.Swatch.Background = new SolidColorBrush(ColorUtil.Parse(lamp?.Color ?? model?.State?.Lighting.Color, Colors.Transparent));
                card.Info.Text = device.Model ?? device.DetectedName ?? (model?.State?.Lighting.Devices.FirstOrDefault(d => d.Id == device.Id) is { } rgbInfo ? $"{rgbInfo.Kind} \u00b7 {rgbInfo.LedCount} LEDs" : device.Ip ?? FriendlyLabelConverter.Label(device.Source));
                card.Buttons.Visibility = device.Configured || device.Dismissed || IsRgb(device) || device.Kind == DeviceKind.NetworkDevice ? Visibility.Collapsed : Visibility.Visible;
            }
    }
    private bool IsRgb(DeviceState d) => model?.State?.Lighting.Devices.Any(r => r.Id == d.Id) == true;
    private DeviceIconKind Icon(DeviceState d) => model?.State?.Lighting.Devices.FirstOrDefault(r => r.Id == d.Id)?.Kind.ToLowerInvariant() switch
    {
        "ram" => DeviceIconKind.Ram, "gpu" => DeviceIconKind.Gpu, "strip" => DeviceIconKind.Strip, _ => d.Kind switch
    {
        DeviceKind.Lamp => DeviceIconKind.Lamp, DeviceKind.FanHeader => model?.State?.Fans.FirstOrDefault(f => d.Id == $"fan:{f.Header}")?.IsPump == true ? DeviceIconKind.Pump : DeviceIconKind.Fan,
        DeviceKind.Appliance => DeviceIconKind.Fryer, DeviceKind.Router => DeviceIconKind.Router,
        DeviceKind.NetworkDevice => DeviceIconKind.Network,
        _ => (d.Name + d.Model).ToLowerInvariant() is var n && n.Contains("ram") ? DeviceIconKind.Ram : n.Contains("gpu") || n.Contains("graphics") || n.Contains("radeon") ? DeviceIconKind.Gpu : DeviceIconKind.Board
    } };
    private Color Accent(DeviceState d) => ColorUtil.Parse(Icon(d) switch { DeviceIconKind.Ram => "#B26BFF", DeviceIconKind.Gpu or DeviceIconKind.Lamp => "#FF7A3D", DeviceIconKind.Router => "#F5A524", _ => "#36D6FF" }, Colors.White);
    private string Reading(DeviceState d)
    {
        if (model?.State is not { } s) return Format.Dash;
        if (s.Lamps?.FirstOrDefault(l => l.Id == d.Id) is { } lamp) return lamp.Connected ? $"{(lamp.On ? "On" : "Off")} \u00b7 {lamp.Brightness}%" : "Unavailable";
        if (d.Kind == DeviceKind.FanHeader) return Format.Rpm(s.Fans.FirstOrDefault(f => d.Id == $"fan:{f.Header}")?.Rpm);
        if (d.Kind == DeviceKind.Router) return s.Router?.Score is { } score ? $"{s.Router.Verdict} \u00b7 {score}" : "Unavailable";
        if (s.Appliances?.FirstOrDefault(a => a.Id == d.Id) is { } appliance) return appliance.Status;
        if (s.Lighting.Devices.FirstOrDefault(r => r.Id == d.Id) is { } rgb)
        {
            if (rgb.Kind.Contains("RAM", StringComparison.OrdinalIgnoreCase))
            {
                int index = s.Lighting.Devices.Where(r => r.Kind.Contains("RAM", StringComparison.OrdinalIgnoreCase)).ToList().FindIndex(r => r.Id == rgb.Id);
                return Format.TempUnit(s.System.DimmTemps.ElementAtOrDefault(index));
            }
            if (rgb.Kind.Contains("GPU", StringComparison.OrdinalIgnoreCase)) return Format.TempUnit(s.System.GpuTemp, 0);
            if (rgb.Kind.Contains("Motherboard", StringComparison.OrdinalIgnoreCase)) return Format.TempUnit(s.System.BoardTemp);
        }
        return d.Status == DeviceStatus.NeedsToken ? "Set up" : Format.Dash;
    }

}
