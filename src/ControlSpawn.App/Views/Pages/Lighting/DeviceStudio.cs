using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using ControlSpawn.App.Common;
using ControlSpawn.App.Controls;
using ControlSpawn.App.ViewModels;
using ControlSpawn.Core;
using ControlSpawn.Core.Ipc;
using ControlSpawn.Lighting;

namespace ControlSpawn.App.Views;

/// <summary>
/// The Per device view. Pick a part of the PC (pump and radiator, case fans, RAM, graphics card, board logo), see its
/// LEDs live and large, then choose what it does: follow the whole-case look, run its own look, or stay off.
/// While a part is selected the page's colour, effect, brightness and speed controls edit that part alone
/// (LightingViewModel.PartTarget), and touching any of them gives it its own look. LEDs can also be painted with
/// the picked colour; paint sits on the overlay layer, so unpainted LEDs keep showing the part's look.
/// </summary>
public sealed class DeviceStudio : UserControl
{
    /// <summary>A lit part: a whole device, or one zone of the board (each ARGB header is its own part).</summary>
    public sealed record Part(RgbDeviceState Device, int Zone, string Name, int First, int Count, string Key)
    {
        public bool Fans => Zone >= 0 && Device.Zones![Zone].MaxLeds > Device.Zones[Zone].MinLeds;
    }
    private enum Mode { Follow, Own, Sensor, Off }

    private static readonly Stopwatch Clock = Stopwatch.StartNew();
    private readonly DispatcherTimer timer = Motion.FrameTimer();
    private readonly SensorLighting sensorLighting = new();
    private readonly WrapPanel rail = new();
    private readonly TextBlock name = new() { FontSize = 18, FontWeight = FontWeights.SemiBold }, detail = new() { FontSize = 12, Margin = new(0, 2, 0, 0) };
    private readonly SegmentedControl mode = new() { ItemsSource = new[] { "Follow the look", "Own look", "Sensor", "Off" }, VerticalAlignment = VerticalAlignment.Center };
    private readonly PartLeds leds = new() { Height = 230, Margin = new(0, 14, 0, 12) };
    private readonly TextBlock hint = new() { TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center, FontSize = 12 };
    private readonly StackPanel tools = new() { Orientation = Orientation.Horizontal };
    private readonly Border brush = new() { Width = 18, Height = 18, CornerRadius = new(9), Margin = new(0, 0, 8, 0), BorderThickness = new(1.5), BorderBrush = new SolidColorBrush(Color.FromArgb(90, 255, 255, 255)) };
    private readonly List<(Button Card, Border Bar, TextBlock State, Part Part)> cards = new();
    private LightingViewModel? model;
    private List<Part> parts = new();
    private string? selected;
    private string signature = "";
    private bool suppress;

    /// <summary>Holds the case layout tools (the map, rotate, flip, reset), shown from "Arrange in case".</summary>
    public FrameworkElement? ArrangeTools { get; set; }

