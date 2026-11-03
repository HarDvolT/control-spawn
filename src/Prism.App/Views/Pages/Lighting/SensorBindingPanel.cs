using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Prism.App.Common;
using Prism.App.Controls;
using Prism.App.ViewModels;
using Prism.Core;
using Prism.Core.Ipc;
using Prism.Lighting;

namespace Prism.App.Views;

/// <summary>
/// "Driven by a sensor": pick a target on the left, then build its binding one card per step on the right.
/// The draft lives here and survives state refreshes; Apply persists one complete validated binding.
/// Tiles are built once (or when the device list changes); the 1 Hz state only rewrites their text and colours.
/// </summary>
public sealed class SensorBindingPanel : Grid
{
    private const double Gap = 16;
    private static readonly string[] Featured = ["cpu.temperature", "gpu.temperature", "gpu.load", "gpu.power", "cpu.load", "gpu.hotspot", "ram.use", "router.ping"];
    private static readonly (string Name, string[] Colors)[] Presets = [
        ("Cool to hot", ["#0080FF", "#00D060", "#FFB020", "#FF2000"]), ("Traffic", ["#3DDC84", "#F5A524", "#FF4D4F"]),
        ("Ice", ["#0C3A4A", "#36D6FF", "#F4F5F7"]), ("Ember", ["#3A0A00", "#FF4D1A", "#FFD08A"]),
        ("Aurora", ["#063C38", "#29FCA8", "#28AACC", "#A665F6"]), ("Mono", ["#1A1C22", "#FF4D1A"])];
    private static readonly string[] StyleNames = ["Gradient", "Fill bar", "Breathing", "Brightness", "Threshold"];
    private static readonly string[] StyleHints = [
        "Every LED shows the colour for the current reading.", "LEDs fill up like a meter; a white LED marks the tip.",
        "LEDs breathe in the reading's colour, faster as it climbs.", "One colour that brightens as the reading climbs.",
        "Low colour until the middle of the range, then the high colour."];

    private sealed record TargetTile(string Id, Button Button, TextBlock State, Border Dot);
    private sealed record SourceTile(SensorSource Source, Button Button, TextBlock Value);

    private readonly StackPanel targetList = new();
    private readonly List<TargetTile> targetTiles = new();
    private readonly SpacedGrid sourceGrid = new() { Columns = 4, Gap = 10 };
    private readonly List<SourceTile> sourceTiles = new();
    private readonly Button moreSources = new() { Margin = new(0, 10, 0, 0), HorizontalAlignment = HorizontalAlignment.Left };
    private readonly TextBlock targetTitle = new(), targetState = new() { FontSize = 13, Margin = new(0, 4, 0, 0), TextWrapping = TextWrapping.Wrap }, status = new() { FontSize = 12, Margin = new(0, 6, 0, 0), TextWrapping = TextWrapping.Wrap };
    private readonly Button apply = new() { Content = "Apply", MinHeight = 38, MinWidth = 96, Padding = new(18, 0, 18, 0) };
    private readonly Button stop = new() { Content = "Stop using sensor", MinHeight = 38, Padding = new(16, 0, 16, 0), Margin = new(0, 0, 10, 0) };
    private readonly SegmentedControl styles = new() { ItemsSource = StyleNames, SelectedIndex = 0 };
    private readonly TextBlock styleHint = new() { Margin = new(0, 10, 0, 0), TextWrapping = TextWrapping.Wrap };
    private readonly SensorLookPreview look = new() { Height = 92, Margin = new(0, 6, 0, 0) };
    private readonly UniformGrid presetGrid = new() { Columns = 3 };
    private readonly WrapPanel stopRow = new() { Margin = new(0, 4, 0, 0) };
    private readonly Popup stopEditor = new() { StaysOpen = false, AllowsTransparency = true, Placement = PlacementMode.Bottom, VerticalOffset = 8 };
    private readonly ColorWheel stopWheel = new() { Width = 150, Height = 150, MinWidth = 0, MinHeight = 0, RingMode = true };
    private readonly RangeTrack rangeTrack = new() { Margin = new(0, 6, 0, 0) };
    private readonly NumberStepper low = new(), high = new(), threshold = new();
    private readonly TextBlock nowReading = new() { FontSize = 13, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center };
    private readonly CheckBox autoLearn = new() { Content = "Learn the range from what the sensor does", Margin = new(0, 16, 0, 0) };
    private readonly ThickSlider smoothing = new() { Minimum = 0, Maximum = 5, Step = 0.1, Height = 14, Glow = false };
    private readonly TextBlock smoothingValue = new();
    private readonly CheckBox alert = new() { Content = "Red pulse above", VerticalAlignment = VerticalAlignment.Center, Margin = new(0, 0, 12, 0) };
    private readonly Grid stepRows = new();
    private readonly Grid layout = new();
    private readonly FrameworkElement targetsCard, steps;
    private readonly SensorLighting session = new();
    private readonly Stopwatch clock = Stopwatch.StartNew();
    private LightingViewModel? model;
    private string? target;
    private bool showAllSources, suppress;
    private string targetSignature = "", sourceSignature = "";
    private int selectedStop;

    // The draft being edited for the selected target; the rest of it lives in the controls.
    private string source = "gpu.temperature";
    private List<PaletteStop> stops = SensorLighting.DefaultPalette.ToList();

    public SensorBindingPanel()
    {
        Children.Add(layout);
        layout.ColumnDefinitions.Add(new() { Width = new GridLength(4, GridUnitType.Star) });
        layout.ColumnDefinitions.Add(new() { Width = new GridLength(Gap) });
        layout.ColumnDefinitions.Add(new() { Width = new GridLength(8, GridUnitType.Star) });
        layout.RowDefinitions.Add(new() { Height = GridLength.Auto }); layout.RowDefinitions.Add(new() { Height = GridLength.Auto });

        targetsCard = new GlassCard { Content = BuildTargets(), VerticalAlignment = VerticalAlignment.Top };
        layout.Children.Add(targetsCard);
        var right = new StackPanel(); steps = right; Grid.SetColumn(right, 2); layout.Children.Add(right);
        right.Children.Add(new GlassCard { Content = BuildSummary() });
        right.Children.Add(Spaced(new GlassCard { Content = BuildSourceCard() }));
        right.Children.Add(Spaced(new GlassCard { Content = BuildLookCard() }));
        stepRows.ColumnDefinitions.Add(new()); stepRows.ColumnDefinitions.Add(new() { Width = new GridLength(Gap) }); stepRows.ColumnDefinitions.Add(new());
        stepRows.RowDefinitions.Add(new() { Height = GridLength.Auto }); stepRows.RowDefinitions.Add(new() { Height = GridLength.Auto });
        stepRows.Children.Add(new GlassCard { Content = BuildColourCard() });
        stepRows.Children.Add(new GlassCard { Content = BuildRangeCard() });
        right.Children.Add(Spaced(stepRows));
        right.Children.Add(Spaced(new GlassCard { Content = BuildExtrasCard() }));
        DraftChanged();

        DataContextChanged += (_, _) => { if (model is not null) model.PropertyChanged -= Changed; model = DataContext as LightingViewModel; if (model is not null) model.PropertyChanged += Changed; Refresh(); };
        Loaded += (_, _) => { if (model is not null) { model.PropertyChanged -= Changed; model.PropertyChanged += Changed; } Refresh(); };
        Unloaded += (_, _) => { if (model is not null) model.PropertyChanged -= Changed; };
        SizeChanged += (_, e) => { if (e.WidthChanged) Reflow(e.NewSize.Width); };
        steps.SizeChanged += (_, e) => { if (e.WidthChanged) PairCards(e.NewSize.Width); };
    }

