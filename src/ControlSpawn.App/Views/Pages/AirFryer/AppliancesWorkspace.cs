using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Media;
using ControlSpawn.App.Controls;
using ControlSpawn.App.Common;
using ControlSpawn.App.ViewModels;
using ControlSpawn.Core.Ipc;

namespace ControlSpawn.App.Views;

/// <summary>The air fryer page from docs/design-v2/V2Fryer: a timer ring with temperature and time steppers, presets, safety and recent cooks.</summary>
public sealed class AppliancesWorkspace : UserControl
{
    private static readonly Color Cool = Color.FromRgb(54, 214, 255), Accent = Color.FromRgb(255, 77, 26), Amber = Color.FromRgb(245, 165, 36), Green = Color.FromRgb(61, 220, 132), Red = Color.FromRgb(255, 77, 79);
    private static readonly Brush Row = Frozen(Color.FromRgb(23, 24, 29)), Track = Frozen(Color.FromRgb(30, 32, 38)), Secondary = Frozen(Color.FromRgb(163, 166, 174)), Tertiary = Frozen(Color.FromRgb(124, 128, 138)), Soft = Frozen(Color.FromRgb(201, 203, 209));
    private static readonly FontFamily Emoji = new("Segoe UI Emoji");
    // WPF draws emoji as outlines, so each food gets its own colour instead.
    private static readonly Dictionary<string, Color> IconTints = new()
    {
        ["\U0001F35F"] = Amber, ["\U0001F357"] = Color.FromRgb(255, 138, 61), ["\U0001F966"] = Green, ["\U0001F41F"] = Cool,
        ["\U0001F355"] = Color.FromRgb(255, 107, 74), ["\U0001F950"] = Color.FromRgb(232, 178, 106), ["⭐"] = Accent
    };
    private static TextBlock Icon(string? icon, double size) => new()
    {
        Text = icon ?? "\U0001F373", FontFamily = Emoji, FontSize = size, VerticalAlignment = VerticalAlignment.Center,
        Foreground = new SolidColorBrush(icon is not null && IconTints.TryGetValue(icon, out var tint) ? tint : Color.FromRgb(201, 203, 209))
    };
    private readonly StackPanel root = new();
    private readonly Dictionary<string, View> views = new();
    private readonly Dictionary<(string, int, int), Action<ApplianceProperty>> propertyUpdates = new();
    private bool updating;
    private AppliancesViewModel? model;
    private string? signature;

    /// <summary>Everything a reading changes on one fryer's cards.</summary>
    private sealed class View
    {
        public TextBlock Headline = null!, Subtitle = null!, RingTop = null!, RingTime = null!, RingTemp = null!, Warning = null!, ConfirmTitle = null!;
        public WrapPanel Chips = null!;
        public StatusChip Link = new() { Margin = new(0, 0, 8, 0) }, State = new();
        public RingGauge Ring = null!;
        public Border Confirm = null!, Shake = null!;
        public ToggleSwitch ShakeSwitch = null!;
        public Button Start = null!, Pause = null!, Stop = null!, Save = null!;
        public readonly List<Stepper> Steppers = new();
        public readonly List<(Button Button, FryerPreset Preset)> Presets = new();
        public readonly List<UIElement> Locked = new(), Adjust = new();
        public (string Nonce, ApplianceActionState Action, DateTime At)? Pending;
        /// <summary>A pause or stop on its way: the button answers at once while the fryer catches up.</summary>
        public (string Action, string Status, DateTime At)? Sent;
        /// <summary>The state the fryer accepted a command for, shown until its own reading catches up.</summary>
        public (string Status, string From, DateTime At)? Expect;
        public Button Yes = null!;
    }
    private sealed class Stepper
    {
        public ApplianceProperty Property = null!;
        public double Min, Max, Step, Shown = double.NaN;
        public DateTime EditedAt;
        public TextBlock Value = null!;
        public ColumnDefinition Fill = null!, Rest = null!;
        public Func<double, string> Format = null!;
    }

    public AppliancesWorkspace()
    {
        Content = root;
        DataContextChanged += (_, _) => { if (model is not null) model.PropertyChanged -= Update; model = DataContext as AppliancesViewModel; if (model is not null) model.PropertyChanged += Update; signature = null; Rebuild(); };
        // A page view left behind by navigation must stop listening, or every visit adds one more updating every second.
        Unloaded += (_, _) => { if (model is not null) model.PropertyChanged -= Update; };
        Loaded += (_, _) => { if (model is not null) { model.PropertyChanged -= Update; model.PropertyChanged += Update; } Rebuild(); };
    }
    private void Update(object? sender, PropertyChangedEventArgs e) { if (e.PropertyName == nameof(AppliancesViewModel.Presets)) signature = null; Rebuild(); }
    private void Rebuild()
    {
        if (model is null) return;
        var next = string.Join(";", model.Appliances.Select(a => a.Id + a.Name + a.Properties.Count + string.Join(",", a.History?.Select(h => h.EndedAt) ?? [])));
        if (next == signature) { UpdateReadings(); return; }
        signature = next; root.Children.Clear(); views.Clear(); propertyUpdates.Clear();
        var first = model.Appliances.FirstOrDefault();
        var view = new View();
        root.Children.Add(Header(view, first));
        var error = new TextBlock { Foreground = Brushes.OrangeRed, TextWrapping = TextWrapping.Wrap, Margin = new(0, 8, 0, 0) }; error.SetBinding(VisibilityProperty, new Binding("LastError") { Converter = new StringToVisibilityConverter() }); error.SetBinding(TextBlock.TextProperty, new Binding("LastError")); root.Children.Add(error);
        var grid = new CardGrid { Margin = new(0, 16, 0, 0) }; root.Children.Add(grid);
        if (first is null) { AddNotConnected(grid); return; }
        foreach (var appliance in model.Appliances)
        {
            var v = appliance == first ? view : new View { Headline = new(), Subtitle = new(), Chips = new() };
            views[appliance.Id] = v;
            grid.Children.Add(CookCard(v, appliance));
            grid.Children.Add(PresetsCard(v, appliance));
            grid.Children.Add(SafetyCard(300));
            grid.Children.Add(RecentCard(v, appliance));
            root.Children.Add(new Expander { Header = "Device details", Content = Details(appliance), Foreground = Brushes.Silver, Margin = new(0, 12, 0, 0) });
        }
        UpdateReadings();
    }

