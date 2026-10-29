using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Prism.App.Controls;
using Prism.App.Common;
using Prism.App.ViewModels;
using Prism.Core;
using Prism.Core.Ipc;
using Prism.Lighting;

namespace Prism.App.Views;

public sealed class PaletteStrip : FrameworkElement
{
    public int Selected { get; private set; }
    public event Action? SelectionChanged;
    private LightingViewModel? Model => DataContext as LightingViewModel;
    public IReadOnlyList<PaletteStop> Stops => localStops ?? Model?.Parameters.Stops ?? Palettes.BuiltIn[Model?.Parameters.Palette ?? "Aurora"];
    public PaletteStrip() { Height = 52; Cursor = Cursors.Hand; }
    protected override void OnRender(DrawingContext dc)
    {
        var brush = new LinearGradientBrush { StartPoint = new(0, 0), EndPoint = new(1, 0) };
        foreach (var stop in Stops) brush.GradientStops.Add(new((Color)ColorConverter.ConvertFromString(stop.Color), stop.Position));
        dc.DrawRoundedRectangle(brush, null, new Rect(8, 8, Math.Max(1, ActualWidth - 16), 22), 7, 7);
        for (int i = 0; i < Stops.Count; i++)
        {
            double x = 8 + Stops[i].Position * Math.Max(1, ActualWidth - 16);
            dc.DrawEllipse(new SolidColorBrush((Color)ColorConverter.ConvertFromString(Stops[i].Color)), new Pen(i == Selected ? Brushes.White : Brushes.Gray, 2), new(x, 34), 6, 6);
        }
    }
    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        double x = e.GetPosition(this).X;
        Selected = Enumerable.Range(0, Stops.Count).MinBy(i => Math.Abs(8 + Stops[i].Position * Math.Max(1, ActualWidth - 16) - x));
        SelectionChanged?.Invoke(); CaptureMouse(); InvalidateVisual();
    }
    protected override void OnMouseMove(MouseEventArgs e)
    {
        if (!IsMouseCaptured || Model is not { } model) return;
        var stops = Stops.ToArray();
        double min = Selected == 0 ? 0 : stops[Selected - 1].Position + 0.005;
        double max = Selected == stops.Length - 1 ? 1 : stops[Selected + 1].Position - 0.005;
        if (min > max) return;
        stops[Selected] = stops[Selected] with { Position = Math.Clamp((e.GetPosition(this).X - 8) / Math.Max(1, ActualWidth - 16), min, max) };
        // Local preview while dragging; one persisted command on release.
        pending = model.Parameters with { Stops = stops };
        localStops = stops; InvalidateVisual();
    }
    private EffectParameters? pending;
    private IReadOnlyList<PaletteStop>? localStops;
    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        ReleaseMouseCapture(); if (pending is { } p && Model is { } model) _ = model.SetParametersAsync(p);
        pending = null; localStops = null; InvalidateVisual();
    }
    public async Task ChangeColorAsync(string hex)
    {
        if (Model is not { } model || !LedLayers.Valid(hex, false)) return;
        var stops = Stops.ToArray(); Selected = Math.Min(Selected, stops.Length - 1);
        stops[Selected] = stops[Selected] with { Color = hex };
        await model.SetParametersAsync(model.Parameters with { Stops = stops }); InvalidateVisual();
    }
    public async Task AddAsync()
    {
        if (Model is not { } model || Stops.Count >= 6) return;
        var gap = Enumerable.Range(0, Stops.Count - 1).MaxBy(i => Stops[i + 1].Position - Stops[i].Position);
        double position = (Stops[gap].Position + Stops[gap + 1].Position) / 2;
        var color = Palettes.Sample(Stops, position);
        var next = Stops.Append(new PaletteStop(position, $"#{color.R:X2}{color.G:X2}{color.B:X2}")).OrderBy(s => s.Position).ToArray();
        await model.SetParametersAsync(model.Parameters with { Stops = next }); InvalidateVisual();
    }
    public async Task RemoveAsync()
    {
        if (Model is not { } model || Stops.Count <= 2) return;
        var stops = Stops.Where((_, i) => i != Selected).ToArray(); Selected = 0;
        await model.SetParametersAsync(model.Parameters with { Stops = stops }); InvalidateVisual();
    }
}