    private static FrameworkElement Spaced(FrameworkElement element) { element.Margin = new(element.Margin.Left, Gap, element.Margin.Right, 0); return element; }

    private void Reflow(double width)
    {
        // Narrow windows stack the targets above the steps.
        bool narrow = width < 980;
        Grid.SetColumn(steps, narrow ? 0 : 2); Grid.SetRow(steps, narrow ? 1 : 0); Grid.SetColumnSpan(steps, narrow ? 3 : 1);
        Grid.SetColumnSpan(targetsCard, narrow ? 3 : 1); steps.Margin = new(0, narrow ? Gap : 0, 0, 0);
        sourceGrid.Columns = width < 560 ? 2 : 4;
    }

    /// <summary>Colours and range sit side by side while each gets about 350 px, else stack (rows size to their own card).</summary>
    private void PairCards(double width)
    {
        bool stack = width < 720;
        var range = (FrameworkElement)stepRows.Children[1];
        stepRows.ColumnDefinitions[1].Width = new GridLength(stack ? 0 : Gap);
        stepRows.ColumnDefinitions[2].Width = stack ? new GridLength(0) : new GridLength(1, GridUnitType.Star);
        Grid.SetColumn(range, stack ? 0 : 2); Grid.SetRow(range, stack ? 1 : 0); range.Margin = new(0, stack ? Gap : 0, 0, 0);
    }

    // ---- Cards -------------------------------------------------------------------------------------------

    private StackPanel BuildTargets()
    {
        var body = new StackPanel();
        body.Children.Add(CardHeader("LIGHTS TO DRIVE", null));
        body.Children.Add(Styled(new TextBlock { Text = "Pick a device or part. Everything else keeps the normal effect.", TextWrapping = TextWrapping.Wrap, Margin = new(0, -4, 0, 12) }, "SmallMuted"));
        body.Children.Add(targetList);
        return body;
    }