    public DeviceStudio()
    {
        var body = new StackPanel(); Content = body;
        var header = new DockPanel { Margin = new(0, 0, 0, 12) };
        var arrange = new Button { Content = "Arrange in case", MinHeight = 32, Padding = new(14, 0, 14, 0), ToolTip = "Move and rotate parts on the case map, so wave effects travel across your PC the right way." };
        arrange.Click += (_, _) =>
        {
            if (ArrangeTools is null) return;
            bool on = ArrangeTools.Visibility != Visibility.Visible;
            arrange.Content = on ? "Done arranging" : "Arrange in case";
            ArrangeTools.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
            detailCard!.Visibility = on ? Visibility.Collapsed : Visibility.Visible;
            rail.Visibility = on ? Visibility.Collapsed : Visibility.Visible;
        };
        DockPanel.SetDock(arrange, Dock.Right); header.Children.Add(arrange);
        header.Children.Add(Styled(new TextBlock { Text = "YOUR PC, PART BY PART", VerticalAlignment = VerticalAlignment.Center }, "SectionLabel"));
        body.Children.Add(header);
        body.Children.Add(rail);

        // The selected part: name and mode, its LEDs, then the paint tools.
        var top = new DockPanel();
        DockPanel.SetDock(mode, Dock.Right); top.Children.Add(mode);
        var titles = new StackPanel(); titles.Children.Add(name); titles.Children.Add(Styled(detail, "SmallMuted")); top.Children.Add(titles);
        var detailBody = new StackPanel(); detailBody.Children.Add(top); detailBody.Children.Add(leds);
        // The brush swatch and what to do, then the tools on their own line so the hint never gets cut off.
        var hintRow = new DockPanel(); DockPanel.SetDock(brush, Dock.Left); hintRow.Children.Add(brush); hintRow.Children.Add(hint);
        detailBody.Children.Add(hintRow);
        tools.Margin = new(-8, 12, 0, 0); detailBody.Children.Add(tools);
        detailCard = new Border { Child = detailBody, Padding = new(18, 16, 18, 16), Margin = new(0, 6, 0, 0), CornerRadius = new(16), BorderThickness = new(1),
            BorderBrush = new SolidColorBrush(Color.FromArgb(22, 255, 255, 255)), Background = new SolidColorBrush(Color.FromRgb(17, 18, 23)) };
        body.Children.Add(detailCard);

        AddTool("Fill with colour", "Paint every LED of this part in the picked colour", () => Paint(Current?.Count is int n ? Enumerable.Range(0, n) : [], false));
        AddTool("Gradient", "Blend from the picked colour to the last different one you used", Gradient);
        AddTool("Clear paint", "Wipe the paint so this part follows the look again", () => Paint(Current?.Count is int n ? Enumerable.Range(0, n) : [], true));

        mode.SelectionChanged += (_, _) => { if (!suppress) _ = SetModeAsync((Mode)Math.Max(0, mode.SelectedIndex)); };
        leds.Painted += (indices, erase) => { if (CurrentMode() is Mode.Off or Mode.Sensor) return; Paint(indices, erase); };
        timer.Tick += (_, _) => { if (!Motion.ReduceAnimations) Tick(); };
        Motion.WhileVisible(this, timer.Start, timer.Stop);
        DataContextChanged += (_, _) => Attach(DataContext as LightingViewModel);
        Unloaded += (_, _) => Attach(null, keep: true);
        Loaded += (_, _) => Attach(DataContext as LightingViewModel);
        // Leaving the view hands the colour and effect controls back to the whole case.
        IsVisibleChanged += (_, _) => { if (IsVisible) { signature = ""; Rebuild(); } else if (model is not null) model.PartTarget = null; };
    }
    private readonly Border detailCard;

    private void Attach(LightingViewModel? next, bool keep = false)
    {
        if (model is not null) { model.PropertyChanged -= Changed; model.Editor.PropertyChanged -= Changed; }
        if (!keep) model = next;
        if (model is not null && !keep) { model.PropertyChanged += Changed; model.Editor.PropertyChanged += Changed; }
        Rebuild();
    }
    private void Changed(object? sender, PropertyChangedEventArgs e) => Rebuild();

    private Part? Current => parts.FirstOrDefault(p => p.Key == selected) ?? parts.FirstOrDefault();

    /// <summary>The board splits into its logo and each used ARGB header; everything else is one part per device.</summary>
    public static List<Part> PartsOf(IReadOnlyList<RgbDeviceState> devices)
    {
        var list = new List<Part>(); int ram = 0;
        foreach (var d in devices.Where(d => d.Connected && d.LedCount > 0))
        {
            if (d.Kind.Contains("Motherboard", StringComparison.OrdinalIgnoreCase) && d.Zones is { Count: > 1 } zones)
            {
                for (int z = 0; z < zones.Count; z++)
                    if (zones[z].LedCount > 0) foreach (var piece in CaseMap.Pieces(d, z)) list.Add(new(d, z, piece.Name, piece.First, piece.Count, piece.Key));
                continue;
            }
            var label = d.Kind.Contains("RAM", StringComparison.OrdinalIgnoreCase) ? $"RAM {++ram}" : d.Kind.Contains("GPU", StringComparison.OrdinalIgnoreCase) ? "Graphics card"
                : d.Kind.Contains("strip", StringComparison.OrdinalIgnoreCase) ? "LED strip" : d.Name;
            list.Add(new(d, -1, label, 0, d.LedCount, d.Id));
        }
        // Fans first: they are what people look at through the glass.
        return list.OrderBy(p => p.Fans ? 0 : p.Device.Kind.Contains("GPU", StringComparison.OrdinalIgnoreCase) ? 1 : p.Device.Kind.Contains("RAM", StringComparison.OrdinalIgnoreCase) ? 2 : 3).ToList();
    }

    private IReadOnlyList<string>? Overlay(RgbDeviceState device) =>
        model?.Editor.Device?.Id == device.Id && model.Editor.Layer == "overlay" ? model.Editor.Colors : device.OverlayColors;