    private static UIElement Header(View v, ApplianceState? appliance)
    {
        var header = new DockPanel { Margin = new(0, 0, 0, 2) };
        v.Chips = new WrapPanel { VerticalAlignment = VerticalAlignment.Bottom }; DockPanel.SetDock(v.Chips, Dock.Right); header.Children.Add(v.Chips);
        var copy = new StackPanel();
        var label = new TextBlock { Text = "AIR FRYER" }; label.SetResourceReference(StyleProperty, "SectionLabel"); copy.Children.Add(label);
        v.Headline = new TextBlock { Text = "Ready when you are", FontSize = 32, Margin = new(0, 4, 0, 0) }; v.Headline.SetResourceReference(StyleProperty, "H1"); copy.Children.Add(v.Headline);
        v.Subtitle = new TextBlock { Text = appliance is null ? "Control Spawn never starts it on its own" : "", FontSize = 13, Foreground = Secondary, Margin = new(0, 6, 0, 0), TextWrapping = TextWrapping.Wrap }; copy.Children.Add(v.Subtitle);
        header.Children.Add(copy);
        return header;
    }

    private GlassCard CookCard(View v, ApplianceState appliance)
    {
        var panel = new StackPanel();
        var top = new DockPanel();
        var dial = new Grid { Width = 220, Height = 220, Margin = new(0, 0, 26, 0) };
        v.Ring = new RingGauge { TickCount = 60, Thickness = 14, ShowValue = false, Maximum = 1 };
        dial.Children.Add(v.Ring);
        var inside = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        v.RingTop = new TextBlock { FontSize = 12, Foreground = Tertiary, HorizontalAlignment = HorizontalAlignment.Center };
        v.RingTime = new TextBlock { FontSize = 46, FontWeight = FontWeights.SemiBold, HorizontalAlignment = HorizontalAlignment.Center, Margin = new(0, 2, 0, 2) }; v.RingTime.SetResourceReference(TextBlock.FontFamilyProperty, "NumberFont");
        v.RingTemp = new TextBlock { FontSize = 16, HorizontalAlignment = HorizontalAlignment.Center }; v.RingTemp.SetResourceReference(TextBlock.FontFamilyProperty, "NumberFont");
        inside.Children.Add(v.RingTop); inside.Children.Add(v.RingTime); inside.Children.Add(v.RingTemp); dial.Children.Add(inside);
        DockPanel.SetDock(dial, Dock.Left); top.Children.Add(dial);
        var steppers = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        AddStepper(v, appliance, steppers, "target-temperature", "Temperature", Accent, n => Format.TempUnit(n, 0));
        AddStepper(v, appliance, steppers, "target-time", "Time", Amber, n => $"{n:0} min");
        top.Children.Add(steppers);
        panel.Children.Add(top);

        v.Warning = new TextBlock { Foreground = new SolidColorBrush(Amber), TextWrapping = TextWrapping.Wrap, Margin = new(0, 14, 0, 0), Visibility = Visibility.Collapsed };
        panel.Children.Add(v.Warning);

        // Starting asks twice, inline: the first tap only fetches a one-use confirmation from the service.
        var confirm = new DockPanel();
        var mark = new TextBlock { Text = "⚠", FontSize = 20, Foreground = new SolidColorBrush(Amber), VerticalAlignment = VerticalAlignment.Center, Margin = new(0, 0, 14, 0) }; DockPanel.SetDock(mark, Dock.Left); confirm.Children.Add(mark);
        var yes = v.Yes = new Button { Content = "Yes, start cooking", MinHeight = 42, Padding = new(20, 0, 20, 0), Margin = new(10, 0, 0, 0) }; yes.SetResourceReference(StyleProperty, "PrimaryPillButton"); yes.FontSize = 13;
        var no = new Button { Content = "Not yet", MinHeight = 42, Padding = new(18, 0, 18, 0), Margin = new(14, 0, 0, 0) }; no.SetResourceReference(StyleProperty, "PillButton");
        DockPanel.SetDock(yes, Dock.Right); DockPanel.SetDock(no, Dock.Right); confirm.Children.Add(yes); confirm.Children.Add(no);
        var ask = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        v.ConfirmTitle = new TextBlock { FontSize = 14, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap }; ask.Children.Add(v.ConfirmTitle);
        ask.Children.Add(new TextBlock { Text = "Check the basket is in and nothing is on top of the fryer.", FontSize = 12, Foreground = Soft, Margin = new(0, 2, 0, 0), TextWrapping = TextWrapping.Wrap });
        confirm.Children.Add(ask);
        v.Confirm = new Border { Child = confirm, Padding = new(16, 14, 16, 14), Margin = new(0, 18, 0, 0), CornerRadius = new(16), Background = new SolidColorBrush(Color.FromArgb(20, Amber.R, Amber.G, Amber.B)), BorderBrush = new SolidColorBrush(Color.FromArgb(115, Amber.R, Amber.G, Amber.B)), BorderThickness = new(1), Visibility = Visibility.Collapsed };
        panel.Children.Add(v.Confirm);
        no.Click += (_, _) => { v.Pending = null; UpdateReadings(); };
        yes.Click += async (_, _) =>
        {
            if (v.Pending is not { } pending || model is null) return;
            var current = Current(appliance);
            // The service drops a confirmation after 30 seconds; fetch a fresh one rather than fail.
            var nonce = DateTime.UtcNow - pending.At > TimeSpan.FromSeconds(25) ? await model.PrepareAsync(current, pending.Action) : pending.Nonce;
            v.Pending = null; UpdateReadings();
            if (nonce is null) return;
            await model.ConfirmAsync(current, pending.Action, nonce);
            if (!model.HasError) { v.Expect = ("cooking", current.Status, DateTime.UtcNow); UpdateReadings(); }
        };

        var buttons = new UniformGrid { Rows = 1, Margin = new(0, 18, 0, 0) };
        v.Start = new Button { Content = "Start cooking", MinHeight = 48 }; v.Start.SetResourceReference(StyleProperty, "PrimaryPillButton");
        v.Pause = new Button { Content = "Pause", MinHeight = 48, Margin = new(0, 0, 10, 0) }; v.Pause.SetResourceReference(StyleProperty, "PillButton");
        v.Stop = new Button { Content = "Stop cooking", MinHeight = 48, Foreground = new SolidColorBrush(Color.FromRgb(255, 138, 138)), Background = new SolidColorBrush(Color.FromArgb(26, Red.R, Red.G, Red.B)), BorderBrush = new SolidColorBrush(Color.FromArgb(128, Red.R, Red.G, Red.B)) };
        v.Stop.SetResourceReference(StyleProperty, "PillButton"); v.Stop.FontWeight = FontWeights.Bold;
        buttons.Children.Add(v.Start); buttons.Children.Add(v.Pause); buttons.Children.Add(v.Stop);
        panel.Children.Add(buttons);
        v.Start.Click += async (_, _) =>
        {
            if (model is null) return;
            var current = Current(appliance);
            var action = current.Actions.FirstOrDefault(a => a.Name == ((v.Expect?.Status ?? current.Status) == "paused" ? "resume-cook" : "start-cook"));
            if (action is null) return;
            var nonce = await model.PrepareAsync(current, action); if (nonce is null) return;
            v.Pending = (nonce, action, DateTime.UtcNow); UpdateReadings();
        };
        async Task Send(string name)
        {
            var current = Current(appliance);
            if (model is null || current.Actions.FirstOrDefault(a => a.Name == name) is not { } action) return;
            v.Sent = (name, current.Status, DateTime.UtcNow); UpdateReadings();
            await model.ActAsync(current, action);
            v.Sent = null;
            if (!model.HasError) v.Expect = (name == "pause" ? "paused" : "idle", current.Status, DateTime.UtcNow);
            UpdateReadings();
        }
        v.Pause.Click += async (_, _) => await Send("pause");
        v.Stop.Click += async (_, _) => await Send("cancel-cooking");
        return new GlassCard { Span = 7, Content = panel, MinHeight = 356 };
    }