    private DockPanel BuildSummary()
    {
        var row = new DockPanel();
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        Styled(stop, "GhostButton"); Styled(apply, "PrimaryButton");
        stop.Click += (_, _) => _ = RemoveAsync(); apply.Click += (_, _) => _ = ApplyAsync();
        AutomationProperties.SetName(apply, "Apply sensor binding"); AutomationProperties.SetName(stop, "Stop using sensor");
        buttons.Children.Add(stop); buttons.Children.Add(apply);
        DockPanel.SetDock(buttons, Dock.Right); row.Children.Add(buttons);
        var text = new StackPanel { Margin = new(0, 0, 16, 0), VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(Styled(targetTitle, "CardTitle")); text.Children.Add(targetState); text.Children.Add(status);
        row.Children.Add(text);
        return row;
    }

    private StackPanel BuildSourceCard()
    {
        var body = new StackPanel();
        body.Children.Add(CardHeader("SENSOR", null));
        body.Children.Add(sourceGrid);
        Styled(moreSources, "TextAction");
        moreSources.Click += (_, _) => { showAllSources = !showAllSources; LayoutSources(); };
        body.Children.Add(moreSources);
        return body;
    }

    private StackPanel BuildLookCard()
    {
        var body = new StackPanel();
        styles.HorizontalAlignment = HorizontalAlignment.Right;
        body.Children.Add(CardHeader("LOOK", styles));
        styles.SelectionChanged += (_, _) => { if (styles.SelectedIndex >= 0) styleHint.Text = StyleHints[styles.SelectedIndex]; DraftChanged(); };
        AutomationProperties.SetName(styles, "Sensor style");
        body.Children.Add(look);
        body.Children.Add(Styled(styleHint, "SmallMuted"));
        styleHint.Text = StyleHints[0];
        return body;
    }

    private StackPanel BuildColourCard()
    {
        var body = new StackPanel();
        body.Children.Add(CardHeader("COLOURS", null));
        foreach (var (name, colors) in Presets)
        {
            var bar = new Border { Height = 8, CornerRadius = new(4), Margin = new(0, 0, 0, 7), Background = Gradient(Even(colors)) };
            var content = new StackPanel(); content.Children.Add(bar); content.Children.Add(new TextBlock { Text = name, FontSize = 12, FontWeight = FontWeights.SemiBold });
            var preset = new Button { Content = content, Margin = new(0, 0, 8, 8), Padding = new(10, 9, 10, 8), HorizontalContentAlignment = HorizontalAlignment.Stretch };
            AutomationProperties.SetName(preset, "Palette " + name);
            preset.Click += (_, _) => { stops = Even(colors).ToList(); selectedStop = 0; BuildStops(); DraftChanged(); };
            presetGrid.Children.Add(preset);
        }
        body.Children.Add(presetGrid);
        body.Children.Add(Styled(new TextBlock { Text = "LOW TO HIGH", Margin = new(0, 10, 0, 8) }, "SectionLabel"));
        body.Children.Add(stopRow);
        body.Children.Add(Styled(new TextBlock { Text = "Click a colour to change it.", Margin = new(0, 4, 0, 0) }, "SmallMuted"));

        var editor = new StackPanel { Margin = new(14) };
        editor.Children.Add(stopWheel);
        stopWheel.ColorPicked += hex => { if (selectedStop < stops.Count && LedLayers.Valid(hex.ToUpperInvariant(), false)) { stops[selectedStop] = stops[selectedStop] with { Color = hex.ToUpperInvariant() }; RecolourStops(); DraftChanged(); } };
        stopEditor.Child = new Border { Child = editor, CornerRadius = new(16), Background = new SolidColorBrush(Color.FromRgb(23, 24, 29)), BorderBrush = new SolidColorBrush(Color.FromArgb(40, 255, 255, 255)), BorderThickness = new(1) };
        BuildStops();
        return body;
    }

    private StackPanel BuildRangeCard()
    {
        var body = new StackPanel();
        body.Children.Add(CardHeader("RANGE", nowReading));
        rangeTrack.RangeChanged += (a, b) => { if (suppress) return; suppress = true; low.Value = a; high.Value = b; suppress = false; DraftChanged(); };
        AutomationProperties.SetName(rangeTrack, "Sensor range");
        body.Children.Add(rangeTrack);
        var ends = new DockPanel { Margin = new(0, 12, 0, 0) };
        var highBox = Labelled("HIGH", high); DockPanel.SetDock(highBox, Dock.Right); ends.Children.Add(highBox);
        ends.Children.Add(Labelled("LOW", low));
        body.Children.Add(ends);
        low.ValueChanged += () => { if (!suppress) { rangeTrack.SetRange(low.Value, high.Value); DraftChanged(); } };
        high.ValueChanged += () => { if (!suppress) { rangeTrack.SetRange(low.Value, high.Value); DraftChanged(); } };
        AutomationProperties.SetName(low, "Range low"); AutomationProperties.SetName(high, "Range high");
        autoLearn.Click += (_, _) => DraftChanged();
        body.Children.Add(autoLearn);
        return body;
    }

    private UniformGrid BuildExtrasCard()
    {
        var grid = new UniformGrid { Columns = 2 };
        var smooth = new StackPanel { Margin = new(0, 0, Gap, 0) };
        smooth.Children.Add(ValueLabel("SMOOTHING", smoothingValue));
        smoothing.ValueChanged += (_, _) => { smoothingValue.Text = smoothing.Value < 0.05 ? "Instant" : $"{smoothing.Value:0.0} s"; DraftChanged(); };
        AutomationProperties.SetName(smoothing, "Smoothing");
        smooth.Children.Add(smoothing);
        smooth.Children.Add(Styled(new TextBlock { Text = "Eases quick spikes so the lights don't flicker.", Margin = new(0, 10, 0, 0), TextWrapping = TextWrapping.Wrap }, "SmallMuted"));
        grid.Children.Add(smooth);

        var warn = new StackPanel { Margin = new(Gap, 0, 0, 0) };
        warn.Children.Add(Styled(new TextBlock { Text = "ALERT", Margin = new(0, 0, 0, 8) }, "SectionLabel"));
        var row = new DockPanel(); row.Children.Add(alert); row.Children.Add(threshold);
        alert.Click += (_, _) => { threshold.IsEnabled = alert.IsChecked == true; DraftChanged(); };
        threshold.ValueChanged += () => { if (!suppress) DraftChanged(); };
        AutomationProperties.SetName(threshold, "Alert threshold");
        warn.Children.Add(row);
        warn.Children.Add(Styled(new TextBlock { Text = "Pulses red over the look while the raw reading is at or above this.", Margin = new(0, 8, 0, 0), TextWrapping = TextWrapping.Wrap }, "SmallMuted"));
        grid.Children.Add(warn);
        return grid;
    }

    // ---- Palette stops -----------------------------------------------------------------------------------

    private void BuildStops()
    {
        stopRow.Children.Clear();
        for (int i = 0; i < stops.Count; i++)
        {
            int index = i;
            var swatch = new ColorSwatch { Width = 40, Height = 34, Margin = new(0, 0, 8, 6) };
            swatch.Click += (_, _) => { selectedStop = index; RecolourStops(); stopWheel.ColorHex = stops[index].Color; stopEditor.PlacementTarget = swatch; stopEditor.IsOpen = true; };
            stopRow.Children.Add(swatch);
        }
        var add = new Button { Content = "+", Width = 34, Height = 34, Margin = new(0, 0, 8, 6), FontSize = 15, Padding = new(0), ToolTip = "Add a colour", IsEnabled = stops.Count < 6 };
        var remove = new Button { Content = "−", Width = 34, Height = 34, Margin = new(0, 0, 0, 6), FontSize = 15, Padding = new(0), ToolTip = "Remove the selected colour", IsEnabled = stops.Count > 2 };
        AutomationProperties.SetName(add, "Add colour"); AutomationProperties.SetName(remove, "Remove selected colour");
        add.Click += (_, _) =>
        {
            // Split the widest gap with the colour already shown there, then space the stops evenly again.
            int gap = Enumerable.Range(0, stops.Count - 1).MaxBy(i => stops[i + 1].Position - stops[i].Position);
            var c = Palettes.Sample(stops, (stops[gap].Position + stops[gap + 1].Position) / 2);
            var colors = stops.Select(s => s.Color).ToList(); colors.Insert(gap + 1, $"#{c.R:X2}{c.G:X2}{c.B:X2}");
            stops = Even(colors).ToList(); selectedStop = gap + 1; BuildStops(); DraftChanged();
        };
        remove.Click += (_, _) => { stops = Even(stops.Where((_, i) => i != selectedStop).Select(s => s.Color).ToArray()).ToList(); selectedStop = Math.Min(selectedStop, stops.Count - 1); BuildStops(); DraftChanged(); };
        stopRow.Children.Add(add); stopRow.Children.Add(remove);
        RecolourStops();
    }

    private void RecolourStops()
    {
        var swatches = stopRow.Children.OfType<ColorSwatch>().ToArray();
        for (int i = 0; i < swatches.Length && i < stops.Count; i++)
        {
            swatches[i].ColorHex = stops[i].Color; swatches[i].SelectedHex = i == selectedStop ? stops[i].Color : "";
            AutomationProperties.SetName(swatches[i], $"Colour {i + 1} of {stops.Count}, {stops[i].Color}");
        }
    }

    private static PaletteStop[] Even(IReadOnlyList<string> colors) => colors.Select((c, i) => new PaletteStop(Math.Round(i / (double)Math.Max(1, colors.Count - 1), 4), c)).ToArray();

    // ---- State -------------------------------------------------------------------------------------------

    private void Changed(object? sender, PropertyChangedEventArgs e) { if (e.PropertyName is null or "" or nameof(LightingViewModel.State)) Refresh(); }

    private void Refresh()
    {
        if (model?.State is not { } state) return;
        var targets = Targets(state);
        var nextTargets = string.Join(";", targets.Select(t => $"{t.Id}:{t.Name}:{t.Indent}"));
        if (nextTargets != targetSignature) { targetSignature = nextTargets; BuildTargetTiles(targets); }
        var available = SensorSources.Available(state).ToList();
        var nextSources = string.Join(";", available.Select(s => s.Id));
        if (nextSources != sourceSignature) { sourceSignature = nextSources; BuildSourceTiles(available); }
        if (target is null || !targets.Any(t => t.Id == target)) Select(targets.FirstOrDefault(t => state.Lighting.Bindings?.ContainsKey(t.Id) == true).Id ?? targets.FirstOrDefault().Id);
        Update(state);
    }

    /// <summary>Text, values and colours only; no elements are created here.</summary>
    private void Update(StateMessage state)
    {
        var bindings = state.Lighting.Bindings;
        foreach (var tile in targetTiles)
        {
            bool on = tile.Id == target;
            var b = bindings?.GetValueOrDefault(tile.Id);
            Highlight(tile.Button, on);
            tile.State.Text = b is null ? "Normal effect" : $"{ShortName(b.Source)} · {Reading(state, tile.Id, b).Text}";
            tile.State.Foreground = new SolidColorBrush(b is null ? ColorUtil.TextTertiary : Color.FromRgb(255, 138, 96));
            tile.Dot.Background = b is null ? Brushes.Transparent : new SolidColorBrush(ColorUtil.Accent);
        }
        foreach (var tile in sourceTiles)
        {
            tile.Value.Text = Format(SensorSources.Read(state, tile.Source.Id), tile.Source.Unit);
            Highlight(tile.Button, tile.Source.Id == source);
        }
        var raw = SensorSources.Read(state, source);
        nowReading.Text = "Now " + Format(raw, Source(state)?.Unit ?? "");
        rangeTrack.Reading = raw; look.Reading = raw;
        UpdateSummary(state);
    }

    private static void Highlight(Button button, bool on)
    {
        button.BorderBrush = new SolidColorBrush(on ? ColorUtil.Accent : Color.FromArgb(18, 255, 255, 255));
        button.Background = new SolidColorBrush(on ? Color.FromArgb(28, 255, 77, 26) : Color.FromRgb(23, 24, 29));
    }

    private void UpdateSummary(StateMessage state)
    {
        if (target is null) return;
        targetTitle.Text = Targets(state).FirstOrDefault(t => t.Id == target).Name ?? target;
        var saved = state.Lighting.Bindings?.GetValueOrDefault(target);
        string line; Color colour;
        if (saved is null) { line = $"Follows the normal effect. Apply to let {ShortName(source)} drive it."; colour = ColorUtil.TextSecondary; }
        else
        {
            var reading = Reading(state, target, saved);
            line = reading.Reason ?? $"Driven by {ShortName(saved.Source)} · {reading.Text}" + (reading.Alert ? " · alert" : "");
            colour = reading.Reason is null ? ColorUtil.TextPrimary : ColorUtil.Fair;
            if (Draft() is { } draft && !Same(draft, saved)) { line += "  ·  unsaved changes"; colour = ColorUtil.Fair; }
        }
        targetState.Text = line; targetState.Foreground = new SolidColorBrush(colour);
        stop.Visibility = saved is null ? Visibility.Collapsed : Visibility.Visible;
        bool dirty = saved is null || Draft() is not { } d || !Same(d, saved);
        apply.Content = saved is null ? "Apply" : dirty ? "Apply changes" : "Applied";
        apply.IsEnabled = dirty;
    }

    private (string Text, bool Alert, string? Reason) Reading(StateMessage state, string id, SensorBinding b)
    {
        var sample = state.Lighting.BindingReadings?.GetValueOrDefault(id) ?? session.Observe(id, b, SensorSources.Read(state, b.Source), clock.Elapsed.TotalSeconds);
        var unit = SensorSources.Available(state).FirstOrDefault(s => s.Id == b.Source)?.Unit ?? "";
        return (Format(sample.Value, unit), sample.Alert, sample.Reason is null ? null : $"{ShortName(b.Source)} has no reading, so the normal effect shows");
    }

    private SensorSource? Source(StateMessage? state) => (state is null ? SensorSources.All : SensorSources.Available(state)).FirstOrDefault(s => s.Id == source);

    // ---- Targets -----------------------------------------------------------------------------------------

    private readonly record struct Target(string Id, string Name, string Detail, DeviceIconKind Icon, bool Indent);

    /// <summary>Devices, then their parts (only for devices with more than one lit part), then lamps.</summary>
    private static List<Target> Targets(StateMessage state)
    {
        var list = new List<Target>(); int ram = 0;
        foreach (var d in state.Lighting.Devices)
        {
            var (name, icon) = d.Kind.Contains("RAM", StringComparison.OrdinalIgnoreCase) ? ($"RAM {++ram}", DeviceIconKind.Ram)
                : d.Kind.Contains("GPU", StringComparison.OrdinalIgnoreCase) ? ("Graphics card", DeviceIconKind.Gpu)
                : d.Kind.Contains("Motherboard", StringComparison.OrdinalIgnoreCase) ? ("Motherboard", DeviceIconKind.Board)
                : d.Kind.Contains("strip", StringComparison.OrdinalIgnoreCase) ? ("LED strip", DeviceIconKind.Strip) : (d.Name, DeviceIconKind.Board);
            var zones = d.Zones ?? [];
            var parts = Enumerable.Range(0, zones.Count).Where(z => zones[z].LedCount > 0).ToList();   // a 0-LED header is dark, nothing to drive
            list.Add(new(d.Id, parts.Count > 1 ? name + " · everything" : name, d.Name, icon, false));
            if (parts.Count > 1)
                foreach (var z in parts)
                    foreach (var piece in CaseMap.Pieces(d, z))   // the AIO header offers its pump and radiator apart
                        list.Add(new(piece.Key, piece.Name, $"{zones[z].Name} · {piece.Count} LEDs", piece.Name == "Radiator" ? DeviceIconKind.Fan : PartIcon(d, z), true));
        }
        foreach (var lamp in state.Lamps ?? []) if (lamp.Id is { } id) list.Add(new(id, lamp.Name ?? "Lamp", "Mi Home lamp", DeviceIconKind.Lamp, false));
        return list;
    }

    /// <summary>The first lit ARGB header carries the pump and radiator, the second the case fans (as <c>CaseMap.HeaderDefault</c>).</summary>
    private static int HeaderOrder(RgbDeviceState d, int zone) =>
        Enumerable.Range(0, d.Zones!.Count).Where(z => d.Zones[z].MaxLeds > d.Zones[z].MinLeds && d.Zones[z].LedCount > 0).ToList().IndexOf(zone);
    // The same part names as the Whole case and Per device views.
    private static string PartName(RgbDeviceState d, int zone) => CaseMap.PartName(d, zone);
    private static DeviceIconKind PartIcon(RgbDeviceState d, int zone) => HeaderOrder(d, zone) switch { 0 => DeviceIconKind.Pump, 1 => DeviceIconKind.Fan, > 1 => DeviceIconKind.Strip, _ => DeviceIconKind.Board };

    private void BuildTargetTiles(List<Target> targets)
    {
        targetList.Children.Clear(); targetTiles.Clear();
        foreach (var t in targets)
        {
            var dot = new Border { Width = 7, Height = 7, CornerRadius = new(4), VerticalAlignment = VerticalAlignment.Center, Margin = new(8, 0, 0, 0) };
            var state = new TextBlock { FontSize = 12, Margin = new(0, 2, 0, 0), TextTrimming = TextTrimming.CharacterEllipsis };
            var row = new DockPanel();
            var icon = new DeviceIcon { Kind = t.Icon, Width = 22, Height = 22, Margin = new(0, 0, 12, 0), VerticalAlignment = VerticalAlignment.Center };
            DockPanel.SetDock(icon, Dock.Left); row.Children.Add(icon);
            DockPanel.SetDock(dot, Dock.Right); row.Children.Add(dot);
            var text = new StackPanel();
            text.Children.Add(new TextBlock { Text = t.Name, FontSize = 13, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis });
            text.Children.Add(state); row.Children.Add(text);
            var button = new Button { Content = row, Padding = new(12, 9, 12, 9), Margin = new(t.Indent ? 22 : 0, 0, 0, 8), HorizontalContentAlignment = HorizontalAlignment.Stretch, BorderThickness = new(1.5), ToolTip = t.Detail };
            AutomationProperties.SetName(button, "Sensor target " + t.Name);
            var id = t.Id; button.Click += (_, _) => { Select(id); if (model?.State is { } s) Update(s); };
            targetList.Children.Add(button); targetTiles.Add(new(t.Id, button, state, dot));
        }
    }

    /// <summary>Selects one part as the target, as clicking it in the list does (used by "open in Sensor" elsewhere).</summary>
    public void ShowTarget(string id) { Select(id); if (model?.State is { } s) Update(s); }

    private void Select(string? id)
    {
        target = id; stopEditor.IsOpen = false; status.Text = "";
        if (id is null) return;
        // A target with no binding keeps the sensor picked last, with that sensor's usual range.
        LoadDraft(model?.State?.Lighting.Bindings?.GetValueOrDefault(id) ?? DefaultBinding(source));
    }

    // ---- Sources -----------------------------------------------------------------------------------------

    private void BuildSourceTiles(List<SensorSource> available)
    {
        sourceTiles.Clear();
        foreach (var s in available)
        {
            var value = new TextBlock { FontSize = 20, FontWeight = FontWeights.SemiBold, Margin = new(0, 4, 0, 0) };
            value.SetResourceReference(TextBlock.FontFamilyProperty, "NumberFont");
            var content = new StackPanel();
            content.Children.Add(new TextBlock { Text = ShortName(s.Id, s), FontSize = 12, Foreground = new SolidColorBrush(ColorUtil.TextSecondary), TextTrimming = TextTrimming.CharacterEllipsis });
            content.Children.Add(value);
            var button = new Button { Content = content, Padding = new(12, 10, 12, 10), HorizontalContentAlignment = HorizontalAlignment.Stretch, BorderThickness = new(1.5), ToolTip = $"{s.Group} · {s.Name}" };
            AutomationProperties.SetName(button, "Sensor " + s.Name);
            var id = s.Id; button.Click += (_, _) => { if (id != source) LoadDraft(DraftFor(id)); };
            sourceTiles.Add(new(s, button, value));
        }
        LayoutSources();
    }

    private void LayoutSources()
    {
        // SpacedGrid lays out every child, so only the visible tiles are added; the tiles themselves are kept.
        sourceGrid.Children.Clear();
        var shown = showAllSources ? sourceTiles : sourceTiles.Where(t => Featured.Contains(t.Source.Id) || t.Source.Id == source)
            .OrderBy(t => Array.IndexOf(Featured, t.Source.Id) is var i && i < 0 ? 99 : i).ToList();
        foreach (var tile in shown) sourceGrid.Children.Add(tile.Button);
        moreSources.Content = showAllSources ? "Show fewer sensors" : $"All sensors ({sourceTiles.Count})";
    }

    /// <summary>Switching sensor keeps the look, colours and extras, and resets the range (and alert level) to the new sensor's.</summary>
    private SensorBinding DraftFor(string id)
    {
        var d = DefaultBinding(id); var current = Draft() ?? d;
        return current with { Source = id, Min = d.Min, Max = d.Max, Alert = current.Alert is null ? null : new(DefaultAlert(Lookup(id))) };
    }

    private SensorSource Lookup(string id) =>
        (model?.State is { } state ? SensorSources.Available(state) : SensorSources.All).FirstOrDefault(s => s.Id == id) ?? new SensorSource(id, "Fans", id, "RPM", 0, 3000);
    private static double DefaultAlert(SensorSource s) => Math.Round(s.Max * 0.95);
    private static double StepFor(SensorSource s) => s.Unit switch { "MHz" or "RPM" => 50, "W" or "ms" => 5, "Mbps" => 10, _ => 1 };
    private SensorBinding DefaultBinding(string id) { var s = Lookup(id); return new SensorBinding(id, s.Min, s.Max); }

    // ---- Draft -------------------------------------------------------------------------------------------

    private void LoadDraft(SensorBinding b)
    {
        suppress = true;
        source = b.Source;
        var s = Lookup(b.Source);
        double step = StepFor(s);
        foreach (var box in new[] { low, high, threshold }) { box.Unit = s.Unit; box.Step = step; }
        look.Unit = s.Unit; rangeTrack.Step = step;
        // The track spans the sensor's usual range with some headroom, and always includes the chosen ends.
        double pad = (s.Max - s.Min) * 0.25;
        double lo = Math.Min(b.Min, s.Min >= 0 ? Math.Max(0, s.Min - pad) : s.Min - pad), hi = Math.Max(b.Max, s.Max + pad);
        rangeTrack.Configure(lo, hi, b.Min, b.Max);
        low.Minimum = high.Minimum = threshold.Minimum = Math.Min(lo, -100); low.Maximum = high.Maximum = threshold.Maximum = Math.Max(hi * 2, 100);
        low.Value = b.Min; high.Value = b.Max;
        styles.SelectedIndex = (int)b.Style; styleHint.Text = StyleHints[(int)b.Style];
        stops = (b.Palette ?? SensorLighting.DefaultPalette).ToList(); selectedStop = 0;
        smoothing.Value = b.Smoothing; smoothingValue.Text = b.Smoothing < 0.05 ? "Instant" : $"{b.Smoothing:0.0} s";
        autoLearn.IsChecked = b.AutoLearn;
        alert.IsChecked = b.Alert is not null; threshold.IsEnabled = b.Alert is not null;
        threshold.Value = b.Alert?.Threshold ?? DefaultAlert(s);
        suppress = false;
        LayoutSources(); BuildStops(); DraftChanged();
        if (model?.State is { } state) Update(state);
    }

    private SensorBinding? Draft() => styles.SelectedIndex < 0 ? null :
        new SensorBinding(source, low.Value, high.Value, (SensorStyle)styles.SelectedIndex, stops.ToArray(), Math.Round(smoothing.Value, 1),
            alert.IsChecked == true ? new(threshold.Value) : null, autoLearn.IsChecked == true, Math.Min(2, Math.Max(0, high.Value - low.Value)));

    private static bool Same(SensorBinding a, SensorBinding b) =>
        a.Source == b.Source && a.Min == b.Min && a.Max == b.Max && a.Style == b.Style && Math.Abs(a.Smoothing - b.Smoothing) < 0.01 && a.Alert == b.Alert && a.AutoLearn == b.AutoLearn &&
        (a.Palette ?? SensorLighting.DefaultPalette).SequenceEqual(b.Palette ?? SensorLighting.DefaultPalette);

    private void DraftChanged()
    {
        if (suppress) return;
        look.Binding = Draft(); rangeTrack.Stops = stops;
        if (model?.State is { } state) UpdateSummary(state);
    }

    private async Task ApplyAsync()
    {
        if (model is null || target is not { } id || Draft() is not { } b) return;
        try
        {
            SensorSources.Validate(b);
            var response = await model.Client.SendAsync(Commands.SetSensorBinding, new { target = id, b.Source, b.Min, b.Max, b.Style, b.Palette, b.Smoothing, b.Alert, b.AutoLearn, b.Hysteresis });
            Report(response.Ok ? $"Saved. {ShortName(b.Source)} now drives {targetTitle.Text}." : response.Error, response.Ok);
        }
        catch (ArgumentException) { Report(b.Min >= b.Max ? "The low end must be below the high end." : "That binding isn't valid. Check the range and colours.", false); }
    }

    private async Task RemoveAsync()
    {
        if (model is null || target is not { } id) return;
        var response = await model.Client.SendAsync(Commands.SetSensorBinding, new { target = id, source = (string?)null });
        Report(response.Ok ? $"{targetTitle.Text} is back on the normal effect." : response.Error, response.Ok);
    }

    private void Report(string? text, bool ok) { status.Text = text ?? ""; status.Foreground = new SolidColorBrush(ok ? ColorUtil.TextTertiary : ColorUtil.Poor); }

    // ---- Helpers -----------------------------------------------------------------------------------------

    private static string ShortName(string id, SensorSource? s = null) => id switch
    {
        "gpu.temperature" => "GPU temp", "gpu.hotspot" => "GPU hotspot", "gpu.memoryJunction" => "VRAM junction", "gpu.load" => "GPU usage",
        "gpu.power" => "GPU power", "gpu.clock" => "GPU clock", "gpu.vram" => "VRAM use", "gpu.fan" => "GPU fan",
        "cpu.temperature" => "CPU temp", "cpu.load" => "CPU load", "cpu.power" => "CPU power", "cpu.clock" => "CPU clock", "cpu.coreMax" => "Busiest core",
        "ram.use" => "RAM use", "ram.temperature" => "RAM temp", "board.temperature" => "Board temp", "network.down" => "Download", "network.up" => "Upload",
        "disk.activity" => "Disk activity", "disk.temperature" => "Disk temp", "router.signal" => "5G signal", "router.ping" => "Ping",
        _ => s?.Name ?? SensorSources.All.FirstOrDefault(x => x.Id == id)?.Name ?? (id.StartsWith("fan.", StringComparison.Ordinal) ? "Fan " + id[4..] : id)
    };

    internal static string Format(double? value, string unit) => value is not { } v ? "—" : unit switch
    {
        "%" => $"{v:0}%",
        "Mbps" when v < 10 => $"{v:0.0} Mbps",
        _ => string.Format(CultureInfo.CurrentCulture, "{0:#,0} {1}", v, unit).TrimEnd()
    };

    private static LinearGradientBrush Gradient(IReadOnlyList<PaletteStop> stops)
    {
        var brush = new LinearGradientBrush { StartPoint = new(0, 0), EndPoint = new(1, 0) };
        foreach (var s in stops) brush.GradientStops.Add(new(ColorUtil.Parse(s.Color, Colors.Black), s.Position));
        return brush;
    }

    private static T Styled<T>(T element, string style) where T : FrameworkElement { element.SetResourceReference(StyleProperty, style); return element; }
    private static DockPanel CardHeader(string text, UIElement? right)
    {
        var header = new DockPanel { Margin = new(0, 0, 0, 12), LastChildFill = true };
        if (right is not null) { DockPanel.SetDock(right, Dock.Right); header.Children.Add(right); }
        header.Children.Add(Styled(new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center }, "SectionLabel"));
        return header;
    }
    private static DockPanel ValueLabel(string text, TextBlock value)
    {
        var row = new DockPanel { Margin = new(0, 0, 0, 10) };
        value.FontSize = 13; value.FontWeight = FontWeights.SemiBold; value.Foreground = Brushes.WhiteSmoke;
        DockPanel.SetDock(value, Dock.Right); row.Children.Add(value);
        row.Children.Add(Styled(new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center }, "SectionLabel"));
        return row;
    }
    private static StackPanel Labelled(string text, UIElement child)
    {
        var panel = new StackPanel();
        panel.Children.Add(Styled(new TextBlock { Text = text, Margin = new(2, 0, 0, 6) }, "SectionLabel"));
        panel.Children.Add(child);
        return panel;
    }
}