    // The same driver as the Whole case chips (LightingViewModel.DriverOf), so both views always agree.
    private Mode ModeOf(Part part) => model?.DriverOf(part.Device, part.Zone, part.Key).Driver switch
    {
        PartDriver.Off => Mode.Off, PartDriver.Sensor => Mode.Sensor, PartDriver.Own => Mode.Own, _ => Mode.Follow
    };
    private static PartDriver DriverFor(Mode m) => m switch { Mode.Off => PartDriver.Off, Mode.Sensor => PartDriver.Sensor, Mode.Own => PartDriver.Own, _ => PartDriver.Case };
    /// <summary>Asks the page to open the Sensor tab on this part.</summary>
    public event Action<string>? OpenSensor;
    private bool Painted(Part part)
    {
        var overlay = Overlay(part.Device);
        for (int i = part.First; overlay is not null && i < part.First + part.Count && i < overlay.Count; i++)
            if (overlay[i].Length == 9 && !overlay[i].EndsWith("00", StringComparison.Ordinal)) return true;
        return false;
    }
    private Mode CurrentMode() => Current is { } p ? ModeOf(p) : Mode.Follow;

    private void Rebuild()
    {
        if (model is null) return;
        var devices = model.State?.Lighting.Devices ?? CaseMapPreview.DemoDevices;
        parts = PartsOf(devices);
        var sig = string.Join(";", parts.Select(p => $"{p.Key}:{p.Count}:{ModeOf(p)}")) + "|" + selected;
        if (sig != signature)
        {
            signature = sig; rail.Children.Clear(); cards.Clear();
            foreach (var part in parts) rail.Children.Add(Card(part));
        }
        var current = Current;
        foreach (var (card, _, state, part) in cards)
        {
            bool on = part.Key == current?.Key;
            card.BorderBrush = new SolidColorBrush(on ? ColorUtil.Accent : Color.FromArgb(18, 255, 255, 255));
            card.Background = new SolidColorBrush(on ? Color.FromArgb(28, 255, 77, 26) : Color.FromRgb(23, 24, 29));
            var m = ModeOf(part);
            var look = model.PartLookOf(part.Key);
            state.Text = LightingWorkspace.DriverMark(DriverFor(m)) + " " + (m switch
            {
                Mode.Off => "Off", Mode.Sensor => "Sensor · " + model.DriverOf(part.Device, part.Zone, part.Key).Note,
                Mode.Own => "Own · " + EffectCatalog.For(look?.Effect ?? LightingEffect.Custom).Name, _ => "Follows the look"
            }) + (m is Mode.Follow or Mode.Own && Painted(part) ? " · painted" : "");
            state.Foreground = new SolidColorBrush(m == Mode.Follow ? Color.FromRgb(163, 166, 174) : LightingWorkspace.DriverColor(DriverFor(m)));
        }
        if (current is null) { name.Text = "No lit parts found"; detail.Text = "Start OpenRGB, or set the LED count of your ARGB headers on the Whole case view."; return; }
        if (IsVisible) model.PartTarget = current.Key;
        var currentMode = ModeOf(current);
        suppress = true; mode.SelectedIndex = (int)currentMode; suppress = false;
        name.Text = current.Name;
        detail.Text = current.Zone >= 0 ? $"{current.Device.Zones![current.Zone].Name} on {current.Device.Name} · {current.Count} LEDs" : $"{current.Device.Name} · {current.Count} LEDs";
        brush.Background = new SolidColorBrush(ColorUtil.Parse(model.ColorHex, Colors.OrangeRed));
        brush.Visibility = tools.Visibility = currentMode is Mode.Off or Mode.Sensor ? Visibility.Collapsed : Visibility.Visible;
        hint.Text = currentMode switch
        {
            Mode.Off => "This part stays dark. Choose Follow the look or Own look to light it.",
            Mode.Sensor => $"{current.Name}'s colours come from {model.DriverOf(current.Device, current.Zone, current.Key).Note}. Click Sensor to change or stop it there; Follow the look or Own look stop the sensor here.",
            Mode.Own => $"Only {current.Name} changes now: pick a colour, effect, brightness or speed. Click or drag the LEDs to paint accents, right-click to wipe them.",
            _ => $"Shows the whole-case look. Pick a colour, then click or drag the LEDs to paint accents. Pick an effect or Own look to give {current.Name} its own."
        };
        leds.Rings = current.Fans ? FanRings(current) : [];
        // Single-LED zones (the graphics card's ARGB header, top side, fan and channel 8) are named under their LED.
        leds.Labels = current.Zone < 0 && current.Device.Zones is { Count: > 1 } z && z.All(x => x.LedCount <= 1) ? z.Where(x => x.LedCount == 1).Select(x => ZoneLabel(current.Device, x.Name)).ToArray() : [];
        leds.Paintable = currentMode is Mode.Follow or Mode.Own;
        Tick();
    }

