using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Shapes;
using ControlSpawn.App.Common;
using ControlSpawn.App.Controls;
using ControlSpawn.App.ViewModels;

namespace ControlSpawn.App.Views;

public sealed class LedEditor : UserControl
{
    public bool SharedCanvas { get; set; }
    private readonly WrapPanel tools = new();
    public FrameworkElement DetachToolbar() { root.Children.Remove(tools); return tools; }
    private readonly StackPanel root = new(), layout = new();
    private readonly WrapPanel tabs = new(), swatches = new();
    private readonly TextBlock status = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 12, 0, 0) };
    private readonly TextBox color = new() { Text = "#FF4D1A", Width = 115 }, second = new() { Text = "#5AA7FF", Width = 115 };
    private readonly Dictionary<int, Ellipse> dots = new();
    private readonly ComboBox mode;
    private LedEditorViewModel? model;
    private int anchor = -1;
    private int? selectedZone;
    public LedEditor()
    {
        Content = root;
        root.Children.Add(new TextBlock { Text = "LED tools", FontSize = 17, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 12) });
        root.Children.Add(tabs);
        mode = new ComboBox { ItemsSource = new[] { "Exact colours (Custom)", "Paint over effect (Overlay)" }, SelectedIndex = 0, Margin = new Thickness(0, 14, 0, 10) };
        mode.SelectionChanged += (_, _) => { if (model is not null) model.ActivateLayer(mode.SelectedIndex == 0 ? "custom" : "overlay"); };
        root.Children.Add(mode);
        root.Children.Add(tools);
        foreach (var (icon, toolName) in new[] { ("\u270E", "Brush"), ("\u25A7", "Fill zone"), ("\u25E9", "Gradient"), ("\u233E", "Eyedropper"), ("\u232B", "Eraser"), ("\u2194", "Select range") })
        {
            AddButton(tools, icon, () => { if (model is not null) model.Tool = toolName; });
            ((Button)tools.Children[tools.Children.Count - 1]).ToolTip = toolName;
        }
        AddButton(tools, "\u25A3", () => { SyncColor(); model?.Paint(Enumerable.Range(0, model.Device?.LedCount ?? 0)); });
        AddButton(tools, "\u2713", () => { SyncColor(); if (model is not null) model.Paint(Range(model.RangeStart, model.RangeEnd)); });
        AddButton(tools, "\u00D7", () => { if (model is not null) _ = model.ClearAsync(); });
        ((Button)tools.Children[6]).ToolTip = "Fill device";
        ((Button)tools.Children[7]).ToolTip = "Apply to selected range";
        ((Button)tools.Children[8]).ToolTip = "Clear layers";
        var picker = new WrapPanel { Margin = new Thickness(0, 6, 0, 12) }; root.Children.Add(picker);
        var wheel = new ColorWheel { Width = 110, Height = 110, MinWidth = 0, MinHeight = 0, Margin = new Thickness(0, 0, 18, 0) };
        wheel.ColorPicked += hex => { color.Text = hex; SyncColor(); };
        picker.Children.Add(wheel);
        var colors = new StackPanel(); picker.Children.Add(colors);
        colors.Children.Add(new TextBlock { Text = "Brush / gradient start", Margin = new Thickness(0, 0, 0, 5) }); colors.Children.Add(color);
        colors.Children.Add(new TextBlock { Text = "Gradient end", Margin = new Thickness(0, 8, 0, 5) }); colors.Children.Add(second);
        StyleInput(color); StyleInput(second);
        color.TextChanged += (_, _) => SyncColor(); second.TextChanged += (_, _) => SyncColor();
        var brightness = new StackPanel { Width = 150, Margin = new Thickness(12, 0, 0, 0) }; picker.Children.Add(brightness);
        brightness.Children.Add(new TextBlock { Text = "Brightness", Margin = new Thickness(0, 0, 0, 12) });
        var slider = new ThickSlider { Minimum = 0, Maximum = 100, Value = 100, Height = 18 };
        slider.ValueChanged += (_, _) => { if (model is not null) model.Brightness = slider.Value; };
        brightness.Children.Add(slider); brightness.Children.Add(swatches);
        root.Children.Add(layout);
        var presets = new WrapPanel { Margin = new Thickness(0, 14, 0, 0) };
        presets.Children.Add(new TextBlock { Text = "Layout name", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 8) });
        var name = new TextBox { Width = 180, Margin = new Thickness(0, 0, 8, 8) }; presets.Children.Add(name);
        StyleInput(name);
        AddButton(presets, "Save layout", () => model?.SavePreset(name.Text));
        AddButton(presets, "Load layout", () => model?.LoadPreset(name.Text));
        root.Children.Add(presets); root.Children.Add(status);
        DataContextChanged += (_, _) =>
        {
            if (model is not null) model.PropertyChanged -= Changed;
            model = DataContext as LedEditorViewModel;
            if (model is not null) model.PropertyChanged += Changed;
            Build();
        };
        Unloaded += (_, _) => { if (model is not null) model.PropertyChanged -= Changed; };
        Loaded += (_, _) => { if (model is not null) { model.PropertyChanged -= Changed; model.PropertyChanged += Changed; Build(); } };
    }
    private void Changed(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName == nameof(LedEditorViewModel.Color) && model is not null && color.Text != model.Color) color.Text = model.Color;
        if (args.PropertyName == nameof(LedEditorViewModel.Layer) && model is not null)
            mode.SelectedIndex = model.Layer == "custom" ? 0 : 1;
        if (args.PropertyName == nameof(LedEditorViewModel.Device))
        {
            if (selectedZone >= (model?.Device?.Zones?.Count ?? 0)) selectedZone = null;
            Build();
        }
        else { UpdateDots(); if (model is not null) status.Text = model.Status; }
    }
    private void SyncColor() { if (model is not null) { model.Color = color.Text; model.SecondColor = second.Text; } }
    private static void StyleInput(TextBox input)
    {
        input.Background = new SolidColorBrush(Color.FromRgb(37, 37, 41));
        input.BorderBrush = new SolidColorBrush(Color.FromRgb(63, 63, 70));
        input.BorderThickness = new Thickness(1); input.Padding = new Thickness(10, 7, 10, 7);
    }
    private static IEnumerable<int> Range(int a, int b) => Enumerable.Range(Math.Min(a, b), Math.Abs(b - a) + 1);
    private static void AddButton(Panel panel, string title, Action action)
    {
        var button = new Button { Content = title, Margin = new Thickness(0, 0, 8, 8), Padding = new Thickness(12, 7, 12, 7) };
        button.Click += (_, _) => action(); panel.Children.Add(button);
    }
    private void Build()
    {
        tabs.Children.Clear(); layout.Children.Clear(); dots.Clear(); anchor = -1;
        if (model is null) return;
        mode.SelectedIndex = model.Layer == "custom" ? 0 : 1;
        foreach (var device in model.Devices)
        {
            var d = device;
            AddButton(tabs, d.Name, () => { selectedZone = null; model.Device = d; Build(); });
            var button = (Button)tabs.Children[tabs.Children.Count - 1];
            if (model.Device?.Id == d.Id && selectedZone is null) button.BorderBrush = new SolidColorBrush(ColorUtil.Accent);
            button.Content = new DeviceNameEditor { DeviceId = d.Id, DisplayName = d.Name, Client = model.Client };
            if (d.Zones is not null)
                for (int z = 0; z < d.Zones.Count; z++)
                {
                    int index = z;
                    if (d.Zones[z].MaxLeds > d.Zones[z].MinLeds)
                        AddButton(tabs, d.Zones[z].Name, () => { selectedZone = index; model.Device = d; Build(); });
                }
        }
        if (model.Device is not { } selected) { layout.Children.Add(new TextBlock { Text = "Connect an RGB device to edit its LEDs." }); return; }
        var zones = selected.Zones ?? [new("LEDs", "Linear", 0, selected.LedCount, selected.LedCount, selected.LedCount)];
        for (int z = 0; z < zones.Count; z++)
        {
            if (selectedZone is not null && z != selectedZone) continue;
            var zone = zones[z]; int zoneIndex = z;
            layout.Children.Add(new TextBlock { Text = $"{zone.Name} · {zone.LedCount} LEDs", FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 10, 0, 8) });
            if (zone.MaxLeds > zone.MinLeds)
            {
                var sizing = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 10) };
                var size = new TextBox { Text = zone.LedCount.ToString(), Width = 65, Margin = new Thickness(12, 0, 8, 8) };
                StyleInput(size);
                var slider = new ThickSlider { Minimum = zone.MinLeds, Maximum = zone.MaxLeds, Value = zone.LedCount, Width = 140, Height = 18 };
                slider.ValueChanged += (_, _) => size.Text = Math.Round(slider.Value).ToString();
                sizing.Children.Add(slider); sizing.Children.Add(size);
                AddButton(sizing, "Resize zone", () => { if (int.TryParse(size.Text, out int n)) _ = model.ResizeAsync(zoneIndex, n); }); layout.Children.Add(sizing);
            }
            var canvas = new Canvas { Height = selected.Kind == "RAM" ? Math.Max(60, zone.LedCount * 32) : zone.MatrixHeight > 0 ? zone.MatrixHeight * 32 + 12 : Math.Max(50, Math.Ceiling(zone.LedCount / 22d) * 38), Background = new SolidColorBrush(Color.FromRgb(23, 23, 27)) };
            if (!SharedCanvas) layout.Children.Add(canvas);
            var indexes = zone.Matrix ?? Enumerable.Range(zone.FirstLed, zone.LedCount).ToArray();
            for (int cell = 0; cell < indexes.Count; cell++)
            {
                int index = indexes[cell]; if (index < 0 || index >= selected.LedCount) continue;
                int col = zone.MatrixWidth > 0 ? cell % zone.MatrixWidth : selected.Kind == "RAM" ? 0 : cell % 22;
                int row = zone.MatrixWidth > 0 ? cell / zone.MatrixWidth : selected.Kind == "RAM" ? cell : cell / 22;
                var dot = new Ellipse { Width = selected.Kind == "RAM" ? 48 : 22, Height = 22, Stroke = Brushes.DimGray, StrokeThickness = 1,
                    Cursor = Cursors.Cross, ToolTip = $"LED {index + 1} · {zone.LedNames?.ElementAtOrDefault(index - zone.FirstLed) ?? zone.Name}" };
                Canvas.SetLeft(dot, 14 + col * 38); Canvas.SetTop(dot, 6 + row * 32); canvas.Children.Add(dot); dots[index] = dot;
                dot.MouseLeftButtonDown += (_, e) =>
                {
                    SyncColor();
                    if (model.Tool == "Eyedropper") { color.Text = model.Colors[index][..7]; return; }
                    if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) || model.Tool == "Select range")
                    {
                        if (anchor < 0) anchor = index;
                        model.RangeStart = anchor; model.RangeEnd = index; UpdateDots(); return;
                    }
                    anchor = index;
                    if (model.Tool == "Fill zone") model.Paint(Enumerable.Range(zone.FirstLed, zone.LedCount));
                    else if (model.Tool != "Gradient") model.Paint([index], model.Tool == "Eraser");
                    e.Handled = true;
                };
                dot.MouseEnter += (_, _) => { if (Mouse.LeftButton == MouseButtonState.Pressed && model.Tool is "Brush" or "Eraser") { SyncColor(); model.Paint([index], model.Tool == "Eraser"); } };
                dot.MouseLeftButtonUp += (_, _) =>
                {
                    if (model.Tool != "Gradient" || anchor < 0) return;
                    SyncColor(); var indices = Range(anchor, index);
                    model.Paint(anchor > index ? indices.Reverse() : indices, gradient: true);
                };
            }
        }
        UpdateDots(); status.Text = model.Status;
    }
    private void UpdateDots()
    {
        if (model is null) return;
        var colors = model.Colors;
        foreach (var (index, dot) in dots)
        {
            if (index >= colors.Length) continue;
            var c = (Color)ColorConverter.ConvertFromString(colors[index][..7]);
            dot.Fill = new SolidColorBrush(c);
            dot.Opacity = colors[index].Length == 9 ? Math.Max(0.15, byte.Parse(colors[index].AsSpan(7, 2), System.Globalization.NumberStyles.HexNumber) / 255d) : 1;
            dot.Effect = new DropShadowEffect { Color = c, BlurRadius = 14, ShadowDepth = 0, Opacity = 0.8 };
            dot.Stroke = model.Tool == "Select range" && index >= Math.Min(model.RangeStart, model.RangeEnd) && index <= Math.Max(model.RangeStart, model.RangeEnd) ? Brushes.White : Brushes.DimGray;
            if (!Motion.ReduceAnimations)
                dot.BeginAnimation(OpacityProperty, new System.Windows.Media.Animation.DoubleAnimation(dot.Opacity * 0.7, dot.Opacity, TimeSpan.FromMilliseconds(300)));
        }
        swatches.Children.Clear();
        foreach (var hex in model.Recent)
        {
            var button = new Button { Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex)), Width = 22, Height = 22, Margin = new Thickness(0, 12, 4, 0), ToolTip = hex };
            button.Click += (_, _) => color.Text = hex; swatches.Children.Add(button);
        }
    }
}