/// <summary>A compact "− 85 °C +" stepper; the number is typed or nudged, and clamped to the range.</summary>
public sealed class NumberStepper : Border
{
    private readonly TextBox box = new() { MinWidth = 44, Background = Brushes.Transparent, BorderThickness = new(0), Foreground = Brushes.WhiteSmoke, FontSize = 13, FontWeight = FontWeights.SemiBold, HorizontalContentAlignment = HorizontalAlignment.Center, VerticalContentAlignment = VerticalAlignment.Center };
    private readonly TextBlock unit = new() { FontSize = 12, VerticalAlignment = VerticalAlignment.Center, Margin = new(0, 0, 4, 0) };
    private double value;
    public double Minimum { get; set; } = double.MinValue;
    public double Maximum { get; set; } = double.MaxValue;
    public double Step { get; set; } = 1;
    public string Unit { get => unit.Text; set => unit.Text = value; }
    public event Action? ValueChanged;
    public double Value
    {
        get => value;
        set { var next = Math.Round(Math.Clamp(value, Minimum, Maximum), 2); bool changed = next != this.value; this.value = next; box.Text = next.ToString("0.##", CultureInfo.CurrentCulture); if (changed) ValueChanged?.Invoke(); }
    }
    public NumberStepper()
    {
        CornerRadius = new(10); BorderThickness = new(1); Height = 36; HorizontalAlignment = HorizontalAlignment.Left;
        Background = new SolidColorBrush(Color.FromRgb(13, 14, 18)); BorderBrush = new SolidColorBrush(Color.FromArgb(31, 255, 255, 255));
        unit.Foreground = new SolidColorBrush(ColorUtil.TextTertiary); box.CaretBrush = new SolidColorBrush(ColorUtil.Accent);
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(Nudge("−", -1)); row.Children.Add(box); row.Children.Add(unit); row.Children.Add(Nudge("+", 1));
        Child = row;
        box.Text = "0";
        box.LostFocus += (_, _) => Commit(); box.KeyDown += (_, e) => { if (e.Key == Key.Enter) Commit(); };
        box.PreviewKeyDown += (_, e) => { if (e.Key is Key.Up or Key.Down) { Value += e.Key == Key.Up ? Step : -Step; e.Handled = true; } };
        IsEnabledChanged += (_, _) => Opacity = IsEnabled ? 1 : 0.45;
    }
    private Button Nudge(string text, int sign)
    {
        var button = new Button { Content = text, Width = 30, Foreground = new SolidColorBrush(ColorUtil.TextSecondary), FontSize = 15, Focusable = false, Cursor = Cursors.Hand };
        var chrome = new FrameworkElementFactory(typeof(Border)); chrome.SetValue(Border.BackgroundProperty, Brushes.Transparent);
        var presenter = new FrameworkElementFactory(typeof(ContentPresenter)); presenter.SetValue(HorizontalAlignmentProperty, HorizontalAlignment.Center); presenter.SetValue(VerticalAlignmentProperty, VerticalAlignment.Center); chrome.AppendChild(presenter);
        button.Template = new ControlTemplate(typeof(Button)) { VisualTree = chrome };
        AutomationProperties.SetName(button, sign > 0 ? "Increase" : "Decrease");
        button.Click += (_, _) => Value += sign * Step;
        return button;
    }
    private void Commit()
    {
        var text = box.Text.Replace(Unit, "").Trim();
        if (double.TryParse(text, NumberStyles.Float, CultureInfo.CurrentCulture, out var v) || double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out v)) Value = v;
        else Value = value;
    }
}