    /// <summary>
    /// What a zone actually lights. OpenRGB's ASRock names don't match the RX 9070 XT Taichi (checked live on 2026-11-02):
    /// "Channel 8" is the fans, "Fan" the cover light, "Top Side" the logo; its ARGB header has nothing plugged in.
    /// </summary>
    public static string ZoneLabel(RgbDeviceState device, string zone) => !device.Name.Contains("ASRock", StringComparison.OrdinalIgnoreCase) ? zone : zone switch
    {
        "Channel 8" => "Fans", "Fan" => "Cover light", "Top Side" => "Logo", "ARGB Header" => "Header (empty)", _ => zone
    };

    /// <summary>Fan headers chain 12-LED rings; the AIO header starts with the pump fan, then the radiator fans.</summary>
    private static string[] FanRings(Part part)
    {
        if (part.Count % 12 != 0) return [part.Name];
        int rings = part.Count / 12; bool aio = part.Name.StartsWith("Pump", StringComparison.Ordinal);
        if (part.Name == "Radiator") return Enumerable.Range(1, rings).Select(i => $"Radiator {i}").ToArray();
        return Enumerable.Range(0, rings).Select(i => aio ? i == 0 ? "Pump" : $"Radiator {i}" : $"Fan {i + 1}").ToArray();
    }

    private void Tick()
    {
        if (model?.State is not { } state) { leds.Colors = Current is { } demo ? new RgbColor[demo.Count] : []; leds.InvalidateVisual(); return; }
        var devices = state.Lighting.Devices;
        double time = Motion.ReduceAnimations ? 1.25 : Clock.Elapsed.TotalSeconds;
        var rendered = new Dictionary<string, RgbColor[]>();
        RgbColor[] Slice(Part part)
        {
            if (!rendered.TryGetValue(part.Device.Id, out var all))
                rendered[part.Device.Id] = all = part.Device.Synced || part.Device.CustomEnabled
                    ? CaseIllustration.LiveColors(model, state, part.Device, devices, time, sensorLighting, Overlay(part.Device))
                    : new RgbColor[part.Device.LedCount];
            return Enumerable.Range(part.First, part.Count).Select(i => i < all.Length ? all[i] : RgbColor.Black).ToArray();
        }
        foreach (var (_, bar, _, part) in cards)
        {
            var colors = Slice(part); var stops = new GradientStopCollection();
            int steps = Math.Min(colors.Length, 12);
            for (int i = 0; i < steps; i++) { var c = colors[i * colors.Length / steps]; stops.Add(new(Color.FromRgb(c.R, c.G, c.B), steps == 1 ? 0 : i / (double)(steps - 1))); }
            if (stops.Count == 1) stops.Add(new(stops[0].Color, 1));
            bar.Background = stops.Count == 0 ? Brushes.Black : new LinearGradientBrush(stops, 0);
        }
        if (Current is { } current) { leds.Colors = Slice(current); leds.InvalidateVisual(); }
    }

    private Button Card(Part part)
    {
        var bar = new Border { Height = 6, CornerRadius = new(3), Margin = new(0, 0, 0, 10), Background = Brushes.Black };
        var state = new TextBlock { FontSize = 11, Margin = new(0, 2, 0, 0) };
        var content = new StackPanel(); content.Children.Add(bar);
        content.Children.Add(new TextBlock { Text = part.Name, FontSize = 13, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis });
        content.Children.Add(state);
        var card = new Button { Content = content, Width = 138, Margin = new(0, 0, 8, 8), Padding = new(12, 10, 12, 10), HorizontalContentAlignment = HorizontalAlignment.Stretch, BorderThickness = new(1.5), Cursor = Cursors.Hand,
            ToolTip = $"{part.Device.Name} · {part.Count} LEDs" };
        System.Windows.Automation.AutomationProperties.SetName(card, "Part " + part.Name);
        card.Click += (_, _) => { selected = part.Key; Rebuild(); };
        cards.Add((card, bar, state, part));
        return card;
    }