    private void AddStepper(View v, ApplianceState appliance, StackPanel host, string name, string label, Color end, Func<double, string> format)
    {
        var p = appliance.Properties.FirstOrDefault(p => p.Name == name);
        var range = p is { Writable: true, Range: { Length: 3 } r } ? r : null;
        // Fryers accept a time limit of many hours; a 90-minute scale keeps everyday times readable.
        var s = new Stepper { Property = p!, Min = range?[0] ?? 0, Max = range is null ? 1 : name == "target-time" ? Math.Min(range[1], 90) : range[1], Step = range?[2] ?? 1, Format = format };
        var line = new StackPanel { Margin = new(0, 0, 0, 16) };
        var caption = new TextBlock { Text = label, Margin = new(0, 0, 0, 8) }; caption.SetResourceReference(StyleProperty, "SectionLabel"); line.Children.Add(caption);
        var row = new DockPanel();
        Button Square(string glyph, double delta, string name)
        {
            var b = new Button { Content = new TextBlock { Text = glyph, FontSize = 22, Margin = new(0, -3, 0, 0) }, Width = 46, Height = 46, MinHeight = 46, Padding = new(0), FontSize = 22, FontWeight = FontWeights.Normal, Background = Track };
            System.Windows.Automation.AutomationProperties.SetName(b, name);
            // Holding repeats, so 15 to 45 minutes is one press, not thirty; the release then isn't one more step.
            var repeat = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(450) };
            bool repeated = false;
            repeat.Tick += async (_, _) => { repeat.Interval = TimeSpan.FromMilliseconds(110); repeated = true; await Step(v, appliance, s, delta); };
            void Stop() => repeat.Stop();
            b.PreviewMouseLeftButtonDown += (_, _) => { repeated = false; repeat.Interval = TimeSpan.FromMilliseconds(450); repeat.Start(); };
            b.PreviewMouseLeftButtonUp += (_, _) => Stop(); b.MouseLeave += (_, _) => Stop(); b.LostMouseCapture += (_, _) => Stop(); b.IsEnabledChanged += (_, _) => Stop();
            b.Click += async (_, _) => { if (repeated) { repeated = false; return; } await Step(v, appliance, s, delta); };
            v.Adjust.Add(b); return b;
        }
        var minus = Square("−", -s.Step, "Lower " + label.ToLowerInvariant()); var plus = Square("+", s.Step, "Raise " + label.ToLowerInvariant());
        row.MouseWheel += async (_, e) => { if (plus.IsEnabled) { e.Handled = true; await Step(v, appliance, s, e.Delta > 0 ? s.Step : -s.Step); } };
        DockPanel.SetDock(minus, Dock.Left); DockPanel.SetDock(plus, Dock.Right); row.Children.Add(minus); row.Children.Add(plus);
        s.Value = new TextBlock { Text = "—", FontSize = 30, FontWeight = FontWeights.SemiBold, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center }; s.Value.SetResourceReference(TextBlock.FontFamilyProperty, "NumberFont");
        row.Children.Add(s.Value); line.Children.Add(row);
        var bar = new Grid { Height = 6, Margin = new(0, 10, 0, 0) };
        s.Fill = new ColumnDefinition { Width = new(0, GridUnitType.Star) }; s.Rest = new ColumnDefinition { Width = new(1, GridUnitType.Star) };
        bar.ColumnDefinitions.Add(s.Fill); bar.ColumnDefinitions.Add(s.Rest);
        var track = new Border { Background = Track, CornerRadius = new(3) }; Grid.SetColumnSpan(track, 2); bar.Children.Add(track);
        bar.Children.Add(new Border { CornerRadius = new(3), Background = new LinearGradientBrush(Cool, end, 0) });
        line.Children.Add(bar);
        host.Children.Add(line);
        if (range is not null) v.Steppers.Add(s); else { minus.IsEnabled = plus.IsEnabled = false; v.Adjust.Remove(minus); v.Adjust.Remove(plus); }
    }

    // A tap shows the new value at once; the reading from the fryer takes over again a moment later.
    private async Task Step(View v, ApplianceState appliance, Stepper s, double delta)
    {
        var current = Current(appliance);
        if (model is null || !AppliancesViewModel.CanAdjust(current)) return;
        var from = double.IsNaN(s.Shown) ? s.Min : s.Shown;
        var next = Math.Clamp(Math.Round((from + delta) / s.Step) * s.Step, s.Min, s.Max);
        if (next == from) return;
        Show(s, next); s.EditedAt = DateTime.UtcNow; v.Pending = null; UpdateReadings();
        await model.SetAsync(current, s.Property, next);
    }
    private static void Show(Stepper s, double value)
    {
        s.Shown = value; s.Value.Text = s.Format(value);
        double t = s.Max > s.Min ? Math.Clamp((value - s.Min) / (s.Max - s.Min), 0, 1) : 0;
        s.Fill.Width = new(t, GridUnitType.Star); s.Rest.Width = new(1 - t, GridUnitType.Star);
    }

    private GlassCard PresetsCard(View v, ApplianceState appliance)
    {
        var panel = new StackPanel();
        var head = new DockPanel { Margin = new(0, 0, 0, 12) };
        v.Save = new Button { Content = "+ Save current", MinHeight = 34, Padding = new(14, 0, 14, 0), FontSize = 12, VerticalAlignment = VerticalAlignment.Center, ToolTip = "Keep the temperature and time as a preset of your own" };
        v.Save.SetResourceReference(StyleProperty, "PillButton");
        v.Save.Click += (_, _) => { var a = Current(appliance); if (a.TargetTemperature is { } t && a.TargetMinutes is { } m) model?.SavePreset(t, m); };
        DockPanel.SetDock(v.Save, Dock.Right); head.Children.Add(v.Save);
        head.Children.Add(Title("Presets", "Pick one, adjust, then start"));
        panel.Children.Add(head);
        var temp = appliance.Properties.FirstOrDefault(p => p.Name == "target-temperature" && p.Writable);
        var time = appliance.Properties.FirstOrDefault(p => p.Name == "target-time" && p.Writable);
        var presets = new SpacedGrid { Columns = 2, Gap = 10 };
        foreach (var preset in model!.Presets)
        {
            var row = new DockPanel();
            var icon = Icon(preset.Icon, 22); icon.Margin = new(0, 0, 12, 0); DockPanel.SetDock(icon, Dock.Left); row.Children.Add(icon);
            var copy = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            copy.Children.Add(new TextBlock { Text = preset.Name, FontSize = 14, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis });
            var sub = new TextBlock { Text = Setting(preset.Temperature, preset.Minutes), FontSize = 12, Foreground = Secondary, Margin = new(0, 2, 0, 0) }; sub.SetResourceReference(TextBlock.FontFamilyProperty, "NumberFont"); copy.Children.Add(sub);
            row.Children.Add(copy);
            var button = new Button { Content = row, MinHeight = 60, HorizontalContentAlignment = HorizontalAlignment.Stretch, Padding = new(14, 10, 14, 10), IsEnabled = temp is not null && time is not null };
            button.SetResourceReference(StyleProperty, "PresetButton");
            // A preset only fills in temperature and time; heating still needs the confirmed Start.
            button.Click += async (_, _) =>
            {
                var current = Current(appliance);
                if (!AppliancesViewModel.CanEdit(current) || temp is null || time is null) return;
                foreach (var s in v.Steppers) { Show(s, s.Property.Name == "target-temperature" ? preset.Temperature : preset.Minutes); s.EditedAt = DateTime.UtcNow; }
                v.Pending = null; UpdateReadings();
                await model.SetAsync(current, (temp, preset.Temperature), (time, preset.Minutes));
            };
            if (model.IsSaved(preset))
            {
                var remove = new MenuItem { Header = "Remove preset" }; remove.Click += (_, _) => model.RemovePreset(preset);
                button.ContextMenu = new ContextMenu { Items = { remove } };
            }
            if (button.IsEnabled) v.Locked.Add(button);
            v.Presets.Add((button, preset)); presets.Children.Add(button);
        }
        panel.Children.Add(presets);
        // The fryer's own turn-basket reminder: it beeps halfway, and the app shows "shake the basket".
        if (appliance.Properties.FirstOrDefault(p => p.Name == "turn-pot-config" && p.Writable) is { } shake)
        {
            var line = new DockPanel();
            v.ShakeSwitch = new ToggleSwitch { VerticalAlignment = VerticalAlignment.Center, IsChecked = shake.Value?.ValueKind == System.Text.Json.JsonValueKind.True };
            v.ShakeSwitch.Checked += async (_, _) => { if (!updating) await model.SetAsync(Current(appliance), shake, true); };
            v.ShakeSwitch.Unchecked += async (_, _) => { if (!updating) await model.SetAsync(Current(appliance), shake, false); };
            propertyUpdates[(appliance.Id, shake.Siid, shake.Piid)] = current => v.ShakeSwitch.IsChecked = current.Value?.ValueKind == System.Text.Json.JsonValueKind.True;
            DockPanel.SetDock(v.ShakeSwitch, Dock.Right); line.Children.Add(v.ShakeSwitch);
            var copy = new StackPanel();
            copy.Children.Add(new TextBlock { Text = "Shake reminder", FontSize = 13, FontWeight = FontWeights.SemiBold });
            copy.Children.Add(new TextBlock { Text = "Remind me halfway through", FontSize = 12, Foreground = Tertiary, Margin = new(0, 2, 0, 0) });
            line.Children.Add(copy);
            v.Shake = new Border { Child = line, Background = Row, CornerRadius = new(14), Padding = new(14, 12, 14, 12), Margin = new(0, 12, 0, 0) };
            v.Adjust.Add(v.ShakeSwitch);
            panel.Children.Add(v.Shake);
        }
        return new GlassCard { Span = 5, Content = panel, MinHeight = 356 };
    }

    private GlassCard RecentCard(View v, ApplianceState appliance)
    {
        var panel = new StackPanel();
        var head = new DockPanel { Margin = new(0, 0, 0, 2) };
        var hint = new TextBlock { Text = "Tap one to load it again", FontSize = 12, Foreground = Tertiary, VerticalAlignment = VerticalAlignment.Center }; DockPanel.SetDock(hint, Dock.Right); head.Children.Add(hint);
        head.Children.Add(new TextBlock { Text = "Recent cooks", FontSize = 15, FontWeight = FontWeights.SemiBold });
        panel.Children.Add(head);
        var temp = appliance.Properties.FirstOrDefault(p => p.Name == "target-temperature" && p.Writable);
        var time = appliance.Properties.FirstOrDefault(p => p.Name == "target-time" && p.Writable);
        foreach (var record in appliance.History?.Take(4) ?? [])
        {
            var preset = model!.Match(record.TargetTemperature, record.TargetMinutes);
            var grid = new Grid();
            grid.ColumnDefinitions.Add(new() { Width = new(30) }); grid.ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) }); grid.ColumnDefinitions.Add(new() { Width = GridLength.Auto }); grid.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
            grid.Children.Add(Icon(preset?.Icon, 18));
            var copy = new StackPanel { Margin = new(12, 0, 12, 0) }; Grid.SetColumn(copy, 1);
            copy.Children.Add(new TextBlock { Text = preset?.Name ?? "Custom cook", FontSize = 13, FontWeight = FontWeights.SemiBold });
            copy.Children.Add(new TextBlock { Text = When(record.EndedAt.LocalDateTime, DateTime.Now), FontSize = 12, Foreground = Tertiary });
            grid.Children.Add(copy);
            var setting = new TextBlock { Text = Setting(record.TargetTemperature, record.TargetMinutes), FontSize = 13, Foreground = Soft, VerticalAlignment = VerticalAlignment.Center, Margin = new(0, 0, 12, 0) }; setting.SetResourceReference(TextBlock.FontFamilyProperty, "NumberFont");
            Grid.SetColumn(setting, 2); grid.Children.Add(setting);
            bool done = record.Outcome == "Finished";
            var chip = Chip(done ? "Done" : "Stopped early", done ? Green : Amber); Grid.SetColumn(chip, 3); grid.Children.Add(chip);
            var button = new Button { Content = grid, HorizontalContentAlignment = HorizontalAlignment.Stretch, Padding = new(12, 10, 12, 10), Margin = new(0, 10, 0, 0), Background = Row, BorderBrush = Frozen(Color.FromArgb(10, 255, 255, 255)) };
            button.Click += async (_, _) =>
            {
                var current = Current(appliance);
                if (!AppliancesViewModel.CanEdit(current) || temp is null || time is null || record.TargetTemperature is not { } t || record.TargetMinutes is not { } m) return;
                foreach (var s in v.Steppers) { Show(s, s.Property.Name == "target-temperature" ? t : m); s.EditedAt = DateTime.UtcNow; }
                v.Pending = null; UpdateReadings();
                await model!.SetAsync(current, (temp, t), (time, m));
            };
            if (temp is not null && time is not null) v.Locked.Add(button);
            panel.Children.Add(button);
        }
        if (appliance.History is not { Count: > 0 })
            panel.Children.Add(new TextBlock { Text = "No cooks yet. Each cook Control Spawn sees shows up here.", Foreground = Tertiary, FontSize = 12, Margin = new(0, 14, 0, 0), TextWrapping = TextWrapping.Wrap });
        return new GlassCard { Span = 7, MinHeight = 300, Content = panel };
    }

    // Every other setting the fryer reports, for people who want them; the cards above cover everyday cooking.
    private StackPanel Details(ApplianceState appliance)
    {
        var details = new StackPanel();
        details.Children.Add(new DeviceNameEditor { DeviceId = appliance.Id, DisplayName = appliance.Name, Client = model!.Client });
        var v = views[appliance.Id];
        foreach (var p in appliance.Properties.Where(p => p.Name is not ("target-temperature" or "target-time" or "turn-pot-config")))
        {
            var line = new StackPanel { Margin = new(0, 8, 0, 8) };
            line.Children.Add(new TextBlock { Text = char.ToUpperInvariant(p.Name[0]) + p.Name[1..].Replace('-', ' '), Foreground = Brushes.Silver });
            bool safe = p.Writable && p.Name is "mode" or "preheat" or "auto-keep-warm" or "texture" or "cooking-weight" or "target-cooking-measure";
            if (safe && p.Format == "bool")
            {
                var toggle = new ToggleSwitch { IsChecked = p.Value?.ValueKind == System.Text.Json.JsonValueKind.True, HorizontalAlignment = HorizontalAlignment.Left };
                toggle.Checked += async (_, _) => { if (!updating) await model.SetAsync(Current(appliance), p, true); }; toggle.Unchecked += async (_, _) => { if (!updating) await model.SetAsync(Current(appliance), p, false); };
                propertyUpdates[(appliance.Id, p.Siid, p.Piid)] = current => toggle.IsChecked = current.Value?.ValueKind == System.Text.Json.JsonValueKind.True;
                line.Children.Add(toggle); v.Locked.Add(toggle);
            }
            else if (safe && p.Values is { } values)
            {
                var segments = new SegmentedControl { ItemsSource = values.Values.ToArray() };
                segments.ItemsPanel = new ItemsPanelTemplate(new FrameworkElementFactory(typeof(WrapPanel)));
                segments.SelectionChanged += async (_, _) => { if (!updating && segments.SelectedItem is string selected) await model.SetAsync(Current(appliance), p, values.Single(x => x.Value == selected).Key); };
                propertyUpdates[(appliance.Id, p.Siid, p.Piid)] = current => { if (current.Value is { } value && value.TryGetInt32(out var key)) segments.SelectedItem = values.GetValueOrDefault(key); };
                line.Children.Add(segments); v.Locked.Add(segments);
            }
            else
            {
                var text = new TextBlock { Foreground = Brushes.WhiteSmoke };
                propertyUpdates[(appliance.Id, p.Siid, p.Piid)] = current => text.Text = current.Value is { } value && value.ValueKind == System.Text.Json.JsonValueKind.Number && value.TryGetInt32(out var key) && current.Values?.TryGetValue(key, out var label) == true ? label : FriendlyLabelConverter.Label(current.Value?.ToString());
                line.Children.Add(text);
            }
            details.Children.Add(line);
        }
        return details;
    }

    private static GlassCard SafetyCard(double minHeight)
    {
        var safety = new StackPanel();
        var head = new DockPanel { Margin = new(0, 0, 0, 14) };
        var chip = new StatusChip { Level = StatusLevel.Ok, Text = "Built in", VerticalAlignment = VerticalAlignment.Center }; DockPanel.SetDock(chip, Dock.Right); head.Children.Add(chip);
        head.Children.Add(new TextBlock { Text = "Safety", FontSize = 15, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center });
        safety.Children.Add(head);
        foreach (var (label, copy) in new[] { ("Start always asks twice", "Nothing heats up from a single tap, a scene or an automation"), ("Changes never start heating", "Adjust temperature and time mid-cook; presets wait until you stop"), ("Stop works even if Control Spawn closes", "The fryer keeps its own timer and shuts off at zero"), ("You get a notification when it is done", "In the tray and the notification bell") })
        {
            var row = new DockPanel { Margin = new(0, 0, 0, 14) };
            var check = new Border { Width = 26, Height = 26, CornerRadius = new(8), Background = new SolidColorBrush(Color.FromArgb(30, Green.R, Green.G, Green.B)), Margin = new(0, 0, 12, 0), VerticalAlignment = VerticalAlignment.Top, Child = new TextBlock { Text = "✓", FontWeight = FontWeights.Bold, Foreground = new SolidColorBrush(Green), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center } };
            DockPanel.SetDock(check, Dock.Left); row.Children.Add(check);
            var copyPanel = new StackPanel(); copyPanel.Children.Add(new TextBlock { Text = label, FontSize = 13, FontWeight = FontWeights.SemiBold }); copyPanel.Children.Add(new TextBlock { Text = copy, FontSize = 12, Foreground = Tertiary, Margin = new(0, 2, 0, 0), TextWrapping = TextWrapping.Wrap }); row.Children.Add(copyPanel);
            safety.Children.Add(row);
        }
        return new GlassCard { Span = 5, MinHeight = minHeight, Content = safety };
    }

    // Same layout as a connected fryer, so the page never looks empty: a dimmed timer with a way to set one up.
    private void AddNotConnected(CardGrid grid)
    {
        var panel = new DockPanel();
        var ring = new RingGauge { Width = 205, Height = 205, TickCount = 60, Thickness = 14, ShowValue = false, Value = 0, Maximum = 1, RingColor = Color.FromRgb(37, 39, 46), Margin = new(0, 0, 26, 0) };
        DockPanel.SetDock(ring, Dock.Left); panel.Children.Add(ring);
        var copy = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        copy.Children.Add(new TextBlock { Text = "No air fryer connected", FontSize = 20, FontWeight = FontWeights.SemiBold });
        copy.Children.Add(new TextBlock { Text = "Sign in with your Mi Home account once and Control Spawn adds the fryer for you. The timer, temperature and presets then appear here.", Foreground = Brushes.Silver, TextWrapping = TextWrapping.Wrap, Margin = new(0, 8, 0, 18) });
        var open = new Button { Content = "Add your air fryer", HorizontalAlignment = HorizontalAlignment.Left, MinHeight = 44, Padding = new(20, 0, 20, 0) };
        open.SetResourceReference(StyleProperty, "PrimaryPillButton");
        open.SetBinding(ButtonBase.CommandProperty, new Binding("OpenPageCommand"));
        open.CommandParameter = "AddDevice";
        copy.Children.Add(open);
        panel.Children.Add(copy);
        grid.Children.Add(new GlassCard { Span = 7, MinHeight = 308, Content = panel });
        grid.Children.Add(SafetyCard(308));
    }

    private void UpdateReadings()
    {
        if (model is null) return;
        foreach (var a in model.Appliances)
        {
            if (!views.TryGetValue(a.Id, out var v)) continue;
            if (v.Expect is { } expect && (a.Status != expect.From || DateTime.UtcNow - expect.At > TimeSpan.FromSeconds(8))) v.Expect = null;
            string status = !a.Connected ? "offline" : v.Expect?.Status ?? a.Status;
            bool running = status is "cooking" or "preheating", locked = status is "cooking" or "preheating" or "paused" or "scheduled";
            var preset = model.Match(a.TargetTemperature, a.TargetMinutes);
            var color = status switch { "cooking" or "preheating" => Accent, "paused" or "scheduled" => Amber, "done" => Green, "fault" => Red, "offline" => Color.FromRgb(124, 128, 138), _ => Cool };
            v.Headline.Text = status switch
            {
                "cooking" => preset is null ? "Cooking" : "Cooking " + preset.Name.ToLowerInvariant(), "preheating" => "Preheating", "paused" => "Paused", "done" => "Food is ready",
                "scheduled" => "Scheduled", "fault" => "The fryer needs a look", "offline" => "The fryer is offline", _ => "Ready when you are"
            };
            v.Subtitle.Text = string.Join(" · ", new[] { ModelName(a.Model), model.Ip(a.Id), "Control Spawn never starts it on its own" }.Where(s => !string.IsNullOrEmpty(s)));
            if (v.Chips.Children.Count == 0) { v.Chips.Children.Add(v.Link); v.Chips.Children.Add(v.State); }
            v.Link.Level = a.Connected ? StatusLevel.Ok : StatusLevel.Unavailable; v.Link.Text = a.Connected ? "Connected" : "Offline";
            var stateText = status switch { "cooking" => a.RemainingMinutes is { } left ? $"Cooking · {left:0} min left" : "Cooking", "preheating" => "Preheating", "paused" => "Paused", "done" => "Done", "scheduled" => "Scheduled", "fault" => "Fault", _ => "Idle" };
            if (a.TurnBasket) stateText = "Shake the basket";
            v.State.Visibility = status == "offline" ? Visibility.Collapsed : Visibility.Visible;
            v.State.Level = status switch { "cooking" or "preheating" or "fault" => StatusLevel.Hot, "paused" or "scheduled" => StatusLevel.Warm, "done" => StatusLevel.Ok, _ => StatusLevel.Cool }; v.State.Text = stateText;

            double total = Math.Max(1, a.TargetMinutes ?? 1), left2 = a.RemainingMinutes ?? total;
            v.Ring.RingColor = color; v.Ring.Maximum = total;
            v.Ring.Value = status switch { "done" => total, "cooking" or "preheating" or "paused" => Math.Clamp(total - left2, 0, total), _ => 0 };
            if (v.Ring.Value == 0) v.Ring.Effect = null; // An empty ring has nothing to glow.
            v.RingTop.Text = status switch { "cooking" => "Time left", "preheating" => "Preheating", "paused" => "Paused at", "done" => "Finished", _ => "Will take" };
            double shownMinutes = status switch { "done" => 0, "cooking" or "preheating" or "paused" => left2, _ => a.TargetMinutes ?? 0 };
            v.RingTime.Text = $"{(int)shownMinutes}:{(int)Math.Round(shownMinutes % 1 * 60):00}";
            v.RingTemp.Text = a.TargetTemperature is { } target ? Format.TempUnit(target, 0) : "";
            v.RingTemp.Foreground = Tint(color);
            v.Ring.ToolTip = a.Temperature is { } now ? "Current temperature " + Format.TempUnit(now, 0) : null;
            v.Warning.Text = a.Warning ?? ""; v.Warning.Visibility = string.IsNullOrWhiteSpace(a.Warning) ? Visibility.Collapsed : Visibility.Visible;

            foreach (var s in v.Steppers)
                // Keep a tapped value until the fryer reports it (or gives up), so it never jumps back to the old one.
                if (a.Properties.FirstOrDefault(p => p.Siid == s.Property.Siid && p.Piid == s.Property.Piid)?.Value is { } value && value.TryGetDouble(out var n)
                    && (n == s.Shown || DateTime.UtcNow - s.EditedAt > TimeSpan.FromSeconds(8)))
                    Show(s, n);
            double? shownTemp = v.Steppers.FirstOrDefault(s => s.Property.Name == "target-temperature")?.Shown, shownTime = v.Steppers.FirstOrDefault(s => s.Property.Name == "target-time")?.Shown;
            foreach (var (button, p) in v.Presets) button.Tag = p.Temperature == shownTemp && p.Minutes == shownTime ? "on" : null;

            bool canEdit = AppliancesViewModel.CanEdit(a);
            foreach (var control in v.Locked) control.IsEnabled = canEdit;
            foreach (var control in v.Adjust) control.IsEnabled = AppliancesViewModel.CanAdjust(a);
            v.Save.IsEnabled = canEdit && a.TargetTemperature is not null && a.TargetMinutes is not null && model.Match(shownTemp, shownTime) is null;
            if (!a.Connected || running) v.Pending = null;
            v.Confirm.Visibility = v.Pending is null ? Visibility.Collapsed : Visibility.Visible;
            if (v.Pending is { } pending)
            {
                v.Yes.Content = pending.Action.Name == "resume-cook" ? "Yes, resume" : "Yes, start cooking";
                v.ConfirmTitle.Text = pending.Action.Name == "resume-cook" ? "Resume cooking?" : $"Start {(model.Match(shownTemp, shownTime)?.Name.ToLowerInvariant() ?? "cooking")} · {Format.TempUnit(shownTemp, 0)} · {shownTime:0} min?";
            }
            v.Start.Content = status == "paused" ? "Resume" : status == "done" ? "Cook again" : "Start cooking";
            v.Start.Visibility = a.Connected && !running && v.Pending is null ? Visibility.Visible : Visibility.Collapsed;
            v.Start.IsEnabled = a.Connected && status is not ("scheduled" or "fault");
            v.Pause.Visibility = running && a.Actions.Any(x => x.Name == "pause") ? Visibility.Visible : Visibility.Collapsed;
            if (v.Sent is { } sent && DateTime.UtcNow - sent.At > TimeSpan.FromSeconds(8)) v.Sent = null;
            v.Pause.Content = v.Sent?.Action == "pause" ? "Pausing…" : "Pause"; v.Stop.Content = v.Sent?.Action == "cancel-cooking" ? "Stopping…" : "Stop cooking";
            v.Pause.IsEnabled = v.Stop.IsEnabled = v.Sent is null;
            v.Stop.Visibility = locked || !a.Connected && a.Status is "cooking" or "preheating" or "paused" ? Visibility.Visible : Visibility.Collapsed;
            v.Stop.Margin = status == "paused" && v.Start.Visibility == Visibility.Visible ? new(10, 0, 0, 0) : new(0);
            updating = true;
            try { foreach (var p in a.Properties) if (propertyUpdates.TryGetValue((a.Id, p.Siid, p.Piid), out var update)) update(p); }
            finally { updating = false; }
        }
    }

    private ApplianceState Current(ApplianceState appliance) => model?.Appliances.FirstOrDefault(a => a.Id == appliance.Id) ?? appliance;
    private static StackPanel Title(string title, string copy)
    {
        var body = new StackPanel();
        body.Children.Add(new TextBlock { Text = title, FontSize = 15, FontWeight = FontWeights.SemiBold });
        body.Children.Add(new TextBlock { Text = copy, FontSize = 12, Foreground = Tertiary, Margin = new(0, 2, 0, 0) });
        return body;
    }
    private static Border Chip(string text, Color color) => new()
    {
        CornerRadius = new(15), MinHeight = 30, Padding = new(12, 0, 12, 0), VerticalAlignment = VerticalAlignment.Center, BorderThickness = new(1),
        Background = new SolidColorBrush(Color.FromArgb(26, color.R, color.G, color.B)), BorderBrush = new SolidColorBrush(Color.FromArgb(85, color.R, color.G, color.B)),
        Child = new TextBlock { Text = text, FontSize = 12, VerticalAlignment = VerticalAlignment.Center }
    };
    private static string Setting(double? temperature, double? minutes) => $"{temperature:0}° · {minutes:0} min";
    public static string ModelName(string model)
    {
        var parts = model.Split('.');
        return parts.Length < 3 ? model : CultureInfo.InvariantCulture.TextInfo.ToTitleCase(parts[0]) + " " + parts[^1].ToUpperInvariant();
    }
    public static string When(DateTime at, DateTime now)
    {
        var day = (now.Date - at.Date).Days;
        var date = day switch { 0 => "Today", 1 => "Yesterday", < 7 => at.ToString("ddd", CultureInfo.CurrentCulture), _ => at.ToString("d MMM", CultureInfo.CurrentCulture) };
        return date + " · " + at.ToString("HH:mm", CultureInfo.CurrentCulture);
    }
    private static Brush Frozen(Color color) { var b = new SolidColorBrush(color); b.Freeze(); return b; }
    private static readonly Dictionary<Color, Brush> Tints = new();
    private static Brush Tint(Color color) => Tints.TryGetValue(color, out var b) ? b : Tints[color] = Frozen(color);
}