public sealed class LightingWorkspace : UserControl
{
    private readonly Grid root = new();
    private readonly StackPanel canvasPanel = new(), inspector = new();
    private readonly CaseMapPreview preview = new() { Height = 500, LedEditing = true };
    private readonly LedEditor editor = new() { SharedCanvas = true };
    private readonly PaletteStrip palette = new();
    private readonly WrapPanel gallery = new(), devices = new();
    private readonly TextBlock status = new() { TextWrapping = TextWrapping.Wrap, Margin = new(0, 12, 0, 0) };
    private readonly ComboBox palettes = new() { ItemsSource = Palettes.BuiltIn.Keys, Margin = new(0, 4, 0, 4) };
    private readonly TextBox hex = new() { Text = "#FF4D1A", Width = 110, Background = new SolidColorBrush(Color.FromRgb(37, 37, 41)), Foreground = Brushes.WhiteSmoke, BorderBrush = Brushes.DimGray, BorderThickness = new(1), Padding = new(8) };
    private readonly ThickSlider angle = Slider(0, 360), scale = Slider(0.1, 8), intensity = Slider(0, 1), tail = Slider(0.02, 1), count = Slider(1, 8), speed = Slider(0, 100), brightness = Slider(0, 100), rotation = Slider(0, 360);
    private readonly DirectionDial dial = new();
    private readonly CheckBox cpuBeat = new() { Foreground = Brushes.WhiteSmoke, Content = "Heartbeat follows CPU load", Margin = new(0, 8, 0, 8) };
    private LightingViewModel? model;
    private bool suppress;
    private string deviceSignature = "";
    public LightingWorkspace()
    {
        Content = root;
        root.ColumnDefinitions.Add(new()); root.ColumnDefinitions.Add(new() { Width = new GridLength(370) });
        root.RowDefinitions.Add(new() { Height = GridLength.Auto }); root.RowDefinitions.Add(new() { Height = GridLength.Auto });
        root.Children.Add(canvasPanel); root.Children.Add(inspector); Grid.SetColumn(inspector, 1);
        canvasPanel.Margin = new(0, 0, 20, 0);
        canvasPanel.Children.Add(new CaseLightingSetup());
        var wholeCase = new Button { Content = "Sync whole case and both lamps", Margin = new Thickness(0, 8, 0, 12), HorizontalAlignment = HorizontalAlignment.Left };
        wholeCase.Click += async (_, _) => { if (model is not null) await model.SyncAllAsync(); };
        canvasPanel.Children.Add(wholeCase);
        Header(canvasPanel, "Your case, in colour", 25);
        canvasPanel.Children.Add(new TextBlock { Text = "One spatial canvas for effects, devices and every LED.", Foreground = Brushes.Gray, Margin = new(0, 4, 0, 16) });
        var mode = new WrapPanel(); canvasPanel.Children.Add(mode);
        var edit = new CheckBox { Foreground = Brushes.WhiteSmoke, Content = "Edit LEDs", IsChecked = true, Margin = new(0, 0, 18, 12) };
        var map = new CheckBox { Foreground = Brushes.WhiteSmoke, Content = "Arrange map", Margin = new(0, 0, 18, 12) };
        mode.Children.Add(edit); mode.Children.Add(map);
        edit.Click += (_, _) => { preview.LedEditing = edit.IsChecked == true; editor.Visibility = preview.LedEditing ? Visibility.Visible : Visibility.Collapsed; if (preview.LedEditing) { map.IsChecked = false; preview.MapEditing = false; } preview.InvalidateVisual(); };
        map.Click += (_, _) => { preview.MapEditing = map.IsChecked == true; if (preview.MapEditing) { edit.IsChecked = false; preview.LedEditing = false; editor.Visibility = Visibility.Collapsed; } preview.InvalidateVisual(); };
        var stage = new Grid(); stage.Children.Add(preview);
        stage.Children.Add(new Border { Child = editor.DetachToolbar(), Background = new SolidColorBrush(Color.FromArgb(235, 32, 32, 38)), BorderBrush = Brushes.DimGray,
            BorderThickness = new(1), CornerRadius = new(14), Padding = new(10, 8, 2, 0), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Bottom, Margin = new(12, 0, 12, 34) });
        canvasPanel.Children.Add(stage);
        var transform = new WrapPanel { Margin = new(0, 10, 0, 10) }; canvasPanel.Children.Add(transform);
        transform.Children.Add(new TextBlock { Text = "Selected device rotation", VerticalAlignment = VerticalAlignment.Center });
        rotation.Width = 150; rotation.Margin = new(14, 0, 10, 0); transform.Children.Add(rotation);
        rotation.ValueChanged += (_, _) => { if (!suppress && preview.SelectedPlacement is { } selected) _ = preview.TransformSelectionAsync(rotation.Value, selected.Flip); };
        Button(transform, "Flip", () => { if (preview.SelectedPlacement is { } p) _ = preview.TransformSelectionAsync(p.Rotation, !p.Flip); });
        Button(transform, "Reset map", () => { if (model is not null) _ = model.SetMapAsync(CaseMap.Default(model.State?.Lighting.Devices ?? CaseMapPreview.DemoDevices)); });
        preview.SelectionChanged += () => { suppress = true; rotation.Value = preview.SelectedPlacement?.Rotation ?? 0; suppress = false; };
        canvasPanel.Children.Add(new SensorBindingPanel());
        canvasPanel.Children.Add(editor);
        Header(inspector, "Effect gallery", 20);
        inspector.Children.Add(new ScrollViewer { Content = gallery, MaxHeight = 380, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Margin = new(0, 12, 0, 12) });
        Header(inspector, "Palette", 17); inspector.Children.Add(palettes); inspector.Children.Add(palette);
        var stopTools = new WrapPanel(); inspector.Children.Add(stopTools); stopTools.Children.Add(hex);
        Button(stopTools, "Set stop", () => _ = palette.ChangeColorAsync(hex.Text));
        Button(stopTools, "+", () => _ = palette.AddAsync()); Button(stopTools, "-", () => _ = palette.RemoveAsync());
        palette.SelectionChanged += () => hex.Text = palette.Stops[Math.Min(palette.Selected, palette.Stops.Count - 1)].Color;
        palettes.SelectionChanged += (_, _) => { if (!suppress && model is not null && palettes.SelectedItem is string value) _ = model.SetParametersAsync(model.Parameters with { Palette = value, Stops = null }); };
        AddSlider(inspector, "Brightness", brightness, () => { if (model is not null) model.BrightnessPercent = brightness.Value; });
        AddSlider(inspector, "Speed", speed, () => { if (model is not null) model.SpeedPercent = speed.Value; });
        AddSlider(inspector, "Direction / angle", angle, () => { if (model is not null) _ = model.SetParametersAsync(model.Parameters with { Angle = angle.Value }); });
        inspector.Children.Add(dial);
        dial.AngleChanged += value => { if (!suppress) angle.Value = value; };
        AddSlider(inspector, "Scale", scale, () => { if (model is not null) _ = model.SetParametersAsync(model.Parameters with { Scale = scale.Value }); });
        AddSlider(inspector, "Intensity", intensity, () => { if (model is not null) _ = model.SetParametersAsync(model.Parameters with { Intensity = intensity.Value }); });
        AddSlider(inspector, "Comet count", count, () => { if (model is not null) _ = model.SetParametersAsync(model.Parameters with { CometCount = (int)Math.Round(count.Value) }); });
        AddSlider(inspector, "Comet tail", tail, () => { if (model is not null) _ = model.SetParametersAsync(model.Parameters with { TailLength = tail.Value }); });
        inspector.Children.Add(cpuBeat);
        cpuBeat.Click += (_, _) => { if (model is not null) _ = model.SetParametersAsync(model.Parameters with { HeartbeatCpu = cpuBeat.IsChecked == true }); };
        var baseColors = new WrapPanel(); inspector.Children.Add(baseColors);
        foreach (var swatch in new[] { "#FF4D1A", "#5AA7FF", "#3DDC84", "#B26BFF", "#F4F4F5" })
        {
            var b = new Button { Width = 30, Height = 30, Margin = new(0, 0, 8, 8), Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString(swatch)), ToolTip = "Base colour " + swatch };
            b.Click += (_, _) => model?.SelectColorCommand.Execute(swatch); baseColors.Children.Add(b);
        }
        Header(inspector, "Devices", 17); inspector.Children.Add(devices);
        Header(inspector, "Lamp follow", 17);
        var follow = new WrapPanel(); inspector.Children.Add(follow);
        foreach (var (label, key) in new[] { ("Map point", "pc"), ("Average", "average"), ("Own colour", "own") }) Button(follow, label, () => model?.SetFollowCommand.Execute(key));
        inspector.Children.Add(new TextBlock { Text = "Move the LAMP marker in Arrange map. By default it follows the strip centre.", Foreground = Brushes.Gray, TextWrapping = TextWrapping.Wrap, Margin = new(0, 4, 0, 12) });
        inspector.Children.Add(new EventOptionsPanel());
        Header(inspector, "Save lighting to scene", 17);
        var scene = new ComboBox { ItemsSource = new[] { "Gaming", "Work", "Night", "Movie" }, SelectedIndex = 0, Margin = new(0, 8, 0, 8) };
        inspector.Children.Add(scene);
        var save = new WrapPanel(); inspector.Children.Add(save);
        Button(save, "Save effect + parameters", () => { if (model is not null) { model.SceneName = scene.SelectedItem?.ToString() ?? "Gaming"; model.SaveToSceneCommand.Execute(null); } });
        inspector.Children.Add(status);
        DataContextChanged += (_, _) => { if (model is not null) model.PropertyChanged -= Changed; model = DataContext as LightingViewModel; if (model is not null) model.PropertyChanged += Changed; BuildGallery(); Refresh(); };
        Loaded += (_, _) => { if (model is not null) { model.PropertyChanged -= Changed; model.PropertyChanged += Changed; } Refresh(); };
        Unloaded += (_, _) => { if (model is not null) model.PropertyChanged -= Changed; };
        SizeChanged += (_, _) => Responsive();
    }
    private void Responsive()
    {
        bool narrow = ActualWidth < 850;
        root.ColumnDefinitions[1].Width = narrow ? new(0) : new(370);
        Grid.SetColumn(inspector, narrow ? 0 : 1); Grid.SetRow(inspector, narrow ? 1 : 0);
        canvasPanel.Margin = narrow ? new(0, 0, 0, 24) : new(0, 0, 20, 0);
        preview.Height = narrow ? 420 : 500;
    }
    private static void Header(Panel panel, string text, double size) => panel.Children.Add(new TextBlock { Text = text, FontSize = size, FontWeight = FontWeights.SemiBold, Margin = new(0, 12, 0, 4) });
    private static void Button(Panel panel, string text, Action action)
    {
        var button = new Button { Content = text, Padding = new(10, 7, 10, 7), Margin = new(4, 0, 0, 8), ToolTip = text };
        button.Click += (_, _) => action(); panel.Children.Add(button);
    }
    private static ThickSlider Slider(double minimum, double maximum) => new() { Minimum = minimum, Maximum = maximum, Height = 18, Margin = new(0, 8, 0, 10) };
    private void AddSlider(Panel panel, string text, ThickSlider slider, Action changed)
    {
        var label = new TextBlock { Text = text, Margin = new(0, 10, 0, 0) }; panel.Children.Add(label); panel.Children.Add(slider);
        slider.ValueChanged += (_, _) => { label.Text = $"{text}   {slider.Value:0.##}"; if (!suppress) changed(); };
    }
    private void BuildGallery()
    {
        gallery.Children.Clear();
        if (model is null) return;
        foreach (var effect in model.Effects)
        {
            var content = new StackPanel();
            content.Children.Add(new CaseMapPreview(effect.Effect) { Height = 100, Width = 146 });
            content.Children.Add(new TextBlock { Text = effect.Name, FontWeight = FontWeights.SemiBold, Margin = new(4, 8, 0, 2), TextWrapping = TextWrapping.Wrap });
            var button = new Button { Content = content, Width = 166, Padding = new(6), Margin = new(0, 0, 8, 8), ToolTip = effect.Subtitle, Tag = effect };
            button.Click += (_, _) => model?.SelectEffectCommand.Execute(effect);
            gallery.Children.Add(button);
        }
    }
    private void Changed(object? sender, PropertyChangedEventArgs args) => Refresh();
    private void Refresh()
    {
        if (model is null) return;
        suppress = true;
        editor.DataContext = model.Editor;
        var p = model.Parameters;
        palettes.SelectedItem = p.Palette; angle.Value = p.Angle; dial.Angle = p.Angle; scale.Value = p.Scale; intensity.Value = p.Intensity; tail.Value = p.TailLength; count.Value = p.CometCount;
        brightness.Value = model.BrightnessPercent; speed.Value = model.SpeedPercent; cpuBeat.IsChecked = p.HeartbeatCpu;
        palette.InvalidateVisual(); preview.InvalidateVisual();
        status.Text = model.LastError ?? model.SavedText;
        foreach (Button b in gallery.Children) b.BorderBrush = ((EffectOption)b.Tag).Effect == model.Effect ? new SolidColorBrush(ColorUtil.Accent) : new SolidColorBrush(Color.FromRgb(63, 63, 70));
        var signature = string.Join(";", model.Devices.Select(d => $"{d.Id}:{d.Name}:{d.Synced}:{p.Devices?.GetValueOrDefault(d.Id, true) != false}"));
        if (signature != deviceSignature)
        {
            deviceSignature = signature; devices.Children.Clear();
            foreach (var d in model.Devices)
            {
                var toggle = new CheckBox { Foreground = Brushes.WhiteSmoke, ToolTip = d.Name, IsChecked = d.Synced && p.Devices?.GetValueOrDefault(d.Id, true) != false, Margin = new(0, 8, 8, 8) };
                toggle.Click += (_, _) => { d.Synced = toggle.IsChecked == true; model.SetDeviceCommand.Execute(d); _ = model.SetParametersAsync(model.Parameters with { Devices = new Dictionary<string, bool>(model.Parameters.Devices ?? new Dictionary<string, bool>()) { [d.Id] = d.Synced } }); };
                var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new(0, 0, 12, 8) };
                row.Children.Add(toggle);
                row.Children.Add(new DeviceNameEditor { DeviceId = d.Id, DisplayName = d.Name, Client = model.Client, Margin = new(0, 8, 0, 8) });
                devices.Children.Add(row);
            }
        }
        suppress = false;
    }
}