    private void AddTool(string text, string tip, Action action)
    {
        var button = new Button { Content = text, MinHeight = 32, Padding = new(12, 0, 12, 0), Margin = new(8, 0, 0, 0), ToolTip = tip };
        button.Click += (_, _) => action(); tools.Children.Add(button);
    }

    /// <summary>Paints part-relative LED indices with the picked colour (or wipes them back to the look) on the overlay layer.</summary>
    private void Paint(IEnumerable<int> indices, bool erase, bool gradient = false)
    {
        if (model is null || Current is not { } part) return;
        var editor = model.Editor;
        if (editor.Device?.Id != part.Device.Id) editor.Device = editor.Devices.FirstOrDefault(d => d.Id == part.Device.Id) ?? part.Device;
        if (editor.Layer != "overlay") editor.ActivateLayer("overlay");
        editor.Color = model.ColorHex;
        editor.Paint(indices.Select(i => part.First + i), erase, gradient);
        if (!erase && model.Parameters.Devices?.GetValueOrDefault(part.Key, true) == false) _ = SetIncludedAsync(part, true);
        Rebuild();
    }

    private void Gradient()
    {
        if (model is null || Current is not { } part) return;
        model.Editor.SecondColor = model.Editor.Recent.FirstOrDefault(c => !string.Equals(c, model.ColorHex, StringComparison.OrdinalIgnoreCase)) ?? "#36D6FF";
        Paint(Enumerable.Range(0, part.Count), false, gradient: true);
    }

    private async Task SetModeAsync(Mode next)
    {
        if (model is null || Current is not { } part) return;
        if (next == Mode.Sensor) { Rebuild(); OpenSensor?.Invoke(part.Key); return; }   // the Sensor tab is where a sensor is set up
        if (next == ModeOf(part)) return;
        if (ModeOf(part) == Mode.Sensor) await model.ReleaseSensorAsync(part.Device, part.Zone, part.Key);
        switch (next)
        {
            case Mode.Follow: if (Painted(part)) Paint(Enumerable.Range(0, part.Count), true); await model.SetPartLookAsync(part.Key, clear: true); await SetIncludedAsync(part, true); break;
            case Mode.Own: await SetIncludedAsync(part, true); await model.SetPartLookAsync(part.Key); break;
            case Mode.Off: if (Painted(part)) Paint(Enumerable.Range(0, part.Count), true); await model.SetPartLookAsync(part.Key, clear: true); await SetIncludedAsync(part, false); break;
        }
        Rebuild();
    }

    private async Task SetIncludedAsync(Part part, bool on)
    {
        if (model is null) return;
        if (on && !part.Device.Synced) { model.SetDeviceCommand.Execute(new DeviceOption(part.Device.Id, part.Device.Name, "", true)); }
        var choices = new Dictionary<string, bool>(model.Parameters.Devices ?? new Dictionary<string, bool>()) { [part.Key] = on };
        if (on && part.Zone >= 0) { choices[part.Device.Id] = true; choices[SensorSources.ZoneTarget(part.Device.Id, part.Zone)] = true; }
        await model.SetParametersAsync(model.Parameters with { Devices = choices });
    }

    private static T Styled<T>(T element, string style) where T : FrameworkElement { element.SetResourceReference(StyleProperty, style); return element; }
}

/// <summary>
/// One part's LEDs drawn large: fan headers as rings of twelve (labelled pump, radiator or fan), other parts as a row
/// of LEDs. Each LED glows in its live colour; click or drag paints, right button erases.
/// </summary>
public sealed class PartLeds : FrameworkElement
{
    public RgbColor[] Colors { get; set; } = [];
    public string[] Rings { get; set; } = [];
    public string[] Labels { get; set; } = [];
    public bool Paintable { get; set; } = true;
    public event Action<int[], bool>? Painted;
    private readonly List<(Point At, double Radius)> spots = new();
    private readonly HashSet<int> stroke = new();
    private bool erasing;