/// <summary>
/// A dual-ended range on a track painted with the palette, plus a tick at the live reading.
/// Drag either end (the nearest one is picked) or use the arrow keys on the last end touched.
/// </summary>
public sealed class RangeTrack : FrameworkElement
{
    private const double Inset = 10, TrackHeight = 12;
    private double lo, hi = 100, min = 20, max = 80;
    private int dragging = -1, lastEnd = 1;
    private double? reading;
    private IReadOnlyList<PaletteStop> stops = SensorLighting.DefaultPalette;
    public double Step { get; set; } = 1;
    public event Action<double, double>? RangeChanged;
    public double? Reading { get => reading; set { if (reading == value) return; reading = value; InvalidateVisual(); } }
    public IReadOnlyList<PaletteStop> Stops { get => stops; set { stops = value; InvalidateVisual(); } }
    public RangeTrack() { Height = 30; Cursor = Cursors.Hand; Focusable = true; }
    public void Configure(double lower, double upper, double start, double end) { lo = lower; hi = Math.Max(lower + 1, upper); min = start; max = end; InvalidateVisual(); }
    public void SetRange(double start, double end) { lo = Math.Min(lo, start); hi = Math.Max(hi, end); min = start; max = end; InvalidateVisual(); }
    private double X(double v) => Inset + (v - lo) / (hi - lo) * Math.Max(1, ActualWidth - Inset * 2);
    private double V(double x) => lo + Math.Clamp((x - Inset) / Math.Max(1, ActualWidth - Inset * 2), 0, 1) * (hi - lo);
    protected override void OnRender(DrawingContext dc)
    {
        double y = ActualHeight / 2, x0 = X(min), x1 = X(max);
        dc.DrawRoundedRectangle(new SolidColorBrush(ColorUtil.Track), null, new Rect(Inset, y - TrackHeight / 2, Math.Max(1, ActualWidth - Inset * 2), TrackHeight), TrackHeight / 2, TrackHeight / 2);
        if (x1 > x0)
        {
            var brush = new LinearGradientBrush { StartPoint = new(0, 0), EndPoint = new(1, 0) };
            foreach (var s in stops) brush.GradientStops.Add(new(ColorUtil.Parse(s.Color, Colors.Black), s.Position));
            dc.DrawRoundedRectangle(brush, null, new Rect(x0, y - TrackHeight / 2, x1 - x0, TrackHeight), TrackHeight / 2, TrackHeight / 2);
        }
        if (reading is { } r && double.IsFinite(r))
        {
            double x = Math.Clamp(X(r), Inset, ActualWidth - Inset);
            dc.DrawRoundedRectangle(Brushes.White, null, new Rect(x - 1.5, y - TrackHeight / 2 - 4, 3, TrackHeight + 8), 1.5, 1.5);
        }
        var ring = new Pen(new SolidColorBrush(Color.FromRgb(13, 14, 18)), 3);
        foreach (var (x, end) in new[] { (x0, 0), (x1, 1) })
            dc.DrawEllipse(Brushes.White, IsKeyboardFocused && end == lastEnd ? new Pen(new SolidColorBrush(ColorUtil.Accent), 3) : ring, new Point(x, y), 9, 9);
    }
    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        Focus(); double x = e.GetPosition(this).X;
        dragging = lastEnd = Math.Abs(x - X(min)) < Math.Abs(x - X(max)) || (Math.Abs(x - X(min)) == Math.Abs(x - X(max)) && x < X(min)) ? 0 : 1;
        CaptureMouse(); Move(V(x));
    }
    protected override void OnMouseMove(MouseEventArgs e) { if (dragging >= 0 && IsMouseCaptured) Move(V(e.GetPosition(this).X)); }
    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e) { dragging = -1; ReleaseMouseCapture(); }
    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.Key is Key.Tab && lastEnd == 0 && Keyboard.Modifiers == ModifierKeys.None) { lastEnd = 1; InvalidateVisual(); e.Handled = true; return; }
        if (e.Key is not (Key.Left or Key.Right or Key.Down or Key.Up)) return;
        double delta = e.Key is Key.Left or Key.Down ? -Step : Step;
        Move((lastEnd == 0 ? min : max) + delta, lastEnd); e.Handled = true;
    }
    protected override void OnGotKeyboardFocus(KeyboardFocusChangedEventArgs e) { base.OnGotKeyboardFocus(e); if (Mouse.LeftButton != MouseButtonState.Pressed) lastEnd = 0; InvalidateVisual(); }
    protected override void OnLostKeyboardFocus(KeyboardFocusChangedEventArgs e) { base.OnLostKeyboardFocus(e); InvalidateVisual(); }
    private void Move(double v, int? end = null)
    {
        // Snap to the step and keep at least one step between the ends.
        v = Math.Round(v / Step) * Step;
        if ((end ?? dragging) == 0) min = Math.Clamp(v, lo, max - Step); else max = Math.Clamp(v, min + Step, hi);
        InvalidateVisual(); RangeChanged?.Invoke(min, max);
    }
}

