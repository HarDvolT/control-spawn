using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Automation;
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

/// <summary>
/// The lighting studio. Two balanced rows: what your PC looks like now (preview) beside the colour you pick,
/// then the effect library beside the controls that effect actually uses. Per-device painting and sensor
/// lighting swap into the same frame from the mode switch.
/// </summary>
public sealed class LightingWorkspace : UserControl
{
    private const double Gap = 16;
    private readonly CaseMapPreview preview = new() { Height = 400, LedEditing = false };
    private readonly LedEditor editor = new() { SharedCanvas = true };
    private readonly CaseIllustration illustration = new() { Height = 380 };
    private readonly PaletteStrip palette = new();
    private readonly ComboBox palettes = new() { ItemsSource = Palettes.BuiltIn.Keys, MinWidth = 150 };
    private readonly SpacedGrid gallery = new() { Columns = 5, Gap = 10 };
    private readonly Button lightsOff = new() { MinHeight = 38, Padding = new(16, 0, 16, 0), Margin = new(0, 0, 12, 0), VerticalAlignment = VerticalAlignment.Bottom };
    private LightingEffect lastOnEffect = LightingEffect.Static;
    private readonly SegmentedControl categories = new() { ItemsSource = EffectCatalog.Categories, SelectedIndex = 0 };
    private readonly SegmentedControl modes = new() { ItemsSource = new[] { "Whole case", "Per device", "Sensor" }, SelectedIndex = 0, VerticalAlignment = VerticalAlignment.Bottom };
    private readonly SegmentedControl lampFollow = new() { ItemsSource = new[] { "Follow PC", "PC average", "Own colour" } };
    private readonly WrapPanel devices = new();
    private readonly TextBlock title = new(), subtitle = new(), followNotice = new() { FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new(0, 14, 0, 10) };
    private readonly TextBlock brightnessValue = new(), speedValue = new(), sizeValue = new(), tailValue = new(), countValue = new();
    private readonly TextBlock tuneTitle = new(), tuneHint = new(), status = new() { TextWrapping = TextWrapping.Wrap, FontSize = 12, Margin = new(0, 10, 0, 0) };
    private readonly TextBox hex = new() { Width = 96, MaxLength = 7, Background = Brushes.Transparent, BorderThickness = new(0), Foreground = Brushes.WhiteSmoke, CaretBrush = new SolidColorBrush(ColorUtil.Accent), FontSize = 13, VerticalContentAlignment = VerticalAlignment.Center };
    private readonly ThickSlider brightness = Slider(0, 100), speed = Slider(0, 100), angle = Slider(0, 360), scale = Slider(0.2, 4), tail = Slider(0.05, 1), count = Slider(1, 6), rotation = Slider(0, 360);
    private readonly DirectionDial dial = new();
    private readonly CheckBox cpuBeat = new() { Content = "Beat faster as the CPU works harder", Margin = new(0, 4, 0, 0) };
    private readonly ComboBox sceneBox = new() { MinWidth = 150 };
    private readonly Dictionary<EffectControls, FrameworkElement> tuneRows = new();
    private readonly List<(Button Button, EffectTile Tile, EffectInfo Info)> tiles = new();
    private Border? argbBanner;
    private FrameworkElement? noTuning;
    private LightingViewModel? model;
    private bool suppress;
    private string deviceSignature = "";

    /// <summary>0 whole case, 1 per device, 2 sensor.</summary>
    public int Mode { get => modes.SelectedIndex; set => modes.SelectedIndex = value; }

