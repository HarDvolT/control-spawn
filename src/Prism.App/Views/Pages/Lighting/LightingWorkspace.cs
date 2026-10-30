using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Data;
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
    private readonly CaseMapPreview preview = new() { Height = 350, LedEditing = false };
    private readonly LedEditor editor = new() { SharedCanvas = true };
    private readonly PaletteStrip palette = new();
    private readonly SpacedGrid gallery = new() { Columns = 3, Gap = 10 };
    private readonly WrapPanel extraEffects = new(), devices = new();
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
        root.ColumnDefinitions.Add(new() { Width = new GridLength(1.443, GridUnitType.Star) }); root.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new() { Height = GridLength.Auto }); root.RowDefinitions.Add(new() { Height = GridLength.Auto });
        root.Children.Add(canvasPanel); root.Children.Add(inspector); Grid.SetColumn(inspector, 1);
        canvasPanel.Margin = new(0, 0, 16, 0);
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
        // Put the existing power tools behind the v2 mode selector; keep their command wiring.
        var canvasChildren = canvasPanel.Children.Cast<UIElement>().ToArray(); canvasPanel.Children.Clear();
        var inspectorChildren = inspector.Children.Cast<UIElement>().ToArray(); inspector.Children.Clear();
        var host = new StackPanel(); Content = host;
        var heading = new DockPanel { Margin = new(0, 0, 0, 16) };
        var modes = new SegmentedControl { ItemsSource = new[] { "Whole case", "Per device", "Sensor" }, SelectedIndex = 0, VerticalAlignment = VerticalAlignment.Bottom };
        DockPanel.SetDock(modes, Dock.Right); heading.Children.Add(modes);
        var titles = new StackPanel();
        var eyebrow = new TextBlock { Text = "LIGHTING" }; eyebrow.SetResourceReference(StyleProperty, "PageEyebrow"); titles.Children.Add(eyebrow);
        var title = new TextBlock { Text = "Your whole setup, one colour" }; title.SetResourceReference(StyleProperty, "H1"); titles.Children.Add(title);
        heading.Children.Add(titles); host.Children.Add(heading);
        var setup = canvasChildren.OfType<CaseLightingSetup>().Single();
        setup.Visibility = Visibility.Collapsed;
        var setupRow = new DockPanel();
        var setupButton = new Button { Content = "Set up case lighting", Padding = new(18, 0, 18, 0), MinHeight = 40, Background = new SolidColorBrush(Color.FromRgb(245, 165, 36)), Foreground = Brushes.Black };
        setupButton.Click += (_, _) => { setup.Visibility = setup.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible; setup.IsExpanded = true; };
        DockPanel.SetDock(setupButton, Dock.Right); setupRow.Children.Add(setupButton);
        setupRow.Children.Add(new TextBlock { Text = "ARGB headers start at 0 LEDs. Set the exact LED count to light your case fans and strip, down to the last LED.", TextWrapping = TextWrapping.Wrap, FontSize = 13, VerticalAlignment = VerticalAlignment.Center, Margin = new(0, 0, 18, 0) });
        host.Children.Add(new Border { Child = setupRow, Padding = new(18, 14, 18, 14), CornerRadius = new(16), Margin = new(0, 0, 0, 16), BorderThickness = new(1), BorderBrush = new SolidColorBrush(Color.FromArgb(85, 245, 165, 36)), Background = new LinearGradientBrush(Color.FromArgb(40, 245, 165, 36), Color.FromArgb(5, 245, 165, 36), 0) });
        host.Children.Add(setup); host.Children.Add(root);
        var previewBody = new StackPanel();
        Header(previewBody, "LIVE PREVIEW", 11); ((TextBlock)previewBody.Children[0]).Margin = new(0, 0, 0, 4);
        var illustration = new CaseIllustration { Margin = new(0, 1, 0, 0) }; previewBody.Children.Add(illustration);
        stage.Visibility = Visibility.Collapsed; previewBody.Children.Add(stage);
        previewBody.Children.Add(devices);
        canvasPanel.Children.Add(new GlassCard { Content = previewBody, MinHeight = 535 });
        var tools = new StackPanel { Visibility = Visibility.Collapsed, Margin = new(0, 12, 0, 0) };
        tools.Children.Add(mode); tools.Children.Add(transform); tools.Children.Add(editor);
        canvasPanel.Children.Add(tools); editor.Visibility = Visibility.Collapsed;
        var sensor = canvasChildren.OfType<SensorBindingPanel>().Single(); sensor.Visibility = Visibility.Collapsed; canvasPanel.Children.Add(sensor);
        // The painting toolbar is only visible in per-device paint mode.
        stage.Children[1].Visibility = Visibility.Collapsed;
        edit.IsChecked = false;
        edit.Click += (_, _) => stage.Children[1].Visibility = edit.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        map.Click += (_, _) => stage.Children[1].Visibility = Visibility.Collapsed;
        modes.SelectionChanged += (_, _) => {
            bool perDevice = modes.SelectedIndex == 1;
            illustration.Visibility = perDevice ? Visibility.Collapsed : Visibility.Visible;
            stage.Visibility = perDevice ? Visibility.Visible : Visibility.Collapsed;
            tools.Visibility = perDevice ? Visibility.Visible : Visibility.Collapsed;
            sensor.Visibility = modes.SelectedIndex == 2 ? Visibility.Visible : Visibility.Collapsed;
            if (!perDevice) { preview.LedEditing = preview.MapEditing = false; stage.Children[1].Visibility = Visibility.Collapsed; }
            else { preview.LedEditing = edit.IsChecked == true; preview.MapEditing = map.IsChecked == true; editor.Visibility = preview.LedEditing ? Visibility.Visible : Visibility.Collapsed; stage.Children[1].Visibility = preview.LedEditing ? Visibility.Visible : Visibility.Collapsed; }
        };
        edit.Content = "Paint LEDs"; map.Content = "Arrange case";
        var effectsBody = new StackPanel(); Header(effectsBody, "EFFECT", 11);
        var effectScroll = (ScrollViewer)inspectorChildren[1]; effectScroll.MaxHeight = 260; effectScroll.Margin = new(0, 9, 0, 0); effectsBody.Children.Add(effectScroll);

        var controlBody = new StackPanel();
        var colourRow = new Grid { Margin = new(0, 0, 0, 8) }; colourRow.ColumnDefinitions.Add(new() { Width = new GridLength(168) }); colourRow.ColumnDefinitions.Add(new());
        var colourControls = new StackPanel(); Grid.SetColumn(colourControls, 1); colourRow.Children.Add(colourControls);
        Header(colourControls, "COLOUR \u00b7 EVERYTHING", 11); ((TextBlock)colourControls.Children[0]).Margin = new(0, 0, 0, 4);
        var swatches = new UniformGrid { Columns = 4, Margin = new(0, 8, 0, 0) };
        foreach (var colour in new[] { "#FF4D1A", "#FF4D4F", "#F5A524", "#3DDC84", "#36D6FF", "#4D6BFF", "#B26BFF", "#F4F5F7" })
        {
            var button = new ColorSwatch { Width = 60, Height = 34, ColorHex = colour, Margin = new(0, 0, 8, 8) };
            button.SetBinding(ColorSwatch.SelectedHexProperty, new Binding("ColorEntry"));
            button.Click += (_, _) => model?.SelectColorCommand.Execute(colour); swatches.Children.Add(button);
        }
        colourControls.Children.Add(swatches);
        var wheel = new ColorWheel { Width = 158, Height = 158, MinWidth = 0, MinHeight = 0, RingMode = true, HorizontalAlignment = HorizontalAlignment.Left, Margin = new(-2, 4, 0, 0) };
        wheel.SetBinding(ColorWheel.ColorHexProperty, new Binding("ColorEntry") { Mode = BindingMode.TwoWay, UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged });
        colourRow.Children.Add(wheel); controlBody.Children.Add(colourRow);
        var advanced = new StackPanel();
        foreach (var child in inspectorChildren.Skip(2))
        {
            if (ReferenceEquals(child, devices) || ReferenceEquals(child, baseColors)) continue;
            if (ReferenceEquals(child, brightness)) { brightness.Margin = new(0, 4, 0, 6); brightness.Height = 14; brightness.Glow = false; brightness.UseGradient = true; brightness.AccentColor = ColorUtil.Parse("#36D6FF", Colors.Cyan); colourControls.Children.Add(child); }
            else if (child is TextBlock label && label.Text.StartsWith("Brightness")) { label.Margin = new(0, 6, 0, 0); colourControls.Children.Add(child); }
            else advanced.Children.Add(child);
        }
        var brightnessPresets = new UniformGrid { Columns = 4 };
        foreach (var amount in new[] { 25, 50, 80, 100 }) { var button = new Button { Content = amount == 100 ? "Max" : amount + "%", Margin = new(0, 0, 6, 0), Padding = new(4), MinHeight = 36 }; button.Click += (_, _) => { if (model is not null) model.BrightnessPercent = amount; }; brightnessPresets.Children.Add(button); }
        colourControls.Children.Add(brightnessPresets);
        controlBody.Children.Add(effectsBody);
        advanced.Children.Add(wholeCase);
        advanced.Children.Add(new Expander { Header = "More effects", Content = extraEffects });
        var advancedMenu = new ContextMenu(); advancedMenu.SetBinding(DataContextProperty, new Binding("PlacementTarget.DataContext") { RelativeSource = RelativeSource.Self });
        advancedMenu.Items.Add(new MenuItem { Header = new ScrollViewer { Content = advanced, Width = 520, MaxHeight = 600, VerticalScrollBarVisibility = ScrollBarVisibility.Auto }, StaysOpenOnClick = true });

        var colourCard = new GlassCard { Content = controlBody, MinHeight = 535, ContextMenu = advancedMenu, ToolTip = "Right-click for advanced lighting controls and more effects" }; inspector.Children.Add(colourCard);
        SizeChanged += (_, _) => Responsive();
    }
    private void Responsive()
    {
        bool narrow = ActualWidth < 850;
        root.ColumnDefinitions[1].Width = narrow ? new(0) : new(1, GridUnitType.Star);
        Grid.SetColumn(inspector, narrow ? 0 : 1); Grid.SetRow(inspector, narrow ? 1 : 0);
        canvasPanel.Margin = narrow ? new(0, 0, 0, 24) : new(0, 0, 16, 0);
        preview.Height = narrow ? 300 : 440;
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
        gallery.Children.Clear(); extraEffects.Children.Clear();
        if (model is null) return;
        string[] preferred = ["Static", "Breathing", "RainbowWave", "Aurora", "Comet", "Fire", "OceanRipple", "Rain", "Strobing"];
        foreach (var effect in model.Effects.OrderBy(e => Array.IndexOf(preferred, e.Effect.ToString()) is var index && index >= 0 ? index : 99))
        {
            var content = new StackPanel();
            var treatment = new Border { Height = 34, CornerRadius = new(9), Margin = new(0, 0, 0, 8) };
            string[] colours = effect.Effect.ToString() switch
            {
                "Breathing" => ["#167184", "#36D6FF", "#167184"], "RainbowWave" => ["#F5A524", "#3DDC84", "#36D6FF", "#B26BFF"],
                "Aurora" => ["#36D6FF", "#6A85FF", "#B26BFF"], "Comet" => ["#0D0E12", "#0D0E12", "#36D6FF"],
                "Fire" => ["#F5A524", "#FFE96A", "#FF4D1A"], "OceanRipple" => ["#36D6FF", "#12354A", "#244F9C"],
                "Rain" => ["#36D6FF", "#12303B", "#36D6FF"], _ => [model.ColorHex, model.ColorHex]
            };
            var gradient = new LinearGradientBrush { StartPoint = new(0, 0), EndPoint = new(1, 0) };
            for (int i = 0; i < colours.Length; i++) gradient.GradientStops.Add(new(ColorUtil.Parse(colours[i], Colors.White), i / (double)(colours.Length - 1)));
            treatment.Background = gradient;
            content.Children.Add(treatment);
            content.Children.Add(new TextBlock { Text = effect.Effect.ToString() switch { "Strobing" => "Strobe", "Fire" => "Fire", "Rain" => "Rain", "OceanRipple" => "Ocean", _ => effect.Name }, FontSize = 13, FontWeight = FontWeights.SemiBold });
            var button = new Button { Content = content, Height = 80, Padding = new(10), HorizontalContentAlignment = HorizontalAlignment.Stretch, ToolTip = effect.Subtitle, Tag = effect };
            button.Click += (_, _) => model?.SelectEffectCommand.Execute(effect);
            if (preferred.Contains(effect.Effect.ToString())) gallery.Children.Add(button);
            else { button.Width = 130; button.Margin = new(0, 0, 8, 8); extraEffects.Children.Add(button); }
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
                var body = new StackPanel { Orientation = Orientation.Horizontal };
                body.Children.Add(new System.Windows.Shapes.Ellipse { Width = 7, Height = 7, Fill = new SolidColorBrush(ColorUtil.Parse(d.Synced ? model.ColorHex : "#7C808A", Colors.Gray)), Margin = new(0, 0, 8, 0) });
                body.Children.Add(new TextBlock { Text = d.Name, FontSize = 12, Foreground = Brushes.WhiteSmoke, FontWeight = FontWeights.SemiBold });
                var toggle = new System.Windows.Controls.Primitives.ToggleButton { Content = body, IsChecked = d.Synced && p.Devices?.GetValueOrDefault(d.Id, true) != false, Margin = new(0, 0, 8, 8), ToolTip = "Sync " + d.Name };
                var chrome = new FrameworkElementFactory(typeof(Border)); chrome.SetValue(Border.CornerRadiusProperty, new CornerRadius(16)); chrome.SetValue(Border.BackgroundProperty, new SolidColorBrush(Color.FromRgb(30, 32, 38))); chrome.SetValue(Border.BorderBrushProperty, new SolidColorBrush(Color.FromArgb(40, 255, 255, 255))); chrome.SetValue(Border.BorderThicknessProperty, new Thickness(1)); chrome.SetValue(Border.PaddingProperty, new Thickness(10, 7, 10, 7));
                var presenter = new FrameworkElementFactory(typeof(ContentPresenter)); presenter.SetValue(ContentPresenter.ContentProperty, new TemplateBindingExtension(ContentControl.ContentProperty)); chrome.AppendChild(presenter);
                toggle.Template = new ControlTemplate(typeof(System.Windows.Controls.Primitives.ToggleButton)) { VisualTree = chrome };
                toggle.Click += (_, _) => { d.Synced = toggle.IsChecked == true; model.SetDeviceCommand.Execute(d); _ = model.SetParametersAsync(model.Parameters with { Devices = new Dictionary<string, bool>(model.Parameters.Devices ?? new Dictionary<string, bool>()) { [d.Id] = d.Synced } }); };
                devices.Children.Add(toggle);
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