/// <summary>
/// What the LEDs do across the range: five short strips rendered by the real <see cref="SensorLighting"/> at
/// the low end, quarter points and high end, plus a marker for where the live reading sits.
/// </summary>
public sealed class SensorLookPreview : FrameworkElement
{
    private const int Leds = 8, Columns = 5;
    private readonly SensorLighting renderer = new();
    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromMilliseconds(66) };
    private readonly Stopwatch clock = Stopwatch.StartNew();
    private SensorBinding? binding;
    private double? reading;
    public string Unit { get; set; } = "";
    /// <summary>Fixed time for offscreen renders; null follows the clock.</summary>
    public double? PreviewTime { get; set; }
    public SensorBinding? Binding { get => binding; set { binding = value; UpdateTimer(); InvalidateVisual(); } }
    public double? Reading { get => reading; set { if (reading == value) return; reading = value; InvalidateVisual(); } }
    public SensorLookPreview()
    {
        timer.Tick += (_, _) => InvalidateVisual();
        IsVisibleChanged += (_, _) => UpdateTimer();
        Unloaded += (_, _) => timer.Stop();
    }
    /// <summary>Only breathing and the alert pulse move, so only they keep a timer running, and only while visible.</summary>
    private void UpdateTimer()
    {
        bool animate = IsVisible && !Motion.ReduceAnimations && binding is { } b && (b.Style == SensorStyle.BreathingSpeed || b.Alert is not null);
        if (animate) timer.Start(); else timer.Stop();
    }
    protected override void OnRender(DrawingContext dc)
    {
        if (binding is not { } b || b.Max <= b.Min || ActualWidth < 100) return;
        double time = PreviewTime ?? (Motion.ReduceAnimations ? 0.5 : clock.Elapsed.TotalSeconds), gap = 12, stripHeight = 30;
        double width = (ActualWidth - gap * (Columns - 1)) / Columns, dip = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        // Smoothing and learning would blur a still picture, so the preview shows the settled look.
        var still = b with { Smoothing = 0, AutoLearn = false };
        var black = Enumerable.Repeat(RgbColor.Black, Leds).ToArray();
        var label = new SolidColorBrush(ColorUtil.TextTertiary);
        var typeface = new Typeface(TextElement.GetFontFamily(this), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
        for (int c = 0; c < Columns; c++)
        {
            double t = c / (double)(Columns - 1), raw = b.Min + t * (b.Max - b.Min), x = c * (width + gap);
            var colours = renderer.Render("preview" + c, still, raw, time, black);
            dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromRgb(13, 14, 18)), new Pen(new SolidColorBrush(Color.FromArgb(24, 255, 255, 255)), 1), new Rect(x, 0, width, stripHeight), 10, 10);
            double pitch = (width - 16) / Leds, radius = Math.Max(1.5, Math.Min(5, pitch / 2 - 1.5));
            for (int i = 0; i < Leds; i++)
            {
                var led = colours[i]; bool lit = led.R + led.G + led.B > 0;
                var centre = new Point(x + 8 + pitch * (i + 0.5), stripHeight / 2);
                if (led.R + led.G + led.B > 60) dc.DrawEllipse(new SolidColorBrush(Color.FromArgb(60, led.R, led.G, led.B)), null, centre, radius + 3, radius + 3);
                dc.DrawEllipse(new SolidColorBrush(lit ? Color.FromRgb(led.R, led.G, led.B) : Color.FromRgb(34, 35, 41)), null, centre, radius, radius);
            }
            var text = new FormattedText(SensorBindingPanel.Format(raw, Unit), CultureInfo.CurrentCulture, FlowDirection.LeftToRight, typeface, 11, label, dip);
            dc.DrawText(text, new Point(x + (width - text.Width) / 2, stripHeight + 8));
        }
        // Where the reading sits right now, on a thin rule under the strips.
        double ruleY = ActualHeight - 12;
        dc.DrawRoundedRectangle(new SolidColorBrush(ColorUtil.Track), null, new Rect(width / 2, ruleY - 1.5, Math.Max(1, ActualWidth - width), 3), 1.5, 1.5);
        if (reading is { } r && double.IsFinite(r))
        {
            double n = Math.Clamp((r - b.Min) / (b.Max - b.Min), 0, 1), mx = width / 2 + n * (ActualWidth - width);
            dc.DrawEllipse(new SolidColorBrush(ColorUtil.Accent), new Pen(Brushes.White, 2), new Point(mx, ruleY), 5, 5);
            var now = new FormattedText("now " + SensorBindingPanel.Format(r, Unit), CultureInfo.CurrentCulture, FlowDirection.LeftToRight, typeface, 11, Brushes.WhiteSmoke, dip);
            double tx = mx + 10 + now.Width > ActualWidth ? mx - 10 - now.Width : mx + 10;
            dc.DrawText(now, new Point(Math.Max(0, tx), ruleY - now.Height / 2));
        }
    }
}
