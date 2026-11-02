using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Prism.App.Common;
using Prism.App.Controls;
using Prism.App.ViewModels;
using Prism.Core.Ipc;
using Prism.App.Navigation;

namespace Prism.App.Widgets;

/// <summary>
/// One desktop widget: the same header, spacing and type as the app, live readings, and the one-tap
/// actions that make sense from the desktop. Built once; each update only changes text, colours and
/// values (lists are rebuilt only when their items change), and nothing animates between updates.
/// </summary>
public sealed class WidgetCard : UserControl
{
    /// <summary>Set by the app: opens Prism on the page that belongs to a widget.</summary>
    public static Action<PageKey>? OpenPage { get; set; }

    private const double BaseWidth = 250;
    private readonly WidgetViewModel vm;
    private readonly StackPanel body = new();
    private readonly TextBlock status = Text(12, "#A3A6AE");
    private readonly List<Action> updates = new();
    private readonly Dictionary<string, (Panel Host, Func<string> Signature, Action Build)> lists = new();
    private bool pending;

    public WidgetCard(WidgetViewModel model)
    {
        vm = model; DataContext = model;
        Width = BaseWidth;
        SetResourceReference(FontFamilyProperty, "BodyFont");
        UseLayoutRounding = true;
        TextOptions.SetTextFormattingMode(this, TextFormattingMode.Display);

        var header = new DockPanel { Margin = new(0, 0, 0, 12) };
        DockPanel.SetDock(status, Dock.Right); status.VerticalAlignment = VerticalAlignment.Center; header.Children.Add(status);
        var icon = new DeviceIcon { Kind = IconFor(vm.Kind, vm.PreviewIcon), Width = 14, Height = 14, Color = ColorUtil.Parse(vm.IconColor, Colors.White), Margin = new(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center };
        header.Children.Add(icon);
        header.Children.Add(new TextBlock { Text = Title(vm.Kind), FontSize = 11, FontWeight = FontWeights.SemiBold, Foreground = Brush("#A3A6AE"), VerticalAlignment = VerticalAlignment.Center });

        var stack = new StackPanel(); stack.Children.Add(header); stack.Children.Add(body);
        var card = new Border { CornerRadius = new(16), BorderThickness = new(1), BorderBrush = Brush("#26FFFFFF"), Padding = new(16, 14, 16, 16), Child = stack };
        card.SetBinding(Border.BackgroundProperty, new System.Windows.Data.Binding(nameof(vm.BackgroundBrush)));
        Content = card;

        Build();
        ContextMenu = BuildMenu();
        // Drag from anywhere that isn't a button.
        MouseLeftButtonDown += (_, e) => { if (e.OriginalSource is DependencyObject d && FindAncestor<ButtonBase>(d) is null && Window.GetWindow(this) is WidgetWindow w) { w.DragWidget(); e.Handled = true; } };
        vm.PropertyChanged += Changed;
        Unloaded += (_, _) => vm.PropertyChanged -= Changed;
        Loaded += (_, _) => { vm.PropertyChanged -= Changed; vm.PropertyChanged += Changed; Refresh(); };
        Refresh();
    }

    // The model raises many changes per update; refresh once, after they have all arrived.
    private void Changed(object? sender, PropertyChangedEventArgs e)
    {
        if (pending) return;
        pending = true;
        Dispatcher.BeginInvoke(() => { pending = false; Refresh(); }, DispatcherPriority.Background);
    }

    private void Refresh()
    {
        foreach (var update in updates) update();
        foreach (var (key, list) in lists)
        {
            var signature = list.Signature();
            if (list.Host.Tag as string == signature) continue;
            list.Host.Tag = signature; list.Host.Children.Clear(); list.Build();
        }
    }

    private void Build()
    {
        switch (vm.Kind)
        {
            case WidgetKind.SystemMonitor: BuildSystem(cpu: true, gpu: true, ram: true); break;
            case WidgetKind.Cpu: BuildSystem(cpu: true, gpu: false, ram: false); break;
            case WidgetKind.Gpu: BuildSystem(cpu: false, gpu: true, ram: false); break;
            case WidgetKind.Fans: BuildFans(); break;
            case WidgetKind.Lighting: BuildLighting(); break;
            case WidgetKind.Lamp: BuildLamps(); break;
            case WidgetKind.Modem or WidgetKind.Ping or WidgetKind.Network: BuildInternet(); break;
            case WidgetKind.Scenes: BuildScenes(); break;
            case WidgetKind.AirFryer: BuildFryer(); break;
        }
    }

    private void BuildSystem(bool cpu, bool gpu, bool ram)
    {
        status.Text = "Live";
        if (cpu) Reading("CPU", () => Degrees(vm.CpuTemperature), () => ColorUtil.Heat(Finite(vm.CpuTemperature)), () => vm.CpuText + " load", () => vm.CpuPercent, "#FF4D1A");
        if (gpu) Reading("GPU", () => Degrees(vm.GpuTemperature), () => ColorUtil.Heat(Finite(vm.GpuTemperature)), () => vm.GpuText + " load", () => vm.GpuPercent, "#36D6FF");
        if (ram) Reading("RAM", () => vm.RamText, () => ColorUtil.TextPrimary, () => $"{Finite(vm.RamPercent):0}%", () => vm.RamPercent, "#B26BFF");
        updates.Add(() =>
        {
            double hottest = Math.Max(Finite(vm.CpuTemperature), Finite(vm.GpuTemperature));
            status.Text = hottest >= 85 ? "Hot" : hottest >= 75 ? "Warm" : "Normal";
            status.Foreground = new SolidColorBrush(ColorUtil.Heat(hottest));
        });
    }

    // Label, big value (coloured), small detail and a thin bar.
    private void Reading(string label, Func<string> value, Func<Color> color, Func<string> detail, Func<double> percent, string barColor)
    {
        var row = new DockPanel { Margin = new(0, 0, 0, 4) };
        var right = new StackPanel { Orientation = Orientation.Horizontal }; DockPanel.SetDock(right, Dock.Right);
        var big = Text(16, "#F4F5F7", FontWeights.SemiBold); big.SetResourceReference(TextBlock.FontFamilyProperty, "NumberFont");
        var small = Text(12, "#7C808A"); small.Margin = new(8, 0, 0, 0); small.VerticalAlignment = VerticalAlignment.Bottom; small.Padding = new(0, 0, 0, 2);
        right.Children.Add(big); right.Children.Add(small); row.Children.Add(right);
        var name = Text(13, "#C9CBD1"); name.Text = label; name.VerticalAlignment = VerticalAlignment.Center; row.Children.Add(name);
        body.Children.Add(row);
        var track = new Border { Height = 4, CornerRadius = new(2), Background = Brush("#1E2026"), Margin = new(0, 2, 0, 12) };
        var fill = new Border { Height = 4, CornerRadius = new(2), Background = Brush(barColor), HorizontalAlignment = HorizontalAlignment.Left };
        track.Child = fill; body.Children.Add(track);
        updates.Add(() =>
        {
            big.Text = value(); big.Foreground = new SolidColorBrush(color()); small.Text = detail();
            fill.Width = Math.Clamp(Finite(percent()), 0, 100) / 100 * (BaseWidth - 34);
        });
    }

    private void BuildFans()
    {
        var modes = new UniformGrid { Rows = 1, Margin = new(0, 0, -6, 12) };
        foreach (var (label, key) in new[] { ("Silent", "Silent"), ("Balanced", "Standard"), ("Turbo", "Turbo") })
        {
            var button = Pill(label); button.Padding = new(2, 0, 2, 0); button.FontSize = 11.5; button.Click += (_, _) => vm.SetFanModeCommand.Execute(key);
            modes.Children.Add(button);
            updates.Add(() => Select(button, string.Equals(vm.FanMode, label, StringComparison.OrdinalIgnoreCase) || string.Equals(vm.FanMode, key, StringComparison.OrdinalIgnoreCase)));
        }
        body.Children.Add(modes);
        var rows = new StackPanel(); body.Children.Add(rows);
        updates.Add(() => status.Text = vm.FanMode is "Standard" ? "Balanced" : vm.FanMode is "Full" ? "Full speed" : vm.FanMode);
        lists["fans"] = (rows, () => string.Join("|", vm.FanRows.Select(f => f.Name + f.RpmText)), () =>
        {
            foreach (var fan in vm.FanRows)
            {
                var row = new DockPanel { Margin = new(0, 0, 0, 8) };
                var rpm = Text(14, "#F4F5F7", FontWeights.SemiBold); rpm.Text = fan.RpmText.Replace(" rpm", ""); rpm.SetResourceReference(TextBlock.FontFamilyProperty, "NumberFont");
                var unit = Text(11, "#7C808A"); unit.Text = " rpm"; unit.VerticalAlignment = VerticalAlignment.Bottom; unit.Padding = new(0, 0, 0, 1);
                var rpmRow = new StackPanel { Orientation = Orientation.Horizontal }; rpmRow.Children.Add(rpm); rpmRow.Children.Add(unit);
                DockPanel.SetDock(rpmRow, Dock.Right); row.Children.Add(rpmRow);
                var fanIcon = new DeviceIcon { Kind = fan.IsPump ? DeviceIconKind.Pump : DeviceIconKind.Fan, Width = 15, Height = 15, Color = fan.IsPump ? ColorUtil.Good : ColorUtil.Excellent, Margin = new(0, 0, 10, 0), VerticalAlignment = VerticalAlignment.Center };
                row.Children.Add(fanIcon);
                var name = Text(13, "#C9CBD1"); name.Text = fan.Name; name.VerticalAlignment = VerticalAlignment.Center; row.Children.Add(name);
                rows.Children.Add(row);
            }
        });
    }

    private void BuildLighting()
    {
        var strip = new LedStrip { Height = 22, Live = false, Margin = new(0, 0, 0, 12) };
        strip.SetBinding(LedStrip.ColorHexProperty, new System.Windows.Data.Binding(nameof(vm.LightingColor)));
        strip.SetBinding(LedStrip.EffectKindProperty, new System.Windows.Data.Binding(nameof(vm.LightingEffectKind)));
        strip.SetBinding(LedStrip.BrightnessProperty, new System.Windows.Data.Binding(nameof(vm.Brightness)));
        body.Children.Add(strip);
        var swatches = new UniformGrid { Rows = 1 };
        foreach (var hex in new[] { "#FF4D1A", "#FF4D4F", "#F5A524", "#3DDC84", "#36D6FF", "#4D6BFF", "#B26BFF", "#F4F5F7" })
        {
            var swatch = new ColorSwatch { Height = 24, ColorHex = hex, Margin = new(0, 0, 6, 0) };
            swatch.SetBinding(ColorSwatch.SelectedHexProperty, new System.Windows.Data.Binding(nameof(vm.LightingColor)));
            swatch.Click += (_, _) => vm.SelectColorCommand.Execute(hex);
            System.Windows.Automation.AutomationProperties.SetName(swatch, "Case colour " + hex);
            swatches.Children.Add(swatch);
        }
        body.Children.Add(swatches);
        updates.Add(() => status.Text = vm.EffectName);
    }

    private void BuildLamps()
    {
        var row = new UniformGrid { Rows = 1 }; body.Children.Add(row);
        updates.Add(() => { int on = vm.LampRows.Count(l => l.Status != "Off" && l.Status != "Offline"); status.Text = vm.LampRows.Count == 0 ? "None" : $"{on} on"; });
        lists["lamps"] = (row, () => string.Join("|", vm.LampRows.Select(l => l.Id + l.Name + l.Color + l.Status)), () =>
        {
            if (vm.LampRows.Count == 0) { var none = Text(12, "#7C808A"); none.Text = "Add a lamp on the Devices page"; row.Children.Add(none); return; }
            foreach (var lamp in vm.LampRows)
            {
                var content = new StackPanel();
                content.Children.Add(new LampOrb { Width = 46, Height = 46, ColorHex = lamp.Color, HorizontalAlignment = HorizontalAlignment.Center, Margin = new(0, 0, 0, 6) });
                var name = Text(12, "#F4F5F7", FontWeights.SemiBold); name.Text = lamp.Name; name.HorizontalAlignment = HorizontalAlignment.Center; name.TextTrimming = TextTrimming.CharacterEllipsis; content.Children.Add(name);
                var state = Text(11, lamp.Status == "Offline" ? "#F5A524" : "#7C808A"); state.Text = lamp.Status == "Offline" ? "Offline" : lamp.Status == "Off" ? "Off" : "On"; state.HorizontalAlignment = HorizontalAlignment.Center; content.Children.Add(state);
                var button = new Button { Content = content, Background = Brushes.Transparent, BorderThickness = new(0), Padding = new(4), Cursor = Cursors.Hand, IsEnabled = lamp.Status != "Offline", ToolTip = "Turn " + lamp.Name + (lamp.Status == "Off" ? " on" : " off") };
                button.SetResourceReference(StyleProperty, "TextAction");
                var id = lamp.Id; button.Click += (_, _) => vm.ToggleLampByIdCommand.Execute(id);
                System.Windows.Automation.AutomationProperties.SetName(button, "Toggle " + lamp.Name);
                row.Children.Add(button);
            }
        });
    }

    private void BuildInternet()
    {
        var row = new DockPanel { Margin = new(0, 0, 0, 10) };
        var bars = new SignalBars { Width = 40, Height = 26, Margin = new(0, 0, 12, 0), VerticalAlignment = VerticalAlignment.Bottom };
        DockPanel.SetDock(bars, Dock.Left); row.Children.Add(bars);
        var verdict = Text(22, "#F4F5F7", FontWeights.SemiBold); verdict.SetResourceReference(TextBlock.FontFamilyProperty, "NumberFont"); verdict.VerticalAlignment = VerticalAlignment.Bottom;
        row.Children.Add(verdict); body.Children.Add(row);
        var chart = new Sparkline { Height = 34, Color = ColorUtil.Excellent };
        chart.SetBinding(LineChart.ValuesProperty, new System.Windows.Data.Binding(vm.Kind == WidgetKind.Network ? nameof(vm.NetDownHistory) : nameof(vm.PingHistory)));
        body.Children.Add(chart);
        var foot = Text(11, "#7C808A"); foot.Margin = new(0, 6, 0, 0); body.Children.Add(foot);
        updates.Add(() =>
        {
            var color = ColorUtil.Parse(vm.ModemColor, ColorUtil.TextPrimary);
            verdict.Text = vm.MiniModemVerdict; verdict.Foreground = new SolidColorBrush(color);
            bars.Value = vm.ModemBarCount; bars.BarColor = color;
            status.Text = vm.Kind == WidgetKind.Network ? $"↓ {vm.DownText}" : vm.PingText + " ms";
            foot.Text = vm.Kind == WidgetKind.Network ? $"Download, last 40 s · ↑ {vm.UpText}" : $"{vm.ModemNetwork} · ping, last 40 s";
        });
    }

    private void BuildScenes()
    {
        var grid = new UniformGrid { Columns = 2 }; body.Children.Add(grid);
        updates.Add(() => status.Text = string.IsNullOrEmpty(vm.ActiveScene) ? "" : vm.ActiveScene);
        lists["scenes"] = (grid, () => string.Join("|", vm.Scenes.Select(s => s.Name + s.IsActive)), () =>
        {
            foreach (var scene in vm.Scenes.Take(6))
            {
                var button = Pill(scene.Name); button.Margin = new(0, 0, 6, 6); var name = scene.Name;
                button.Click += (_, _) => vm.ApplySceneCommand.Execute(name);
                Select(button, scene.IsActive);
                grid.Children.Add(button);
            }
        });
    }

    private void BuildFryer()
    {
        var host = new StackPanel(); body.Children.Add(host);
        lists["fryer"] = (host, () => string.Join("|", vm.Appliances.Select(a => a.Id + a.Status + a.RemainingMinutes + a.TargetTemperature + a.Connected)), () =>
        {
            var fryer = vm.Appliances.FirstOrDefault();
            if (fryer is null) { status.Text = "Not connected"; var none = Text(12, "#7C808A"); none.Text = "Add your fryer on the Devices page"; host.Children.Add(none); return; }
            bool cooking = fryer.Status is "cooking" or "preheating" or "paused";
            status.Text = !fryer.Connected ? "Offline" : cooking ? Capital(fryer.Status) : "Ready";
            var time = Text(30, "#F4F5F7", FontWeights.SemiBold); time.SetResourceReference(TextBlock.FontFamilyProperty, "NumberFont");
            time.Text = cooking && fryer.RemainingMinutes is { } left ? $"{left:0} min" : "Idle";
            host.Children.Add(time);
            var detail = Text(12, "#A3A6AE"); detail.Text = $"{fryer.TargetTemperature:0}°C target"; detail.Margin = new(0, 2, 0, 0); host.Children.Add(detail);
            if (cooking && fryer.Actions.Any(a => a.Name == "cancel-cooking"))
            {
                var cancel = Pill("Stop cooking"); cancel.Margin = new(0, 12, 0, 0); cancel.Foreground = new SolidColorBrush(ColorUtil.Poor);
                cancel.Click += (_, _) => vm.CancelCookingCommand.Execute(fryer); host.Children.Add(cancel);
            }
        });
    }

    private ContextMenu BuildMenu()
    {
        var menu = new ContextMenu();
        var open = new MenuItem { Header = "Open in Prism" }; open.Click += (_, _) => OpenPage?.Invoke(PageFor(vm.Kind)); menu.Items.Add(open);
        menu.Items.Add(new Separator());
        var size = new MenuItem { Header = "Size" };
        foreach (var option in Enum.GetValues<WidgetSize>()) { var item = new MenuItem { Header = option.ToString(), IsCheckable = true }; item.Click += (_, _) => vm.Size = option; size.Items.Add(item); }
        menu.Items.Add(size);
        var background = new MenuItem { Header = "Background" };
        foreach (var option in vm.Backgrounds) { var item = new MenuItem { Header = option, IsCheckable = true }; item.Click += (_, _) => vm.Background = option; background.Items.Add(item); }
        menu.Items.Add(background);
        var top = new MenuItem { Header = "Always on top", IsCheckable = true }; top.Click += (_, _) => vm.AlwaysOnTop = top.IsChecked; menu.Items.Add(top);
        var through = new MenuItem { Header = "Click through (hold Alt to move)", IsCheckable = true }; through.Click += (_, _) => vm.ClickThrough = through.IsChecked; menu.Items.Add(through);
        menu.Items.Add(new Separator());
        var hide = new MenuItem { Header = "Hide this widget" }; hide.Click += (_, _) => vm.Enabled = false; menu.Items.Add(hide);
        menu.Opened += (_, _) =>
        {
            foreach (MenuItem item in size.Items) item.IsChecked = (string)item.Header == vm.Size.ToString();
            foreach (MenuItem item in background.Items) item.IsChecked = (string)item.Header == vm.Background;
            top.IsChecked = vm.AlwaysOnTop; through.IsChecked = vm.ClickThrough;
        };
        return menu;
    }

    private static string Degrees(double value) => double.IsFinite(value) ? $"{value:0}°" : Format.Dash;
    private static DeviceIconKind IconFor(WidgetKind kind, DeviceIconKind fallback) => kind switch
    {
        WidgetKind.SystemMonitor or WidgetKind.Cpu => DeviceIconKind.Activity, WidgetKind.Gpu => DeviceIconKind.Gpu, WidgetKind.Fans => DeviceIconKind.Fan,
        WidgetKind.Lighting => DeviceIconKind.Strip, WidgetKind.Lamp => DeviceIconKind.Lamp, WidgetKind.Modem or WidgetKind.Ping or WidgetKind.Network => DeviceIconKind.Router,
        WidgetKind.AirFryer => DeviceIconKind.Fryer, _ => fallback
    };
    private static PageKey PageFor(WidgetKind kind) => kind switch
    {
        WidgetKind.Fans => PageKey.Fans, WidgetKind.Lighting => PageKey.Lighting, WidgetKind.Lamp => PageKey.Lamp,
        WidgetKind.Modem or WidgetKind.Ping or WidgetKind.Network => PageKey.Modem, WidgetKind.Scenes => PageKey.Scenes,
        WidgetKind.AirFryer => PageKey.Appliances, _ => PageKey.Monitor
    };
    private static string Title(WidgetKind kind) => kind switch
    {
        WidgetKind.SystemMonitor => "SYSTEM", WidgetKind.Cpu => "PROCESSOR", WidgetKind.Gpu => "GRAPHICS", WidgetKind.Fans => "FANS",
        WidgetKind.Lighting => "LIGHTING", WidgetKind.Lamp => "LAMPS", WidgetKind.Modem => "INTERNET", WidgetKind.Ping => "PING",
        WidgetKind.Network => "NETWORK", WidgetKind.Scenes => "SCENES", WidgetKind.AirFryer => "AIR FRYER", _ => kind.ToString().ToUpperInvariant()
    };
    private static Button Pill(string text)
    {
        var button = new Button { Content = text, MinHeight = 30, Padding = new(8, 0, 8, 0), Margin = new(0, 0, 6, 0), FontSize = 12, FontWeight = FontWeights.SemiBold, Cursor = Cursors.Hand };
        return button;
    }
    private static void Select(Button button, bool on)
    {
        button.Background = new SolidColorBrush(on ? Color.FromArgb(40, 255, 77, 26) : Color.FromRgb(30, 32, 38));
        button.BorderBrush = new SolidColorBrush(on ? ColorUtil.Accent : Color.FromArgb(18, 255, 255, 255));
        button.Foreground = new SolidColorBrush(on ? Color.FromRgb(255, 138, 96) : Color.FromRgb(201, 203, 209));
    }
    private static TextBlock Text(double size, string color, FontWeight? weight = null) => new() { FontSize = size, Foreground = Brush(color), FontWeight = weight ?? FontWeights.Normal };
    private static SolidColorBrush Brush(string hex) => new(ColorUtil.Parse(hex, Colors.White));
    private static double Finite(double value) => double.IsFinite(value) ? value : 0;
    private static string Capital(string text) => text.Length == 0 ? text : char.ToUpperInvariant(text[0]) + text[1..];
    private static T? FindAncestor<T>(DependencyObject? d) where T : DependencyObject
    {
        while (d is not null) { if (d is T found) return found; d = d is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d); }
        return null;
    }
}
