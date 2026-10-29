using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using Prism.App.Controls;
using Prism.App.ViewModels;
using Prism.Core.Ipc;
using Prism.Lighting;

namespace Prism.App.Views;

/// <summary>Sensor drafts survive state refreshes; Apply persists one complete validated binding.</summary>
public sealed class SensorBindingPanel : StackPanel
{
    private readonly TabControl targets = new() { Margin = new(0, 8, 0, 8) };
    private readonly ComboBox zones = new() { Margin = new(0, 4, 0, 8), DisplayMemberPath = "Value", SelectedValuePath = "Key" };
    private readonly ComboBox sources = new() { DisplayMemberPath = "Name", SelectedValuePath = "Id", Margin = new(0, 4, 0, 8) };
    private readonly SegmentedControl styles = new() { ItemsSource = new[] { "Gradient", "Fill bar", "Breathing", "Brightness", "Threshold" } };
    private readonly ThickSlider min = Slider(-100, 10000), max = Slider(-100, 10000), smoothing = Slider(0, 5);
    private readonly CheckBox auto = new() { Content = "Auto-learn range", Foreground = Brushes.WhiteSmoke }, alert = new() { Content = "Red pulse alert above", Foreground = Brushes.WhiteSmoke };
    private readonly TextBox palette = new() { Text = "#0080FF, #00D060, #FFB020, #FF2000", Margin = new(0, 4, 0, 8) }, threshold = new() { Text = "95", Margin = new(0, 4, 0, 8) };
    private readonly TextBlock reading = new() { TextWrapping = TextWrapping.Wrap, Margin = new(0, 8, 0, 8) }, status = new() { TextWrapping = TextWrapping.Wrap };
    private readonly Grid range = new() { Height = 16, Margin = new(0, 4, 0, 8) };
    private readonly Border marker = new() { Width = 3, Background = Brushes.White, HorizontalAlignment = HorizontalAlignment.Left };
    private readonly SensorLighting session = new();
    private readonly Stopwatch clock = Stopwatch.StartNew();
    private LightingViewModel? model;
    private bool suppress;
    private string signature = "";
    private string? Target => zones.SelectedValue as string;
    public SensorBindingPanel()
    {
        zones.DisplayMemberPath = ""; sources.DisplayMemberPath = "";
        zones.ItemTemplate = MemberTemplate("Value"); sources.ItemTemplate = MemberTemplate("Name");
        targets.Background = Brushes.Transparent; targets.BorderThickness = new(0);
        var tabStyle = new Style(typeof(TabItem));
        var chrome = new FrameworkElementFactory(typeof(Border)); chrome.SetValue(Border.PaddingProperty, new Thickness(8)); chrome.SetValue(Border.CornerRadiusProperty, new CornerRadius(6));
        chrome.SetBinding(Border.BackgroundProperty, new Binding("Background") { RelativeSource = RelativeSource.TemplatedParent });
        var header = new FrameworkElementFactory(typeof(ContentPresenter)); header.SetValue(ContentPresenter.ContentSourceProperty, "Header"); chrome.AppendChild(header);
        tabStyle.Setters.Add(new Setter(TabItem.TemplateProperty, new ControlTemplate(typeof(TabItem)) { VisualTree = chrome }));
        tabStyle.Setters.Add(new Setter(TabItem.BackgroundProperty, new SolidColorBrush(Color.FromRgb(34, 34, 40))));
        tabStyle.Setters.Add(new Setter(TabItem.ForegroundProperty, Brushes.WhiteSmoke));
        tabStyle.Setters.Add(new Setter(TabItem.MarginProperty, new Thickness(0, 0, 4, 4)));
        var selected = new Trigger { Property = TabItem.IsSelectedProperty, Value = true }; selected.Setters.Add(new Setter(TabItem.BackgroundProperty, new SolidColorBrush(Color.FromRgb(94, 46, 34)))); tabStyle.Triggers.Add(selected);
        targets.ItemContainerStyle = tabStyle;
        foreach (var box in new[] { palette, threshold }) { box.Background = new SolidColorBrush(Color.FromRgb(34, 34, 40)); box.Foreground = Brushes.WhiteSmoke; box.Padding = new(8); box.FontSize = 13; }
        Children.Add(new TextBlock { Text = "Driven by a sensor", FontSize = 19, FontWeight = FontWeights.SemiBold, Margin = new(0, 16, 0, 4) });
        Children.Add(targets); Children.Add(zones); Children.Add(sources);
        var presets = new WrapPanel(); Children.Add(presets);
        foreach (var id in new[] { "gpu.temperature", "gpu.power", "gpu.load", "cpu.temperature", "router.ping" })
        {
            var source = SensorSources.All.Single(s => s.Id == id);
            AddButton(presets, source.Name, () => { sources.SelectedValue = id; Defaults(source); });
        }
        Children.Add(styles);
        Children.Add(new TextBlock { Text = "Palette · 2–6 comma-separated hex colours", Margin = new(0, 8, 0, 0) }); Children.Add(palette);
        AddSlider("Range minimum", min); AddSlider("Range maximum", max);
        Children.Add(auto); Children.Add(reading); Children.Add(range); range.Children.Add(marker); range.SizeChanged += (_, _) => Refresh();
        AddSlider("Smoothing (seconds)", smoothing);
        Children.Add(alert); Children.Add(threshold);
        Children.Add(new TextBlock { Text = "Threshold style switches at range midpoint; returns below midpoint - 2 units.", TextWrapping = TextWrapping.Wrap, Foreground = Brushes.Gray, Margin = new(0, 4, 0, 8) });
        var actions = new WrapPanel(); Children.Add(actions);
        AddButton(actions, "Apply sensor", () => _ = ApplyAsync());
        AddButton(actions, "Use normal effect", () => _ = RemoveAsync());
        Children.Add(status);
        targets.SelectionChanged += (_, e) => { if (e.Source == targets && !suppress) LoadZones(); };
        zones.SelectionChanged += (_, _) => { if (!suppress) LoadBinding(); };
        sources.SelectionChanged += (_, _) => { if (!suppress && sources.SelectedItem is SensorSource s) Defaults(s); };
        DataContextChanged += (_, _) => { if (model is not null) model.PropertyChanged -= Changed; model = DataContext as LightingViewModel; if (model is not null) model.PropertyChanged += Changed; Refresh(); };
        Loaded += (_, _) => { if (model is not null) { model.PropertyChanged -= Changed; model.PropertyChanged += Changed; } Refresh(); };
        Unloaded += (_, _) => { if (model is not null) model.PropertyChanged -= Changed; };
    }
    private static ThickSlider Slider(double low, double high) => new() { Minimum = low, Maximum = high, Height = 18, Margin = new(0, 8, 0, 8) };
    private void AddSlider(string name, ThickSlider slider)
    {
        var label = new TextBlock(); Children.Add(label); Children.Add(slider);
        void Update() => label.Text = $"{name}   {slider.Value:0.##}";
        slider.ValueChanged += (_, _) => Update(); Update();
    }
    private static void AddButton(Panel panel, string text, Action action)
    {
        var button = new Button { Content = text, Padding = new(8, 6, 8, 6), Margin = new(0, 4, 6, 8) }; panel.Children.Add(button); button.Click += (_, _) => action();
    }
    private void Changed(object? sender, PropertyChangedEventArgs e) => Refresh();
    private void Refresh()
    {
        if (model?.State is not { } state) return;
        var next = string.Join(";", state.Lighting.Devices.Select(d => $"{d.Id}:{d.Name}:{d.Zones?.Count}").Concat((state.Lamps ?? []).Select(l => $"{l.Id}:{l.Name}")));
        if (signature != next)
        {
            signature = next; var selected = (targets.SelectedItem as TabItem)?.Tag as string;
            suppress = true; targets.Items.Clear();
            foreach (var d in state.Lighting.Devices) targets.Items.Add(new TabItem { Header = d.Name, Tag = d.Id });
            foreach (var lamp in state.Lamps ?? []) targets.Items.Add(new TabItem { Header = lamp.Name ?? "Lamp", Tag = lamp.Id });
            targets.SelectedItem = targets.Items.Cast<TabItem>().FirstOrDefault(t => (string)t.Tag == selected) ?? targets.Items.Cast<TabItem>().FirstOrDefault();
            var view = new ListCollectionView(SensorSources.Available(state).ToList()); view.GroupDescriptions.Add(new PropertyGroupDescription("Group"));
            sources.ItemsSource = view;
            sources.GroupStyle.Clear(); sources.GroupStyle.Add(new GroupStyle { HeaderTemplate = GroupHeader() });
            suppress = false; LoadZones();
        }
        if (Target is not { } target || state.Lighting.Bindings?.GetValueOrDefault(target) is not { } b) { reading.Text = "Normal effect · choose a source and Apply sensor"; return; }
        var sample = state.Lighting.BindingReadings?.GetValueOrDefault(target) ?? session.Observe(target, b, SensorSources.Read(state, b.Source), clock.Elapsed.TotalSeconds);
        var source = SensorSources.Available(state).FirstOrDefault(s => s.Id == b.Source);
        reading.Text = sample.Reason ?? $"{sample.Value:0.##} {source?.Unit}   ·   {sample.Min:0.##}–{sample.Max:0.##} {source?.Unit}" + (sample.Alert ? " · Alert active" : "");
        range.Background = new LinearGradientBrush(new GradientStopCollection((b.Palette ?? SensorLighting.DefaultPalette).Select(p => new GradientStop((Color)ColorConverter.ConvertFromString(p.Color), p.Position))), 0);
        marker.Margin = new(Math.Max(0, range.ActualWidth - 3) * sample.Normalized, 0, 0, 0);
    }
    private static DataTemplate MemberTemplate(string path)
    {
        var text = new FrameworkElementFactory(typeof(TextBlock)); text.SetBinding(TextBlock.TextProperty, new Binding(path)); text.SetValue(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis);
        return new DataTemplate { VisualTree = text };
    }
    private static DataTemplate GroupHeader()
    {
        var template = new DataTemplate(); var text = new FrameworkElementFactory(typeof(TextBlock)); text.SetBinding(TextBlock.TextProperty, new Binding("Name")); text.SetValue(TextBlock.FontWeightProperty, FontWeights.Bold); template.VisualTree = text; return template;
    }
    private void LoadZones()
    {
        if (model?.State is not { } state || (targets.SelectedItem as TabItem)?.Tag is not string id) return;
        var device = state.Lighting.Devices.FirstOrDefault(d => d.Id == id);
        var items = new List<KeyValuePair<string, string>> { new(id, device is null ? "Whole lamp" : "Whole device") };
        for (int z = 0; z < (device?.Zones?.Count ?? 0); z++) items.Add(new(SensorSources.ZoneTarget(id, z), device!.Zones![z].Name));
        suppress = true; zones.ItemsSource = items; zones.SelectedIndex = 0; suppress = false; LoadBinding();
    }
    private void Defaults(SensorSource s)
    {
        min.Minimum = max.Minimum = Math.Min(-100, s.Min); min.Maximum = max.Maximum = Math.Max(s.Max * 2, 100);
        min.Value = s.Min; max.Value = s.Max;
        if (styles.SelectedIndex < 0) styles.SelectedIndex = 0;
    }
    private void LoadBinding()
    {
        if (Target is not { } target) return;
        var b = model?.State?.Lighting.Bindings?.GetValueOrDefault(target) ?? new SensorBinding("gpu.temperature", 35, 85);
        suppress = true; sources.SelectedValue = b.Source;
        Defaults(SensorSources.All.FirstOrDefault(s => s.Id == b.Source) ?? new(b.Source, "Fans", b.Source, "RPM", 0, 3000));
        min.Minimum = max.Minimum = Math.Min(min.Minimum, b.Min); min.Maximum = max.Maximum = Math.Max(max.Maximum, b.Max);
        min.Value = b.Min; max.Value = b.Max; smoothing.Value = b.Smoothing; styles.SelectedIndex = (int)b.Style;
        palette.Text = string.Join(", ", (b.Palette ?? SensorLighting.DefaultPalette).Select(p => p.Color));
        auto.IsChecked = b.AutoLearn; alert.IsChecked = b.Alert is not null; threshold.Text = (b.Alert?.Threshold ?? 95).ToString(CultureInfo.InvariantCulture);
        suppress = false; Refresh();
    }
    private async Task ApplyAsync()
    {
        if (model is null || Target is not { } target || sources.SelectedValue is not string source) return;
        try
        {
            var colors = palette.Text.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
            var stops = colors.Select((c, i) => new PaletteStop(i / (double)Math.Max(1, colors.Length - 1), c)).ToArray();
            var b = new SensorBinding(source, min.Value, max.Value, (SensorStyle)styles.SelectedIndex, stops, smoothing.Value,
                alert.IsChecked == true ? new(double.Parse(threshold.Text, CultureInfo.InvariantCulture)) : null, auto.IsChecked == true, Math.Min(2, Math.Max(0, max.Value - min.Value)));
            SensorSources.Validate(b);
            var response = await model.Client.SendAsync(Commands.SetSensorBinding, new { target, b.Source, b.Min, b.Max, b.Style, b.Palette, b.Smoothing, b.Alert, b.AutoLearn, b.Hysteresis });
            status.Text = response.Ok ? "Sensor binding saved" : response.Error;
        }
        catch (Exception e) when (e is ArgumentException or FormatException or OverflowException or InvalidOperationException) { status.Text = e.Message; }
    }
    private async Task RemoveAsync()
    {
        if (model is null || Target is not { } target) return;
        var response = await model.Client.SendAsync(Commands.SetSensorBinding, new { target, source = (string?)null });
        status.Text = response.Ok ? "Using normal effect" : response.Error;
    }
}
