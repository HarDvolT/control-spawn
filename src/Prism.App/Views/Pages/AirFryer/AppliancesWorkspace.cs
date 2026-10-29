using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using Prism.App.Controls;
using Prism.App.ViewModels;
using Prism.Core.Ipc;

namespace Prism.App.Views;

public sealed class AppliancesWorkspace : UserControl
{
    private readonly StackPanel root = new();
    private readonly Dictionary<string, (TextBlock Status, RingGauge Ring, TextBlock Remaining)> displays = new();
    private readonly Dictionary<(string, int, int), Action<ApplianceProperty>> propertyUpdates = new();
    private bool updating;
    private AppliancesViewModel? model;
    private string signature = "";
    public AppliancesWorkspace()
    {
        Content = root;
        DataContextChanged += (_, _) => { if (model is not null) model.PropertyChanged -= Update; model = DataContext as AppliancesViewModel; if (model is not null) model.PropertyChanged += Update; Rebuild(); };
    }
    private void Update(object? sender, PropertyChangedEventArgs e) => Rebuild();
    private void Rebuild()
    {
        if (model is null) return;
        var next = string.Join(";", model.Appliances.Select(a => a.Id + a.Name + a.Connected + a.Properties.Count));
        if (next == signature && displays.Count > 0) { UpdateReadings(); return; }
        signature = next; root.Children.Clear(); displays.Clear(); propertyUpdates.Clear();
        root.Children.Add(new TextBlock { Text = "Air fryer", FontSize = 28, Foreground = Brushes.WhiteSmoke, Margin = new(0, 0, 0, 12) });
        root.Children.Add(new TextBlock { Text = "Cooking starts only after you confirm here. Cancel is always one click.", Foreground = Brushes.Silver, TextWrapping = TextWrapping.Wrap });
        var error = new TextBlock { Foreground = Brushes.OrangeRed, TextWrapping = TextWrapping.Wrap }; error.SetBinding(TextBlock.TextProperty, new Binding("LastError")); root.Children.Add(error);
        if (model.Appliances.Count == 0) root.Children.Add(new TextBlock { Text = "Find your fryer on the Devices page, then import its token.", Foreground = Brushes.Silver, Margin = new(0, 24, 0, 24) });
        foreach (var appliance in model.Appliances)
        {
            var panel = new StackPanel { Margin = new(16) };
            panel.Children.Add(new DeviceNameEditor { DeviceId = appliance.Id, DisplayName = appliance.Name, Client = model.Client });
            var status = new TextBlock { Foreground = Brushes.WhiteSmoke, FontSize = 18, TextWrapping = TextWrapping.Wrap, Margin = new(0, 12, 0, 12) }; panel.Children.Add(status);
            var ring = new RingGauge { Width = 150, Height = 150, Maximum = appliance.TargetMinutes ?? 1 }; panel.Children.Add(ring);
            var remaining = new TextBlock { Foreground = Brushes.Silver, HorizontalAlignment = HorizontalAlignment.Center }; panel.Children.Add(remaining);
            displays[appliance.Id] = (status, ring, remaining);
            var details = new StackPanel();
            foreach (var p in appliance.Properties)
            {
                var line = new StackPanel { Margin = new(0, 8, 0, 8) };
                line.Children.Add(new TextBlock { Text = char.ToUpperInvariant(p.Name[0]) + p.Name[1..].Replace('-', ' '), Foreground = Brushes.Silver });
                bool safe = p.Writable && p.Name is "target-temperature" or "target-time" or "mode" or "preheat" or "auto-keep-warm" or "turn-pot-config" or "texture" or "cooking-weight" or "target-cooking-measure";
                if (!safe)
                {
                    var text = new TextBlock { Foreground = Brushes.WhiteSmoke };
                    propertyUpdates[(appliance.Id, p.Siid, p.Piid)] = current => text.Text = current.Value is { } value && value.ValueKind == System.Text.Json.JsonValueKind.Number && value.TryGetInt32(out var key) && current.Values?.TryGetValue(key, out var label) == true ? label : FriendlyLabelConverter.Label(current.Value?.ToString());
                    line.Children.Add(text);
                }
                else if (p.Format == "bool")
                {
                    var toggle = new ToggleSwitch { IsChecked = p.Value?.ValueKind == System.Text.Json.JsonValueKind.True, HorizontalAlignment = HorizontalAlignment.Left, IsEnabled = appliance.Connected };
                    toggle.Checked += async (_, _) => { if (!updating) await model.SetAsync(appliance, p, true); }; toggle.Unchecked += async (_, _) => { if (!updating) await model.SetAsync(appliance, p, false); }; line.Children.Add(toggle);
                    propertyUpdates[(appliance.Id, p.Siid, p.Piid)] = current => toggle.IsChecked = current.Value?.ValueKind == System.Text.Json.JsonValueKind.True;
                }
                else if (p.Values is { } values)
                {
                    var segments = new SegmentedControl { ItemsSource = values.Values.ToArray(), IsEnabled = appliance.Connected };
                    segments.ItemsPanel = new ItemsPanelTemplate(new FrameworkElementFactory(typeof(WrapPanel)));
                    segments.SelectionChanged += async (_, _) => { if (!updating && segments.SelectedItem is string selected) await model.SetAsync(appliance, p, values.Single(v => v.Value == selected).Key); };
                    propertyUpdates[(appliance.Id, p.Siid, p.Piid)] = current => { if (current.Value is { } value && value.TryGetInt32(out var key)) segments.SelectedItem = values.GetValueOrDefault(key); };
                    line.Children.Add(segments);
                }
                else if (p.Range is { Length: 3 } range)
                {
                    var slider = new ThickSlider { Minimum = range[0], Maximum = range[1], Value = p.Value?.GetDouble() ?? range[0], IsEnabled = appliance.Connected };
                    var number = new TextBlock { Foreground = Brushes.WhiteSmoke }; number.Text = $"{slider.Value:0} {p.Unit}";
                    slider.ValueChanged += (_, _) => number.Text = $"{slider.Value:0} {p.Unit}";
                    propertyUpdates[(appliance.Id, p.Siid, p.Piid)] = current => { if (!slider.IsMouseCaptureWithin && !slider.IsKeyboardFocusWithin && current.Value is { } v && v.TryGetDouble(out var n)) slider.Value = n; };
                    slider.PreviewMouseLeftButtonUp += async (_, _) => await model.SetAsync(appliance, p, slider.Value);
                    slider.PreviewKeyUp += async (_, _) => await model.SetAsync(appliance, p, slider.Value);
                    line.Children.Add(slider); line.Children.Add(number);
                }
                if (p.Name is "target-temperature" or "target-time" or "mode") panel.Children.Add(line); else details.Children.Add(line);
            }
            panel.Children.Add(new Expander { Header = "Device details", Content = details, Foreground = Brushes.Silver, Margin = new(0, 12, 0, 0) });
            var actions = new WrapPanel();
            foreach (var action in appliance.Actions.Where(a => a.Name is "start-cook" or "resume-cook" or "resume-cooking" or "pause" or "cancel-cooking"))
            {
                var button = new Button { Content = FriendlyLabelConverter.Label(action.Name), Margin = new(0, 8, 12, 8), IsEnabled = appliance.Connected || action.Name == "cancel-cooking" };
                button.Click += async (_, _) => {
                    var current = model.Appliances.FirstOrDefault(a => a.Id == appliance.Id) ?? appliance;
                    if (!action.NeedsConfirmation) { await model.ActAsync(current, action); return; }
                    var nonce = await model.PrepareAsync(current, action); if (nonce is null) return;
                    if (MessageBox.Show(Window.GetWindow(this), $"Start cooking on {current.Name}?\nConfirm the basket is safe and ready. Target: {current.TargetTemperature}°C, {current.TargetMinutes} minutes.", "Confirm cooking start", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) == MessageBoxResult.Yes)
                        await model.ConfirmAsync(current, action, nonce);
                };
                actions.Children.Add(button);
            }
            panel.Children.Insert(2, actions); root.Children.Add(new Border { Child = panel, Background = new SolidColorBrush(Color.FromRgb(30, 30, 34)), CornerRadius = new(10), Margin = new(0, 18, 0, 0) });
        }
        UpdateReadings();
    }
    private void UpdateReadings()
    {
        if (model is null) return;
        foreach (var a in model.Appliances)
            if (displays.TryGetValue(a.Id, out var d))
            {
                d.Status.Text = $"{a.Status} · {(a.Connected ? "connected" : "offline")}\nCurrent temperature: {(a.Temperature is { } temp ? $"{temp:0}°C" : "not reported by this model")}\n{a.Warning}{(a.TurnBasket ? " · Turn basket" : "")}";
                d.Ring.Maximum = Math.Max(1, a.TargetMinutes ?? 1); d.Ring.Value = a.RemainingMinutes ?? 0;
                d.Remaining.Text = $"{a.RemainingMinutes:0} minutes remaining · {a.TargetTemperature:0}°C target";
                updating = true;
                try { foreach (var p in a.Properties) if (propertyUpdates.TryGetValue((a.Id, p.Siid, p.Piid), out var update)) update(p); }
                finally { updating = false; }
            }
    }
}