    public LightingWorkspace()
    {
        var host = new StackPanel(); Content = host;

        // Heading: what is lit right now, and the mode switch.
        var heading = new DockPanel { Margin = new(0, 0, 0, 18) };
        DockPanel.SetDock(modes, Dock.Right); heading.Children.Add(modes);
        DockPanel.SetDock(lightsOff, Dock.Right); heading.Children.Add(lightsOff);
        lightsOff.Click += (_, _) => { if (model is null) return; var target = model.Effect == LightingEffect.Off ? lastOnEffect : LightingEffect.Off; if (model.Effects.FirstOrDefault(e => e.Effect == target) is { } option) model.SelectEffectCommand.Execute(option); };
        var titles = new StackPanel();
        titles.Children.Add(Styled(new TextBlock { Text = "LIGHTING" }, "PageEyebrow"));
        titles.Children.Add(Styled(title, "H1"));
        titles.Children.Add(Styled(subtitle, "PageSubtitle"));
        heading.Children.Add(titles); host.Children.Add(heading);

        // Shown only while an ARGB header still has 0 LEDs.
        var setup = new CaseLightingSetup { Visibility = Visibility.Collapsed };
        var setupRow = new DockPanel();
        var setupButton = new Button { Content = "Set up case lighting", Padding = new(18, 0, 18, 0), MinHeight = 40, Background = new SolidColorBrush(ColorUtil.Fair), Foreground = Brushes.Black };
        setupButton.Click += (_, _) => { setup.Visibility = setup.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible; setup.IsExpanded = true; };
        DockPanel.SetDock(setupButton, Dock.Right); setupRow.Children.Add(setupButton);
        setupRow.Children.Add(new TextBlock { Text = "An ARGB header is set to 0 LEDs, so whatever is plugged into it stays dark. Set the LED count to light it.", TextWrapping = TextWrapping.Wrap, FontSize = 13, VerticalAlignment = VerticalAlignment.Center, Margin = new(0, 0, 18, 0) });
        host.Children.Add(argbBanner = new Border { Child = setupRow, Padding = new(18, 14, 18, 14), CornerRadius = new(16), Margin = new(0, 0, 0, Gap), BorderThickness = new(1), BorderBrush = new SolidColorBrush(Color.FromArgb(85, 245, 165, 36)), Background = new LinearGradientBrush(Color.FromArgb(40, 245, 165, 36), Color.FromArgb(5, 245, 165, 36), 0), Visibility = Visibility.Collapsed });
        host.Children.Add(setup);

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new() { Width = new GridLength(7, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new() { Width = new GridLength(Gap) });
        grid.ColumnDefinitions.Add(new() { Width = new GridLength(5, GridUnitType.Star) });
        grid.RowDefinitions.Add(new() { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new() { Height = new GridLength(Gap) });
        grid.RowDefinitions.Add(new() { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new() { Height = GridLength.Auto });
        host.Children.Add(grid);

        // Row 1, left: the live preview (whole case) or the editable canvas (per device).
        var previewBody = new StackPanel();
        var liveHeader = CardHeader("YOUR PC, LIVE", null);
        previewBody.Children.Add(liveHeader);
        previewBody.Children.Add(illustration);
        // Per device: the part-by-part studio, with the case layout tools one click away inside it.
        var arrangeTools = BuildPerDeviceTools(); arrangeTools.Visibility = Visibility.Collapsed;
        var studio = new DeviceStudio { ArrangeTools = arrangeTools };
        var perDevice = new StackPanel { Visibility = Visibility.Collapsed }; perDevice.Children.Add(studio); perDevice.Children.Add(arrangeTools);
        previewBody.Children.Add(perDevice);
        followNotice.Margin = new(0, 12, 0, 8);
        previewBody.Children.Add(followNotice); previewBody.Children.Add(devices);
        Place(grid, new GlassCard { Content = previewBody }, 0, 0);

        // Row 1, right: colour and brightness.
        Place(grid, new GlassCard { Content = BuildColourCard() }, 0, 2);

        // Row 2: the effect library and the controls the chosen effect uses.
        Place(grid, new GlassCard { Content = BuildEffectsCard() }, 2, 0);
        Place(grid, new GlassCard { Content = BuildTuneCard() }, 2, 2);

        // Sensor mode adds its panel underneath, across the full width.
        var sensor = new SensorBindingPanel { Visibility = Visibility.Collapsed, Margin = new(0, Gap, 0, 0) };
        Grid.SetRow(sensor, 3); Grid.SetColumnSpan(sensor, 3); grid.Children.Add(sensor);

        modes.SelectionChanged += (_, _) =>
        {
            bool device = modes.SelectedIndex == 1;
            illustration.Visibility = device ? Visibility.Collapsed : Visibility.Visible;
            perDevice.Visibility = device ? Visibility.Visible : Visibility.Collapsed;
            // The studio has its own header and its own per-part switches, so the case-wide ones step aside.
            liveHeader.Visibility = followNotice.Visibility = this.devices.Visibility = device ? Visibility.Collapsed : Visibility.Visible;
            sensor.Visibility = modes.SelectedIndex == 2 ? Visibility.Visible : Visibility.Collapsed;
            if (!device) { preview.LedEditing = preview.MapEditing = false; editor.Visibility = Visibility.Collapsed; }
            UpdateHeading();
        };

        DataContextChanged += (_, _) => { if (model is not null) model.PropertyChanged -= Changed; model = DataContext as LightingViewModel; if (model is not null) model.PropertyChanged += Changed; BuildGallery(); Refresh(); };
        Loaded += (_, _) => { if (model is not null) { model.PropertyChanged -= Changed; model.PropertyChanged += Changed; } Refresh(); };
        Unloaded += (_, _) => { if (model is not null) model.PropertyChanged -= Changed; };
        SizeChanged += (_, _) => gallery.Columns = ActualWidth < 1000 ? 3 : ActualWidth < 1250 ? 4 : 5;
    }

    private StackPanel BuildColourCard()
    {
        var body = new StackPanel();
        var hexBox = new Border { Background = new SolidColorBrush(Color.FromRgb(13, 14, 18)), BorderBrush = new SolidColorBrush(Color.FromArgb(31, 255, 255, 255)), BorderThickness = new(1), CornerRadius = new(10), Padding = new(10, 4, 10, 4), Child = hex };
        hex.LostFocus += (_, _) => CommitHex(); hex.KeyDown += (_, e) => { if (e.Key == Key.Enter) CommitHex(); };
        AutomationProperties.SetName(hex, "Colour code");
        body.Children.Add(CardHeader("COLOUR", hexBox));
        var row = new Grid { Margin = new(0, 4, 0, 0) };
        row.ColumnDefinitions.Add(new() { Width = new GridLength(176) }); row.ColumnDefinitions.Add(new());
        var wheel = new ColorWheel { Width = 160, Height = 160, MinWidth = 0, MinHeight = 0, RingMode = true, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top };
        wheel.SetBinding(ColorWheel.ColorHexProperty, new Binding("ColorEntry") { Mode = BindingMode.TwoWay, UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged });
        row.Children.Add(wheel);
        var swatches = new UniformGrid { Columns = 4, VerticalAlignment = VerticalAlignment.Center }; Grid.SetColumn(swatches, 1);
        foreach (var colour in new[] { "#FF4D1A", "#FF4D4F", "#F5A524", "#3DDC84", "#36D6FF", "#4D6BFF", "#B26BFF", "#F4F5F7" })
        {
            var swatch = new ColorSwatch { Height = 36, ColorHex = colour, Margin = new(0, 0, 8, 8) };
            swatch.SetBinding(ColorSwatch.SelectedHexProperty, new Binding("ColorEntry"));
            AutomationProperties.SetName(swatch, "Colour " + colour);
            swatch.Click += (_, _) => model?.SelectColorCommand.Execute(colour); swatches.Children.Add(swatch);
        }
        row.Children.Add(swatches); body.Children.Add(row);

        body.Children.Add(ValueLabel("BRIGHTNESS", brightnessValue, new(0, 18, 0, 8)));
        brightness.Glow = false; brightness.UseGradient = true; brightness.AccentColor = ColorUtil.Parse("#36D6FF", Colors.Cyan);
        brightness.ValueChanged += (_, _) => { brightnessValue.Text = $"{brightness.Value:0}%"; if (!suppress && model is not null) model.BrightnessPercent = brightness.Value; };
        AutomationProperties.SetName(brightness, "Brightness");
        body.Children.Add(brightness);
        var presets = new UniformGrid { Columns = 4, Margin = new(0, 12, 0, 0) };
        foreach (var amount in new[] { 25, 50, 80, 100 })
        {
            var button = new Button { Content = amount == 100 ? "Max" : amount + "%", Margin = new(0, 0, 8, 0), MinHeight = 36, Padding = new(4) };
            button.Click += (_, _) => { if (model is not null) model.BrightnessPercent = amount; };
            presets.Children.Add(button);
        }
        body.Children.Add(presets);

        // One-click looks: effect, colour and palette together.
        body.Children.Add(Styled(new TextBlock { Text = "QUICK LOOKS", Margin = new(0, 20, 0, 10) }, "SectionLabel"));
        var looks = new UniformGrid { Columns = 3 };
        foreach (var (name, effect, colour, paletteName, a, b) in new[]
        {
            ("Ember", LightingEffect.Static, "#FF4D1A", (string?)null, "#FF4D1A", "#7A1408"),
            ("Ice", LightingEffect.Static, "#36D6FF", null, "#36D6FF", "#0C3A4A"),
            ("Warm white", LightingEffect.Static, "#FFB070", null, "#FFD5A8", "#6B4420"),
            ("Aurora", LightingEffect.Aurora, "#29FCA8", "Aurora", "#29FCA8", "#A665F6"),
            ("Lava", LightingEffect.Fire, "#FF4D1A", "Fire", "#FF9A13", "#CA2907"),
            ("Rainbow", LightingEffect.RainbowWave, "#FF4D1A", "Spectrum", "#FF315A", "#588AFF"),
        })
        {
            var swatch = new Border { Height = 8, CornerRadius = new(4), Margin = new(0, 0, 0, 8), Background = new LinearGradientBrush(ColorUtil.Parse(a, Colors.White), ColorUtil.Parse(b, Colors.White), 0) };
            var content = new StackPanel(); content.Children.Add(swatch); content.Children.Add(new TextBlock { Text = name, FontSize = 12, FontWeight = FontWeights.SemiBold });
            var look = new Button { Content = content, Margin = new(0, 0, 8, 8), Padding = new(10, 10, 10, 9), HorizontalContentAlignment = HorizontalAlignment.Stretch, ToolTip = $"{EffectCatalog.For(effect).Name} in {name.ToLowerInvariant()}" };
            AutomationProperties.SetName(look, "Quick look " + name);
            look.Click += (_, _) => { if (model is not null) _ = model.ApplyLookAsync(effect, colour, paletteName); };
            looks.Children.Add(look);
        }
        body.Children.Add(looks);
        return body;
    }

    private StackPanel BuildEffectsCard()
    {
        var body = new StackPanel();
        categories.SelectionChanged += (_, _) => FilterGallery();
        body.Children.Add(CardHeader("EFFECTS", categories));
        gallery.Margin = new(0, 6, 0, 0);
        body.Children.Add(gallery);
        return body;
    }

    private StackPanel BuildTuneCard()
    {
        var body = new StackPanel();
        body.Children.Add(Styled(tuneTitle, "CardTitle"));
        tuneHint.Margin = new(0, 2, 0, 6); body.Children.Add(Styled(tuneHint, "SmallMuted"));

        var speedRow = new StackPanel();
        speedRow.Children.Add(ValueLabel("SPEED", speedValue, new(0, 12, 0, 8))); speedRow.Children.Add(speed);
        speed.ValueChanged += (_, _) => { speedValue.Text = speed.Value < 34 ? "Slow" : speed.Value < 67 ? "Medium" : "Fast"; if (!suppress && model is not null) model.SpeedPercent = speed.Value; };
        AutomationProperties.SetName(speed, "Speed");

        var paletteRow = new StackPanel();
        var paletteHeader = new DockPanel { Margin = new(0, 14, 0, 6) };
        DockPanel.SetDock(palettes, Dock.Right); paletteHeader.Children.Add(palettes);
        paletteHeader.Children.Add(Styled(new TextBlock { Text = "PALETTE", VerticalAlignment = VerticalAlignment.Center }, "SectionLabel"));
        paletteRow.Children.Add(paletteHeader); paletteRow.Children.Add(palette);
        paletteRow.Children.Add(Styled(new TextBlock { Text = "Drag a dot to move it. Pick a colour above to recolour the selected dot.", Margin = new(0, 2, 0, 0), TextWrapping = TextWrapping.Wrap }, "SmallMuted"));
        palettes.SelectionChanged += (_, _) => { if (!suppress && model is not null && palettes.SelectedItem is string value) _ = model.SetParametersAsync(model.Parameters with { Palette = value, Stops = null }); };
        AutomationProperties.SetName(palettes, "Palette");

        var directionRow = new DockPanel { Margin = new(0, 14, 0, 0) };
        dial.Margin = new(0, 0, 16, 0); DockPanel.SetDock(dial, Dock.Left); directionRow.Children.Add(dial);
        var directionText = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        directionText.Children.Add(Styled(new TextBlock { Text = "DIRECTION" }, "SectionLabel"));
        directionText.Children.Add(Styled(new TextBlock { Text = "Drag the dial to choose which way it travels", Margin = new(0, 4, 0, 0), TextWrapping = TextWrapping.Wrap }, "SmallMuted"));
        directionRow.Children.Add(directionText);
        dial.AngleChanged += value => { if (!suppress && model is not null) _ = model.SetParametersAsync(model.Parameters with { Angle = Math.Round(value) }); };

        var sizeRow = new StackPanel();
        sizeRow.Children.Add(ValueLabel("SIZE", sizeValue, new(0, 14, 0, 8))); sizeRow.Children.Add(scale);
        scale.ValueChanged += (_, _) => { sizeValue.Text = $"{scale.Value:0.0}×"; if (!suppress && model is not null) _ = model.SetParametersAsync(model.Parameters with { Scale = Math.Round(scale.Value, 2) }); };
        AutomationProperties.SetName(scale, "Size");

        var cometRow = new StackPanel();
        cometRow.Children.Add(ValueLabel("COMETS", countValue, new(0, 14, 0, 8))); cometRow.Children.Add(count);
        cometRow.Children.Add(ValueLabel("TAIL", tailValue, new(0, 12, 0, 8))); cometRow.Children.Add(tail);
        count.Step = 1;
        count.ValueChanged += (_, _) => { countValue.Text = $"{count.Value:0}"; if (!suppress && model is not null) _ = model.SetParametersAsync(model.Parameters with { CometCount = (int)Math.Round(count.Value) }); };
        tail.ValueChanged += (_, _) => { tailValue.Text = tail.Value < 0.3 ? "Short" : tail.Value < 0.65 ? "Medium" : "Long"; if (!suppress && model is not null) _ = model.SetParametersAsync(model.Parameters with { TailLength = Math.Round(tail.Value, 2) }); };

        var beatRow = new StackPanel { Margin = new(0, 12, 0, 0) }; beatRow.Children.Add(cpuBeat);
        cpuBeat.Click += (_, _) => { if (model is not null) _ = model.SetParametersAsync(model.Parameters with { HeartbeatCpu = cpuBeat.IsChecked == true }); };

        noTuning = Styled(new TextBlock { Text = "This effect has nothing to tune. It takes care of itself.", Margin = new(0, 12, 0, 0), TextWrapping = TextWrapping.Wrap }, "SmallMuted");
        tuneRows[EffectControls.Speed] = speedRow; tuneRows[EffectControls.Palette] = paletteRow; tuneRows[EffectControls.Direction] = directionRow;
        tuneRows[EffectControls.Size] = sizeRow; tuneRows[EffectControls.Comets] = cometRow; tuneRows[EffectControls.CpuBeat] = beatRow;
        foreach (var row in new FrameworkElement[] { speedRow, paletteRow, directionRow, sizeRow, cometRow, beatRow, noTuning }) body.Children.Add(row);

        // Lamps and saving are about the whole look, so they sit at the bottom of the card.
        body.Children.Add(new Border { Height = 1, Background = new SolidColorBrush(Color.FromArgb(18, 255, 255, 255)), Margin = new(0, 18, 0, 0) });
        body.Children.Add(Styled(new TextBlock { Text = "LAMPS", Margin = new(0, 14, 0, 8) }, "SectionLabel"));
        lampFollow.SelectionChanged += (_, _) => { if (!suppress) _ = SetLampFollowAsync(lampFollow.SelectedIndex switch { 0 => "pc", 1 => "average", _ => "own" }); };
        lampFollow.HorizontalAlignment = HorizontalAlignment.Left;
        body.Children.Add(lampFollow);
        body.Children.Add(Styled(new TextBlock { Text = "SAVE THIS LOOK", Margin = new(0, 16, 0, 8) }, "SectionLabel"));
        var save = new DockPanel();
        var saveButton = new Button { Content = "Save to scene", MinHeight = 36, Padding = new(16, 0, 16, 0), Margin = new(8, 0, 0, 0) };
        saveButton.Click += (_, _) => { if (model is not null && sceneBox.SelectedItem is string scene) { model.SceneName = scene; model.SaveToSceneCommand.Execute(null); } };
        DockPanel.SetDock(saveButton, Dock.Right); save.Children.Add(saveButton); save.Children.Add(sceneBox);
        AutomationProperties.SetName(sceneBox, "Scene to save to");
        body.Children.Add(save);
        body.Children.Add(status);
        body.Children.Add(new Expander { Header = "Event flashes", Content = new EventOptionsPanel(), Foreground = Brushes.Silver, Margin = new(0, 12, 0, 0) });
        return body;
    }

    private StackPanel BuildPerDeviceTools()
    {
        var panel = new StackPanel();
        var modeRow = new WrapPanel { Margin = new(0, 0, 0, 10) };
        var paint = new CheckBox { Content = "Paint LEDs", Margin = new(0, 0, 18, 0) };
        var arrange = new CheckBox { Content = "Arrange case", Margin = new(0, 0, 18, 0) };
        modeRow.Children.Add(paint); modeRow.Children.Add(arrange);
        // Painting moved into the studio, so this panel is only the case layout: it opens straight into arranging.
        modeRow.Visibility = Visibility.Collapsed; panel.Children.Add(modeRow);
        panel.Children.Add(Styled(new TextBlock { Text = "Drag a part or a lamp to where it sits in your case. Wave and sweep effects travel across this map.", TextWrapping = TextWrapping.Wrap, Margin = new(0, 0, 0, 10) }, "SmallMuted"));
        panel.IsVisibleChanged += (_, _) => { preview.MapEditing = panel.IsVisible; arrange.IsChecked = panel.IsVisible; preview.LedEditing = false; preview.InvalidateVisual(); };
        var stage = new Grid(); stage.Children.Add(preview);
        var toolbar = new Border { Child = editor.DetachToolbar(), Background = new SolidColorBrush(Color.FromArgb(235, 32, 32, 38)), BorderBrush = Brushes.DimGray,
            BorderThickness = new(1), CornerRadius = new(14), Padding = new(10, 8, 2, 0), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Bottom, Margin = new(12, 0, 12, 34), Visibility = Visibility.Collapsed };
        stage.Children.Add(toolbar); panel.Children.Add(stage);
        paint.Click += (_, _) =>
        {
            preview.LedEditing = paint.IsChecked == true;
            if (preview.LedEditing) { arrange.IsChecked = false; preview.MapEditing = false; }
            editor.Visibility = toolbar.Visibility = preview.LedEditing ? Visibility.Visible : Visibility.Collapsed; preview.InvalidateVisual();
        };
        arrange.Click += (_, _) =>
        {
            preview.MapEditing = arrange.IsChecked == true;
            if (preview.MapEditing) { paint.IsChecked = false; preview.LedEditing = false; editor.Visibility = toolbar.Visibility = Visibility.Collapsed; }
            preview.InvalidateVisual();
        };
        var transform = new WrapPanel { Margin = new(0, 10, 0, 0) };
        transform.Children.Add(new TextBlock { Text = "Rotate selected", VerticalAlignment = VerticalAlignment.Center, Margin = new(0, 0, 12, 0) });
        rotation.Width = 160; transform.Children.Add(rotation);
        rotation.ValueChanged += (_, _) => { if (!suppress && preview.SelectedPlacement is { } selected) _ = preview.TransformSelectionAsync(rotation.Value, selected.Flip); };
        AddButton(transform, "Flip", () => { if (preview.SelectedPlacement is { } p) _ = preview.TransformSelectionAsync(p.Rotation, !p.Flip); });
        AddButton(transform, "Reset layout", () => { if (model is not null) _ = model.SetMapAsync(CaseMap.Default(model.State?.Lighting.Devices ?? CaseMapPreview.DemoDevices)); });
        preview.SelectionChanged += () => { suppress = true; rotation.Value = preview.SelectedPlacement?.Rotation ?? 0; suppress = false; };
        panel.Children.Add(transform);
        editor.Visibility = Visibility.Collapsed; panel.Children.Add(editor);
        return panel;
    }

    private void BuildGallery()
    {
        gallery.Children.Clear(); tiles.Clear();
        if (model is null) return;
        foreach (var info in EffectCatalog.All.Where(e => e.Category != "Hidden"))
        {
            var tile = new EffectTile { LightEffect = info.Effect, Margin = new(0, 0, 0, 8) };
            var content = new StackPanel();
            content.Children.Add(tile);
            content.Children.Add(new TextBlock { Text = info.Name, FontSize = 13, FontWeight = FontWeights.SemiBold });
            var button = new Button { Content = content, Padding = new(10, 10, 10, 10), HorizontalContentAlignment = HorizontalAlignment.Stretch, ToolTip = info.Description, BorderThickness = new(1.5) };
            AutomationProperties.SetName(button, "Effect " + info.Name);
            var option = model.Effects.FirstOrDefault(e => e.Effect == info.Effect);
            button.Click += (_, _) => { if (option is not null) model?.SelectEffectCommand.Execute(option); };
            tiles.Add((button, tile, info)); gallery.Children.Add(button);
        }
        FilterGallery();
    }

    private void FilterGallery()
    {
        var category = categories.SelectedItem as string ?? "All";
        foreach (var (button, _, info) in tiles) button.Visibility = category == "All" || info.Category == category ? Visibility.Visible : Visibility.Collapsed;
        // SpacedGrid lays out every child, so rebuild the visible set to keep rows tight.
        gallery.Children.Clear();
        foreach (var (button, _, _) in tiles.Where(t => t.Button.Visibility == Visibility.Visible)) gallery.Children.Add(button);
    }

    private void Changed(object? sender, PropertyChangedEventArgs args) => Refresh();

    private void Refresh()
    {
        if (model is null) return;
        suppress = true;
        editor.DataContext = model.Editor;
        var p = model.Parameters;
        var info = EffectCatalog.For(model.Effect);
        palettes.SelectedItem = p.Palette; dial.Angle = p.Angle; scale.Value = p.Scale; tail.Value = p.TailLength; count.Value = p.CometCount;
        brightness.Value = model.BrightnessPercent; speed.Value = model.SpeedPercent; cpuBeat.IsChecked = p.HeartbeatCpu;
        brightnessValue.Text = $"{model.BrightnessPercent:0}%";
        if (!hex.IsKeyboardFocusWithin) hex.Text = model.ColorHex;
        palette.InvalidateVisual(); preview.InvalidateVisual(); illustration.InvalidateVisual();
        status.Text = model.LastError ?? model.SavedText;
        status.Foreground = new SolidColorBrush(model.LastError is null ? ColorUtil.TextTertiary : ColorUtil.Poor);

        // Effect tiles: the chosen one is outlined and animates; the rest hold a frame.
        foreach (var (button, tile, tileInfo) in tiles)
        {
            bool on = tileInfo.Effect == model.Effect;
            tile.IsActive = on; tile.InvalidateVisual();
            button.BorderBrush = new SolidColorBrush(on ? ColorUtil.Accent : Color.FromArgb(18, 255, 255, 255));
            button.Background = new SolidColorBrush(on ? Color.FromArgb(28, 255, 77, 26) : Color.FromRgb(23, 24, 29));
        }
        if (model.Effect != LightingEffect.Off) lastOnEffect = model.Effect;
        lightsOff.Content = model.Effect == LightingEffect.Off ? "Turn lights on" : "Turn lights off";
        tuneTitle.Text = "Tune · " + info.Name;
        tuneHint.Text = info.Description;
        foreach (var (control, row) in tuneRows) row.Visibility = info.Controls.HasFlag(control) ? Visibility.Visible : Visibility.Collapsed;
        if (noTuning is not null) noTuning.Visibility = tuneRows.Keys.Any(c => info.Controls.HasFlag(c)) ? Visibility.Collapsed : Visibility.Visible;

        var follow = model.State?.Lamps?.FirstOrDefault()?.Follow ?? model.State?.Lamp?.Follow ?? "pc";
        lampFollow.SelectedIndex = follow switch { "average" => 1, "own" => 2, _ => 0 };
        var scenes = model.State?.Scenes?.Select(s => s.Name).ToArray() is { Length: > 0 } names ? names : ["Gaming", "Work", "Night", "Movie"];
        if (!scenes.SequenceEqual(sceneBox.ItemsSource as IEnumerable<string> ?? [])) { var keep = sceneBox.SelectedItem; sceneBox.ItemsSource = scenes; sceneBox.SelectedItem = keep is string k && scenes.Contains(k) ? k : scenes[0]; }

        RefreshDevices(p);
        UpdateHeading();
        if (argbBanner is not null)
            argbBanner.Visibility = model.State?.Lighting.Devices.Any(dev => dev.Zones?.Any(z => z.MaxLeds > z.MinLeds && z.LedCount == 0 && z.Name.Contains("Addressable", StringComparison.OrdinalIgnoreCase)) == true) == true
                ? Visibility.Visible : Visibility.Collapsed;
        suppress = false;
    }

    private void UpdateHeading()
    {
        if (model is null) return;
        var info = EffectCatalog.For(model.Effect);
        title.Text = modes.SelectedIndex switch { 1 => "Fine-tune each device", 2 => "Lights that show what your PC is doing", _ => model.Effect == LightingEffect.Off ? "Case lights are off" : "Your whole setup, one look" };
        int lit = model.State?.Lighting.Devices.Count(d => d.Synced && d.Connected) ?? 0, lamps = model.State?.Lamps?.Count(l => l.Connected) ?? 0;
        subtitle.Text = $"{info.Name}{(info.Controls.HasFlag(EffectControls.Colour) ? " · " + model.ColorHex : "")} · {model.BrightnessPercent:0}% · {lit} device{(lit == 1 ? "" : "s")}{(lamps > 0 ? $" and {lamps} lamp{(lamps == 1 ? "" : "s")}" : "")}";
    }

    private void RefreshDevices(EffectParameters p)
    {
        if (model is null) return;
        var lighting = model.State?.Lighting.Devices ?? [];
        var signature = string.Join(";", model.Devices.Select(d => $"{d.Id}:{d.Name}:{d.Synced}:{p.Devices?.GetValueOrDefault(d.Id, true) != false}"))
            + "|" + string.Join(";", (p.Devices ?? new Dictionary<string, bool>()).Where(k => k.Key.Contains("/zone/")).Select(k => $"{k.Key}={k.Value}"))
            + "|" + string.Join(";", lighting.Select(d => $"{d.Id}:{string.Join(",", (d.Zones ?? []).Select(z => z.LedCount))}"));
        if (signature == deviceSignature) return;
        deviceSignature = signature; devices.Children.Clear();
        int ramIndex = 0, excluded = 0;
        void SetChoice(string key, bool on) =>
            _ = model.SetParametersAsync(model.Parameters with { Devices = new Dictionary<string, bool>(model.Parameters.Devices ?? new Dictionary<string, bool>()) { [key] = on } });
        foreach (var d in model.Devices)
        {
            bool on = d.Synced && p.Devices?.GetValueOrDefault(d.Id, true) != false;
            var state = lighting.FirstOrDefault(x => x.Id == d.Id);
            var kind = state?.Kind ?? "";
            if (kind.Contains("Motherboard", StringComparison.OrdinalIgnoreCase) && on && state?.Zones is { Count: > 1 } zones)
            {
                // The board's own LEDs and each header it drives are separate parts here: pump and radiator fans, case fans.
                for (int z = 0; z < zones.Count; z++)
                {
                    if (zones[z].LedCount == 0) continue;
                    var key = SensorSources.ZoneTarget(d.Id, z);
                    bool partOn = p.Devices?.GetValueOrDefault(key, true) != false;
                    if (!partOn) excluded++;
                    devices.Children.Add(Chip(CaseMap.PartName(state, z), partOn, " · off", $"{zones[z].Name} · {zones[z].LedCount} LEDs on {d.Name}\n{(partOn ? "Follows the case. Click to turn it off." : "Off. Click to make it follow the case.")}",
                        now => SetChoice(key, now)));
                }
                continue;
            }
            if (!on) excluded++;
            // Short, recognisable names; the full model name is in the tooltip.
            var label = kind.Contains("RAM", StringComparison.OrdinalIgnoreCase) ? $"RAM {++ramIndex}"
                : kind.Contains("GPU", StringComparison.OrdinalIgnoreCase) ? "Graphics card"
                : kind.Contains("Motherboard", StringComparison.OrdinalIgnoreCase) ? "Motherboard and fans"
                : kind.Contains("strip", StringComparison.OrdinalIgnoreCase) ? "LED strip" : d.Name;
            devices.Children.Add(Chip(label, on, " · own colour", $"{d.Name}\n{(on ? "Follows the case. Click to give it its own colour." : "Keeps its own colour. Click to make it follow the case.")}",
                now => { d.Synced = now; model.SetDeviceCommand.Execute(d); SetChoice(d.Id, now); }));
        }
        followNotice.Text = excluded == 0 ? "APPLIES TO · every part follows this look"
            : $"APPLIES TO · {excluded} part{(excluded == 1 ? " is" : "s are")} left out. Click a chip to include it.";
        followNotice.Foreground = new SolidColorBrush(excluded == 0 ? ColorUtil.TextTertiary : ColorUtil.Fair);
    }

    private static ToggleButton Chip(string label, bool on, string offNote, string tip, Action<bool> changed)
    {
        var body = new StackPanel { Orientation = Orientation.Horizontal };
        body.Children.Add(new TextBlock { Text = on ? "✓" : "○", FontSize = 12, FontWeight = FontWeights.Bold, Foreground = new SolidColorBrush(on ? ColorUtil.Accent : Color.FromRgb(124, 128, 138)), Margin = new(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center });
        body.Children.Add(new TextBlock { Text = label, FontSize = 12, Foreground = on ? Brushes.WhiteSmoke : new SolidColorBrush(Color.FromRgb(163, 166, 174)), FontWeight = FontWeights.SemiBold });
        if (!on) body.Children.Add(new TextBlock { Text = offNote, FontSize = 12, Foreground = new SolidColorBrush(ColorUtil.Fair) });
        var toggle = new ToggleButton { Content = body, IsChecked = on, Margin = new(0, 0, 8, 8), Cursor = Cursors.Hand, ToolTip = tip };
        AutomationProperties.SetName(toggle, $"{label} follows case colour");
        var chrome = new FrameworkElementFactory(typeof(Border)); chrome.SetValue(Border.CornerRadiusProperty, new CornerRadius(16));
        chrome.SetValue(Border.BackgroundProperty, new SolidColorBrush(on ? Color.FromArgb(38, 255, 77, 26) : Colors.Transparent));
        chrome.SetValue(Border.BorderBrushProperty, new SolidColorBrush(on ? Color.FromArgb(150, 255, 77, 26) : Color.FromArgb(150, 245, 165, 36)));
        chrome.SetValue(Border.BorderThicknessProperty, new Thickness(1)); chrome.SetValue(Border.PaddingProperty, new Thickness(10, 7, 12, 7));
        var presenter = new FrameworkElementFactory(typeof(ContentPresenter)); presenter.SetValue(ContentPresenter.ContentProperty, new TemplateBindingExtension(ContentControl.ContentProperty)); chrome.AppendChild(presenter);
        toggle.Template = new ControlTemplate(typeof(ToggleButton)) { VisualTree = chrome };
        toggle.Click += (_, _) => changed(toggle.IsChecked == true);
        return toggle;
    }

    private void CommitHex()
    {
        var text = hex.Text.Trim(); if (!text.StartsWith('#')) text = "#" + text;
        if (model is not null && LedLayers.Valid(text.ToUpperInvariant(), false)) model.SelectColorCommand.Execute(text.ToUpperInvariant());
        else if (model is not null) hex.Text = model.ColorHex;
    }

    // Lamp follow applies to every lamp, not just the first.
    private async Task SetLampFollowAsync(string follow)
    {
        if (model?.State?.Lamps is not { Count: > 0 } lamps) { model?.SetFollowCommand.Execute(follow); return; }
        foreach (var lamp in lamps) await model.Client.SendAsync(Commands.SetLamp, new { lamp = lamp.Id, follow });
    }

    private static void Place(Grid grid, UIElement element, int row, int column) { Grid.SetRow(element, row); Grid.SetColumn(element, column); grid.Children.Add(element); }
    private static T Styled<T>(T element, string style) where T : FrameworkElement { element.SetResourceReference(StyleProperty, style); return element; }
    private static DockPanel CardHeader(string text, UIElement? right)
    {
        var header = new DockPanel { Margin = new(0, 0, 0, 12), LastChildFill = true };
        if (right is not null) { DockPanel.SetDock(right, Dock.Right); header.Children.Add(right); }
        header.Children.Add(Styled(new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center }, "SectionLabel"));
        return header;
    }
    private static DockPanel ValueLabel(string text, TextBlock value, Thickness margin)
    {
        var row = new DockPanel { Margin = margin };
        value.FontSize = 13; value.FontWeight = FontWeights.SemiBold; value.Foreground = Brushes.WhiteSmoke;
        DockPanel.SetDock(value, Dock.Right); row.Children.Add(value);
        row.Children.Add(Styled(new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center }, "SectionLabel"));
        return row;
    }
    private static void AddButton(Panel panel, string text, Action action)
    {
        var button = new Button { Content = text, Padding = new(12, 7, 12, 7), Margin = new(8, 0, 0, 0) };
        button.Click += (_, _) => action(); panel.Children.Add(button);
    }
    private static ThickSlider Slider(double minimum, double maximum) => new() { Minimum = minimum, Maximum = maximum, Height = 16 };
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

