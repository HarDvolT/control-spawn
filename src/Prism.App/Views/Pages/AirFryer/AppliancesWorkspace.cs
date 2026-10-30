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

public sealed class AppliancesWorkspace : UserControl
{
    private readonly StackPanel root = new();
    private readonly Dictionary<string, (TextBlock Status, RingGauge Ring, TextBlock Remaining)> displays = new();
    private readonly Dictionary<(string, int, int), Action<ApplianceProperty>> propertyUpdates = new();
    private readonly Dictionary<string, List<UIElement>> settingControls = new();
    private readonly Dictionary<string, WrapPanel> secondaryActions = new();
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
        var next = string.Join(";", model.Appliances.Select(a => a.Id + a.Name + a.Connected + a.Properties.Count + string.Join(",", a.History?.Select(h => h.EndedAt) ?? [])));
        if (next == signature && displays.Count > 0) { UpdateReadings(); return; }
        signature = next; root.Children.Clear(); displays.Clear(); propertyUpdates.Clear(); settingControls.Clear();
        root.Children.Add(new TextBlock { Text = "AIR FRYER", FontSize = 11, Foreground = Brushes.WhiteSmoke, Margin = new(0, 0, 0, 12) });
        var title = new TextBlock { Text = "Ready when you are", Margin = new(0, -8, 0, 6) };
        title.SetResourceReference(StyleProperty, "H1"); root.Children.Add(title);
        root.Children.Add(new TextBlock { Text = "Cooking starts only after you confirm here.", Foreground = Brushes.Silver, TextWrapping = TextWrapping.Wrap });
        var error = new TextBlock { Foreground = Brushes.OrangeRed, TextWrapping = TextWrapping.Wrap }; error.SetBinding(VisibilityProperty, new Binding("LastError") { Converter = new StringToVisibilityConverter() }); error.SetBinding(TextBlock.TextProperty, new Binding("LastError")); root.Children.Add(error);
        if (model.Appliances.Count == 0) root.Children.Add(new TextBlock { Text = "Find your fryer on the Devices page, then import its token.", Foreground = Brushes.Silver, Margin = new(0, 24, 0, 24) });
        var grid = new CardGrid { Margin = new(0, 16, 0, 0) }; root.Children.Add(grid);
        foreach (var appliance in model.Appliances)
        {
            var panel = new StackPanel();
            settingControls[appliance.Id] = new();
            panel.Children.Add(new DeviceNameEditor { DeviceId = appliance.Id, DisplayName = appliance.Name, Client = model.Client });
            var status = new TextBlock { Foreground = Brushes.WhiteSmoke, FontSize = 18, TextWrapping = TextWrapping.Wrap, Margin = new(0, 12, 0, 12) }; panel.Children.Add(status);
            var ring = new RingGauge { Width = 205, Height = 205, TickCount = 60, TickOnFill = true, Thickness = 14, ValueFontSize = 44, Maximum = appliance.TargetMinutes ?? 1 }; panel.Children.Add(ring);
            var remaining = new TextBlock { Foreground = Brushes.Silver, HorizontalAlignment = HorizontalAlignment.Center }; panel.Children.Add(remaining);
            displays[appliance.Id] = (status, ring, remaining);
            var details = new StackPanel();
            var targets = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            foreach (var p in appliance.Properties.OrderBy(p => p.Name == "target-temperature" ? 0 : p.Name == "target-time" ? 1 : 2))
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
                    var slider = new ThickSlider { Glow = false, Step = range[2], AccentColor = Color.FromRgb(54, 214, 255), Minimum = range[0], Maximum = range[1], Value = p.Value?.GetDouble() ?? range[0], IsEnabled = appliance.Connected };
                    var number = new TextBlock { Foreground = Brushes.WhiteSmoke }; number.Text = p.Name == "target-temperature" ? Format.TempUnit(slider.Value, 0) : $"{slider.Value:0} min";
                    slider.ValueChanged += (_, _) => number.Text = p.Name == "target-temperature" ? Format.TempUnit(slider.Value, 0) : $"{slider.Value:0} min";
                    propertyUpdates[(appliance.Id, p.Siid, p.Piid)] = current => { if (!slider.IsMouseCaptureWithin && !slider.IsKeyboardFocusWithin && current.Value is { } v && v.TryGetDouble(out var n)) slider.Value = n; };
                    slider.PreviewMouseLeftButtonUp += async (_, _) => await model.SetAsync(appliance, p, slider.Value);
                    slider.PreviewKeyUp += async (_, _) => await model.SetAsync(appliance, p, slider.Value);
                    var stepper = new DockPanel { Margin = new(0, 6, 0, 8) };
                    var minus = new Button { Content = "\u2212", Width = 46, Height = 46, Padding = new(0), FontSize = 22 };
                    var plus = new Button { Content = "+", Width = 46, Height = 46, Padding = new(0), FontSize = 22 };
                    DockPanel.SetDock(minus, Dock.Left); DockPanel.SetDock(plus, Dock.Right);
                    stepper.Children.Add(minus); stepper.Children.Add(plus);
                    number.FontSize = 30; number.FontWeight = FontWeights.SemiBold; number.HorizontalAlignment = HorizontalAlignment.Center; number.VerticalAlignment = VerticalAlignment.Center;
                    stepper.Children.Add(number);
                    async Task Step(double delta)
                    {
                        var current = model.Appliances.FirstOrDefault(a => a.Id == appliance.Id) ?? appliance;
                        if (!AppliancesViewModel.CanEdit(current)) return;
                        slider.Value = Math.Clamp(slider.Value + delta, range[0], range[1]);
                        await model.SetAsync(current, p, slider.Value);
                    }
                    minus.Click += async (_, _) => await Step(-range[2]); plus.Click += async (_, _) => await Step(range[2]);
                    settingControls[appliance.Id].Add(minus); settingControls[appliance.Id].Add(plus);
                    line.Children.Add(stepper); line.Children.Add(slider);
                }
                foreach (var control in line.Children.OfType<UIElement>()) settingControls[appliance.Id].Add(control);
                if (p.Name is "target-temperature" or "target-time") targets.Children.Add(line); else details.Children.Add(line);
            }
            root.Children.Add(new Expander { Header = "Device details", Content = details, Foreground = Brushes.Silver, Margin = new(0, 12, 0, 0) });
            var actions = new WrapPanel(); secondaryActions[appliance.Id] = actions;
            foreach (var action in appliance.Actions.Where(a => a.Name is "start-cook" or "resume-cook" or "resume-cooking" or "pause" or "cancel-cooking"))
            {
                var button = new Button { Content = action.Name == "start-cook" ? "Start cooking" : FriendlyLabelConverter.Label(action.Name), Margin = new(0, 8, 12, 8), IsEnabled = appliance.Connected || action.Name == "cancel-cooking" };
                if (action.Name == "start-cook") button.SetResourceReference(StyleProperty, "PrimaryPillButton");
                button.Click += async (_, _) => {
                    var current = model.Appliances.FirstOrDefault(a => a.Id == appliance.Id) ?? appliance;
                    if (action.Name is "cancel-cooking" or "pause") { await model.ActAsync(current, action); return; }
                    var nonce = await model.PrepareAsync(current, action); if (nonce is null) return;
                    if (MessageBox.Show(Window.GetWindow(this), $"Start cooking on {current.Name}?\nConfirm the basket is safe and ready. Target: {current.TargetTemperature}°C, {current.TargetMinutes} minutes.", "Confirm cooking start", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) == MessageBoxResult.Yes)
                        await model.ConfirmAsync(current, action, nonce);
                };
                if (action.Name == "start-cook") { button.HorizontalAlignment = HorizontalAlignment.Stretch; button.MinHeight = 48; button.Margin = new(0, 8, 0, 0); panel.Children.Add(button); }
                else actions.Children.Add(button);
            }
            panel.Children.Remove(ring); panel.Children.Remove(remaining); panel.Children.Remove(status);
            status.FontSize = 11; status.Margin = new(0, 4, 0, 0);
            var nameEditor = panel.Children.OfType<DeviceNameEditor>().First(); panel.Children.Remove(nameEditor); details.Children.Insert(0, nameEditor);
            var cook = new DockPanel { Margin = new(0, 8, 0, 12) };
            var timer = new StackPanel { Width = 235 }; timer.Children.Add(ring); timer.Children.Add(remaining); timer.Children.Add(status);
            DockPanel.SetDock(timer, Dock.Left); cook.Children.Add(timer); cook.Children.Add(targets); panel.Children.Insert(0, cook);
            panel.Children.Add(actions); grid.Children.Add(new GlassCard { Span = 7, Content = panel, MinHeight = 358 });
            var aside = new StackPanel(); CardGrid.SetSpan(aside, 5);
            StackPanel Info(string title, string copy) { var body = new StackPanel(); body.Children.Add(new TextBlock { Text = title, FontSize = 15, FontWeight = FontWeights.SemiBold, Margin = new(0, 0, 0, 12) }); body.Children.Add(new TextBlock { Text = copy, TextWrapping = TextWrapping.Wrap, Foreground = Brushes.Silver }); return body; }
            var presets = Info("Presets", "No saved presets available");
            var presetGrid = new SpacedGrid { Columns = 2, Gap = 10, Margin = new(0, 14, 0, 0) };
            for (int i = 0; i < 6; i++) presetGrid.Children.Add(new Border { Height = 60, CornerRadius = new(14), Background = new SolidColorBrush(Color.FromRgb(23, 24, 29)), Child = new TextBlock { Text = "\u2014", Foreground = Brushes.Gray, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center } });
            presets.Children.Add(presetGrid);
            aside.Children.Add(new GlassCard { Content = presets, MinHeight = 358 });
            var safety = new StackPanel(); safety.Children.Add(new TextBlock { Text = "Safety", FontSize = 15, FontWeight = FontWeights.SemiBold, Margin = new(0, 0, 0, 16) });
            foreach (var (label, copy) in new[] { ("Start always asks twice", "Heating needs explicit confirmation here."), ("Settings lock while cooking", "Temperature and time lock until you stop."), ("Stop stays available", "Pause or cancel while the fryer is cooking."), ("Scenes cannot start cooking", "No scene or automation starts heating.") })
            {
                var row = new DockPanel { Margin = new(0, 0, 0, 14) };
                var check = new Border { Width = 26, Height = 26, CornerRadius = new(9), Background = new SolidColorBrush(Color.FromArgb(30, 61, 220, 132)), Margin = new(0, 0, 12, 0), Child = new TextBlock { Text = "\u2713", Foreground = new SolidColorBrush(Color.FromRgb(61, 220, 132)), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center } }; DockPanel.SetDock(check, Dock.Left); row.Children.Add(check);
                var copyPanel = new StackPanel(); copyPanel.Children.Add(new TextBlock { Text = label, FontWeight = FontWeights.SemiBold }); copyPanel.Children.Add(new TextBlock { Text = copy, FontSize = 12, Foreground = Brushes.Gray, Margin = new(0, 3, 0, 0), TextWrapping = TextWrapping.Wrap }); row.Children.Add(copyPanel); safety.Children.Add(row);
            }
            var safetyCard = new GlassCard { Span = 5, MinHeight = 308, Content = safety };
            grid.Children.Add(aside);
            grid.Children.Add(safetyCard);
            var recent = Info("Recent cooks", "Observed by the Prism service");
            foreach (var record in appliance.History?.Take(4) ?? [])
            {
                var row = new StackPanel { Margin = new(16, 8, 16, 8) };
                row.Children.Add(new TextBlock { Text = record.Outcome + " \u00b7 " + Format.TempUnit(record.TargetTemperature, 0) + " \u00b7 " + Format.Number(record.TargetMinutes) + " min", FontWeight = FontWeights.SemiBold });
                row.Children.Add(new TextBlock { Text = record.EndedAt.LocalDateTime.ToString("g"), FontSize = 12, Foreground = Brushes.Gray });
                recent.Children.Add(new Border { Background = new SolidColorBrush(Color.FromRgb(23, 24, 29)), CornerRadius = new(14), Margin = new(0, 10, 0, 0), Child = row });
            }
            if (appliance.History is not { Count: > 0 }) recent.Children.Add(new TextBlock { Text = "No cooks observed yet", Foreground = Brushes.Gray, Margin = new(0, 16, 0, 0) });
            grid.Children.Add(new GlassCard { Span = 7, MinHeight = 308, Content = recent });
        }
        UpdateReadings();
    }
    private void UpdateReadings()
    {
        if (model is null) return;
        foreach (var a in model.Appliances)
            if (displays.TryGetValue(a.Id, out var d))
            {
                if (secondaryActions.TryGetValue(a.Id, out var secondary)) secondary.Visibility = a.Status == "idle" ? Visibility.Collapsed : Visibility.Visible;
                d.Status.Visibility = !string.IsNullOrWhiteSpace(a.Warning) || a.TurnBasket ? Visibility.Visible : Visibility.Collapsed;
                d.Ring.ToolTip = $"{a.Status} ? {(a.Connected ? "connected" : "offline")} ? Current temperature: {(a.Temperature is { } current ? Format.TempUnit(current, 0) : "unavailable")}";
                d.Status.Text = $"{a.Status} · {(a.Connected ? "connected" : "offline")}\nCurrent temperature: {(a.Temperature is { } temp ? $"{temp:0}°C" : "not reported by this model")}\n{a.Warning}{(a.TurnBasket ? " · Turn basket" : "")}";
                d.Ring.ValueFormat = "00':00'";
                d.Ring.RingColor = a.Status == "idle" ? Color.FromRgb(37, 39, 46) : Color.FromRgb(255, 77, 26);
                d.Ring.Maximum = Math.Max(1, a.TargetMinutes ?? 1); d.Ring.Value = a.Status == "idle" ? a.TargetMinutes ?? double.NaN : a.RemainingMinutes ?? double.NaN;
                d.Remaining.Text = $"{(a.Status == "idle" ? "Will take " + a.TargetMinutes + " min" : a.RemainingMinutes + " minutes remaining")} · {a.TargetTemperature:0}°C target";
                if (settingControls.TryGetValue(a.Id, out var controls))
                    foreach (var control in controls) control.IsEnabled = AppliancesViewModel.CanEdit(a);
                updating = true;
                try { foreach (var p in a.Properties) if (propertyUpdates.TryGetValue((a.Id, p.Siid, p.Piid), out var update)) update(p); }
                finally { updating = false; }
            }
    }
}