public sealed class DirectionDial : FrameworkElement
{
    private double angle;
    public double Angle { get => angle; set { angle = value; InvalidateVisual(); } }
    public event Action<double>? AngleChanged;
    public DirectionDial() { Width = Height = 62; HorizontalAlignment = HorizontalAlignment.Left; Cursor = Cursors.Hand; ToolTip = "Drag to set travel direction"; }
    protected override void OnRender(DrawingContext dc)
    {
        var centre = new Point(31, 31); dc.DrawEllipse(new SolidColorBrush(Color.FromRgb(35, 35, 41)), new Pen(Brushes.DimGray, 1), centre, 27, 27);
        double a = Angle * Math.PI / 180; var tip = new Point(31 + Math.Cos(a) * 23, 31 + Math.Sin(a) * 23);
        dc.DrawLine(new Pen(new SolidColorBrush(ColorUtil.Accent), 3), centre, tip); dc.DrawEllipse(Brushes.White, null, tip, 3, 3);
    }
    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e) { CaptureMouse(); Pick(e.GetPosition(this)); }
    protected override void OnMouseMove(MouseEventArgs e) { if (IsMouseCaptured) Pick(e.GetPosition(this)); }
    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e) { ReleaseMouseCapture(); }
    private void Pick(Point p) { Angle = (Math.Atan2(p.Y - 31, p.X - 31) * 180 / Math.PI + 360) % 360; AngleChanged?.Invoke(Angle); }
}