    public PartLeds() { Cursor = Cursors.Pen; ClipToBounds = true; }

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight; spots.Clear();
        if (w < 40 || h < 40) return;
        dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromRgb(12, 13, 17)), null, new Rect(0, 0, w, h), 14, 14);
        int n = Colors.Length;
        if (n == 0) return;
        double dip = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        if (Rings.Length > 0)
        {
            int per = n / Rings.Length;
            double cell = Math.Min((w - 24) / Rings.Length, h - 44), radius = cell * 0.36, led = Math.Max(3, Math.Min(9, radius * Math.PI / per * 0.42));
            double left = (w - cell * Rings.Length) / 2;
            for (int r = 0; r < Rings.Length; r++)
            {
                var centre = new Point(left + cell * (r + 0.5), 14 + cell / 2);
                dc.DrawEllipse(null, new Pen(new SolidColorBrush(Color.FromRgb(36, 38, 46)), 1.2), centre, radius + led + 6, radius + led + 6);
                dc.DrawEllipse(new SolidColorBrush(Color.FromRgb(20, 21, 26)), new Pen(new SolidColorBrush(Color.FromRgb(42, 44, 52)), 1), centre, radius * 0.42, radius * 0.42);
                for (int k = 0; k < per; k++)
                {
                    double a = -Math.PI / 2 + k * 2 * Math.PI / per;
                    Led(dc, r * per + k, new(centre.X + Math.Cos(a) * radius, centre.Y + Math.Sin(a) * radius), led);
                }
                var label = new FormattedText(Rings[r], CultureInfo.CurrentCulture, FlowDirection.LeftToRight, Typefaces.Body(FontWeights.SemiBold), 11, new SolidColorBrush(Color.FromRgb(124, 128, 138)), dip);
                dc.DrawText(label, new(centre.X - label.Width / 2, centre.Y + cell / 2 + 4));
            }
            return;
        }
        // Rows of LEDs, centred; a handful of LEDs (board logo, GPU) get big dots.
        int columns = Math.Min(n, Math.Max(1, (int)((w - 24) / 26))), rows = (n + columns - 1) / columns;
        double pitch = Math.Min(Math.Min((w - 24) / columns, (h - 24) / rows), 64), size = pitch * 0.32;
        double x0 = (w - pitch * columns) / 2 + pitch / 2, y0 = (h - pitch * rows) / 2 + pitch / 2;
        for (int i = 0; i < n; i++)
        {
            var at = new Point(x0 + i % columns * pitch, y0 + i / columns * pitch);
            Led(dc, i, at, size);
            if (i >= Labels.Length) continue;
            var label = new FormattedText(Labels[i], CultureInfo.CurrentCulture, FlowDirection.LeftToRight, Typefaces.Body(FontWeights.SemiBold), 11, new SolidColorBrush(Color.FromRgb(124, 128, 138)), dip) { MaxTextWidth = pitch - 4, TextAlignment = TextAlignment.Center };
            dc.DrawText(label, new(at.X - (pitch - 4) / 2, at.Y + size * 2.6));
        }
    }

    private void Led(DrawingContext dc, int index, Point at, double radius)
    {
        var c = index < Colors.Length ? Colors[index] : RgbColor.Black;
        var colour = Color.FromRgb(c.R, c.G, c.B);
        bool dark = c.R + c.G + c.B < 24;
        if (!dark) dc.DrawEllipse(new RadialGradientBrush(Color.FromArgb(110, c.R, c.G, c.B), Color.FromArgb(0, c.R, c.G, c.B)), null, at, radius * 2.4, radius * 2.4);
        dc.DrawEllipse(new SolidColorBrush(dark ? Color.FromRgb(30, 32, 38) : colour), new Pen(new SolidColorBrush(Color.FromArgb(40, 255, 255, 255)), 1), at, radius, radius);
        spots.Add((at, Math.Max(radius * 1.6, 8)));
    }

    private int Hit(Point p)
    {
        int best = -1; double bestDistance = double.MaxValue;
        for (int i = 0; i < spots.Count; i++)
        {
            double d = (spots[i].At - p).Length;
            if (d < spots[i].Radius && d < bestDistance) { best = i; bestDistance = d; }
        }
        return best;
    }

    private void Stroke(Point p)
    {
        int hit = Hit(p);
        if (hit >= 0 && stroke.Add(hit)) Painted?.Invoke([hit], erasing);
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e) { Begin(e, false); }
    protected override void OnMouseRightButtonDown(MouseButtonEventArgs e) { Begin(e, true); }
    private void Begin(MouseButtonEventArgs e, bool erase)
    {
        if (!Paintable) return;
        erasing = erase; stroke.Clear(); CaptureMouse(); Stroke(e.GetPosition(this)); e.Handled = true;
    }
    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (IsMouseCaptured) Stroke(e.GetPosition(this));
    }
    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e) { ReleaseMouseCapture(); stroke.Clear(); }
    protected override void OnMouseRightButtonUp(MouseButtonEventArgs e) { ReleaseMouseCapture(); stroke.Clear(); e.Handled = true; }
}
