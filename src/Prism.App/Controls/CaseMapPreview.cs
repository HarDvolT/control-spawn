using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Prism.App.Common;
using Prism.App.ViewModels;
using Prism.Core;
using Prism.Core.Ipc;
using Prism.Lighting;

namespace Prism.App.Controls;

/// <summary>Shared spatial renderer for gallery cards, the whole-PC preview and LED painting.</summary>
public sealed class CaseMapPreview : FrameworkElement
{
    private readonly SensorLighting sensorLighting = new();
    private static readonly Stopwatch Clock = Stopwatch.StartNew();
    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromSeconds(1d / 30) };
    private LightingViewModel? model;
    private readonly LightingEffect? effect;
    private LightingMap? draft;
    private DevicePlacement? dragged;
    private Point dragStart;
    private int anchor = -1;
    private string? anchorDevice;
    private bool draggingLamp;
    private string? draggedLampId;
    private readonly List<(Point Pixel, string Id)> lampHits = new();
    private readonly List<(Point Pixel, RgbDeviceState Device, int Index, DevicePlacement Placement)> hits = new();
    public bool MapEditing { get; set; }
    public bool LedEditing { get; set; }
    public double? PreviewTime { get; set; }
    public DevicePlacement? SelectedPlacement { get; private set; }
    public event Action? SelectionChanged;
    public CaseMapPreview() : this(null) { }
    public CaseMapPreview(LightingEffect? effect)
    {
        this.effect = effect;
        ClipToBounds = true; Focusable = true;
        timer.Tick += (_, _) => { if (!Motion.ReduceAnimations) InvalidateVisual(); };
        Loaded += (_, _) => { Subscribe(); timer.Start(); };
        Unloaded += (_, _) => { timer.Stop(); if (model is not null) { model.PropertyChanged -= Changed; model.Editor.PropertyChanged -= Changed; } };
        DataContextChanged += (_, _) => { Subscribe(); InvalidateVisual(); };
        ToolTip = effect is null ? "Map mode: drag devices or lamp. Edit LEDs: brush, gradient, fill or select directly on the case." : effect.ToString();
    }
    private void Subscribe()
    {
        if (model is not null) { model.PropertyChanged -= Changed; model.Editor.PropertyChanged -= Changed; }
        model = DataContext as LightingViewModel;
        if (model is not null) { model.PropertyChanged += Changed; model.Editor.PropertyChanged += Changed; }
    }
    private void Changed(object? sender, PropertyChangedEventArgs args) => InvalidateVisual();
    public LightingMap CurrentMap
    {
        get
        {
            var devices = model?.State?.Lighting.Devices ?? DemoDevices;
            var saved = model?.Map ?? new([]);
            var placements = CaseMap.Default(devices).Devices.Select(d => saved.Devices.FirstOrDefault(p => p.Device == d.Device && p.Zone == d.Zone) ?? d)
                .Concat(saved.Devices.Where(d => !CaseMap.Default(devices).Devices.Any(p => p.Device == d.Device && p.Zone == d.Zone))).ToArray();
            return draft ?? new(placements, saved.Lamp, saved.Lamps);
        }
    }
    public static IReadOnlyList<RgbDeviceState> DemoDevices { get; } = [new("ram1", "RAM 1", "RAM", true, true, 10), new("ram2", "RAM 2", "RAM", true, true, 10),
        new("gpu", "GPU", "GPU", true, true, 4), new("strip", "ARGB", "strip", true, true, 24), new("board", "Board", "Motherboard", true, true, 2)];
    private Point Pixel(MapPoint p) => new(22 + p.X * Math.Max(1, ActualWidth - 44), 22 + p.Y * Math.Max(1, ActualHeight - 44));
    protected override void OnRender(DrawingContext dc)
    {
        bool mini = effect.HasValue;
        double w = ActualWidth, h = ActualHeight;
        if (w < 1 || h < 1) return;
        dc.DrawRoundedRectangle(Brush("#141418"), new Pen(Brush("#34343D"), 1), new Rect(3, 3, w - 6, h - 6), mini ? 10 : 22, mini ? 10 : 22);
        dc.DrawRoundedRectangle(Brush("#1B1B21"), new Pen(Brush("#292931"), 1), new Rect(w * 0.15, h * 0.18, w * 0.60, h * 0.64), 8, 8);
        // Case fans and PSU give scale to the map without hiding the LEDs.
        for (int f = 0; f < 3; f++)
        {
            var centre = new Point(w * 0.87, h * (0.25 + f * 0.22)); double r = Math.Min(w * 0.075, h * 0.08);
            dc.DrawEllipse(Brush("#1D1D24"), new Pen(Brush("#34343D"), 1), centre, r, r);
            dc.DrawEllipse(null, new Pen(Brush("#2B2B34"), 3), centre, r * 0.7, r * 0.7);
        }
        var devices = model?.State?.Lighting.Devices ?? DemoDevices;
        var map = CurrentMap;
        var scene = new Scene("Preview", effect ?? model?.Effect ?? LightingEffect.Aurora, Palettes.Parse(model?.ColorHex ?? "#FF4D1A"), model?.Speed ?? 1, model?.Brightness ?? 1);
        double time = PreviewTime ?? (Motion.ReduceAnimations ? 1.25 : Clock.Elapsed.TotalSeconds);
        var p = model?.Parameters ?? new();
        if (mini) p = p with { Palette = Palettes.ForEffect(scene.Effect), Stops = null };
        var system = model?.State?.System;
        var capture = model?.State?.Lighting.Inputs;
        bool illustrative = mini || model?.State?.Health.Mock != false;
        var input = new ReactiveFrame(system?.CpuLoad ?? 42, system?.GpuLoad ?? 65, system?.RamTotalGb > 0 ? (system.RamUsedGb ?? 0) / system.RamTotalGb.Value : 0.5,
            system?.CpuTemp ?? 50, system?.GpuTemp ?? 55,
            illustrative ? Enumerable.Range(0, 12).Select(i => 0.35 + 0.3 * Math.Sin(time * 2 + i)).ToArray() : capture?.AudioBands ?? [],
            illustrative ? 0.1 : capture?.Beat ?? 0,
            illustrative ? new(30, 90, 180) : capture is null ? RgbColor.Black : Palettes.Parse(capture.ScreenLeft),
            illustrative ? new(140, 60, 180) : capture is null ? RgbColor.Black : Palettes.Parse(capture.ScreenCentre),
            illustrative ? new(255, 130, 40) : capture is null ? RgbColor.Black : Palettes.Parse(capture.ScreenRight));
        hits.Clear();
        foreach (var device in devices)
        {
            var placement = CaseMap.Placement(map, device, devices);
            var positions = CaseMap.Positions(device, placement, map);
            var colors = SpatialEffects.Render(scene, p, time, positions, device.Id, device.Kind, input);
            if (!mini)
            {
                var editor = model?.Editor;
                var custom = editor?.Device?.Id == device.Id && editor.Layer == "custom" ? editor.Colors : device.CustomColors;
                var overlay = editor?.Device?.Id == device.Id && editor.Layer == "overlay" ? editor.Colors : device.OverlayColors;
                // Paint drafts are visible immediately on the shared canvas.
                colors = LedLayers.Composite(colors, custom, overlay, device.CustomEnabled || editor?.Device?.Id == device.Id && editor.Layer == "custom" && editor.HasCustomLayer || scene.Effect == LightingEffect.Custom).ToArray();
            }
            if (!mini && model?.State is { } sensorState)
                colors = sensorLighting.Composite(device, sensorState.Lighting.Bindings, sensorState, time, colors, scene.Brightness).ToArray();
            if (!mini && device.Zones is not null)
                for (int z = 0; z < device.Zones.Count; z++)
                {
                    if (device.Zones[z].LedCount != 0 || device.Zones[z].MaxLeds <= device.Zones[z].MinLeds) continue;
                    var empty = map.Devices.FirstOrDefault(d => d.Device == device.Id && d.Zone == z);
                    if (empty is null) continue;
                    double a = empty.Rotation * Math.PI / 180;
                    var start = Pixel(new(Math.Clamp(empty.X - Math.Cos(a) * empty.Length / 2, 0, 1), Math.Clamp(empty.Y - Math.Sin(a) * empty.Length / 2, 0, 1)));
                    var end = Pixel(new(Math.Clamp(empty.X + Math.Cos(a) * empty.Length / 2, 0, 1), Math.Clamp(empty.Y + Math.Sin(a) * empty.Length / 2, 0, 1)));
                    dc.DrawLine(new Pen(Brush("#555563"), 5) { DashStyle = DashStyles.Dash }, start, end);
                    Label(dc, "ARGB / 0 LEDs", new(start.X, start.Y + 10), 10, Brush("#A1A1AA"));
                    if (MapEditing) for (int hIndex = 0; hIndex <= 10; hIndex++) hits.Add((new(start.X + (end.X - start.X) * hIndex / 10, start.Y + (end.Y - start.Y) * hIndex / 10), device, -1, empty));
                }
            for (int i = 0; i < positions.Length; i++)
            {
                var point = Pixel(positions[i]); var c = colors[i]; var color = Color.FromRgb(c.R, c.G, c.B);
                int? zoneIndex = null;
                if (device.Zones is not null)
                    for (int z = 0; z < device.Zones.Count; z++) if (i >= device.Zones[z].FirstLed && i < device.Zones[z].FirstLed + device.Zones[z].LedCount && device.Zones[z].MaxLeds > device.Zones[z].MinLeds) zoneIndex = z;
                var ledPlacement = zoneIndex is null ? placement : map.Devices.FirstOrDefault(d => d.Device == device.Id && d.Zone == zoneIndex) ?? placement;
                double rx = mini ? 2.4 : device.Kind == "RAM" ? 13 : 6, ry = mini ? 2.4 : 5;
                if (i == 0 || zoneIndex is not null && i == device.Zones![zoneIndex.Value].FirstLed)
                {
                    var group = positions.Where((_, index) => zoneIndex is null ? device.Zones?.Any(z => z.MaxLeds > z.MinLeds && index >= z.FirstLed && index < z.FirstLed + z.LedCount) != true : index >= device.Zones![zoneIndex.Value].FirstLed && index < device.Zones[zoneIndex.Value].FirstLed + device.Zones[zoneIndex.Value].LedCount).Select(Pixel).ToArray();
                    if (group.Length > 0)
                    {
                        var bounds = new Rect(new Point(group.Min(v => v.X) - rx - 6, group.Min(v => v.Y) - ry - 6), new Point(group.Max(v => v.X) + rx + 6, group.Max(v => v.Y) + ry + 6));
                        dc.DrawRoundedRectangle(Brush("#25252D"), new Pen(SelectedPlacement?.Device == device.Id && SelectedPlacement.Zone == zoneIndex ? Brush("#FF4D1A") : Brush("#44444D"), 1), bounds, 5, 5);
                        if (!mini) Label(dc, zoneIndex is null ? device.Name : device.Zones![zoneIndex.Value].Name, new(bounds.Left, bounds.Bottom + 5), 10, Brush("#A1A1AA"));
                    }
                }
                for (int glow = 3; glow > 0; glow--) dc.DrawEllipse(new SolidColorBrush(Color.FromArgb((byte)(15 + (3 - glow) * 7), c.R, c.G, c.B)), null, point, rx + glow * (mini ? 1.5 : 4), ry + glow * (mini ? 1.5 : 4));
                dc.DrawRoundedRectangle(new SolidColorBrush(color), null, new Rect(point.X - rx, point.Y - ry, rx * 2, ry * 2), 3, 3);
                var selectedEditor = model?.Editor;
                if (!mini && LedEditing && selectedEditor?.Device?.Id == device.Id && selectedEditor.Tool == "Select range" &&
                    i >= Math.Min(selectedEditor.RangeStart, selectedEditor.RangeEnd) && i <= Math.Max(selectedEditor.RangeStart, selectedEditor.RangeEnd))
                    dc.DrawRoundedRectangle(null, new Pen(Brushes.White, 1.5), new Rect(point.X - rx - 2, point.Y - ry - 2, rx * 2 + 4, ry * 2 + 4), 4, 4);
                if (!mini) hits.Add((point, device, i, ledPlacement));
            }
        }
        if (!mini)
        {
            lampHits.Clear();
            var lamps = model?.State?.Lamps;
            foreach (var item in lamps ?? [])
            {
                var lp = Pixel(CaseMap.LampPoint(map, devices, item.Id ?? "unresolved", lampHits.Count));
                lampHits.Add((lp, item.Id ?? "unresolved"));
                var lampColor = Palettes.Parse(item.Color);
                if (model?.State is { } lampState && item.Id is { } lampId)
                {
                    var lampDevice = new RgbDeviceState(lampId, item.Name ?? "Lamp", "Lamp", true, true, 1);
                    lampColor = sensorLighting.Composite(lampDevice, lampState.Lighting.Bindings, lampState, time, [lampColor], scene.Brightness)[0];
                }
                dc.DrawEllipse(new SolidColorBrush(Color.FromRgb(lampColor.R, lampColor.G, lampColor.B)), new Pen(Brush("#E4E4E7"), 1.5), lp, 10, 10);
                Label(dc, item.Name ?? "Lamp", new(lp.X > ActualWidth * 0.7 ? lp.X - 76 : lp.X + 14, lp.Y - 22), 10, Brush("#E4E4E7"));
            }
            var point = Pixel(CaseMap.LampPoint(map, devices));
            if (lamps is not { Count: > 0 }) dc.DrawEllipse(null, new Pen(Brush("#E4E4E7"), 1.5), point, 10, 10);
            if (lamps is not { Count: > 0 }) Label(dc, "LAMP", new(point.X + 14, point.Y - 9), 10, Brush("#E4E4E7"));
            Label(dc, MapEditing ? "DRAG TO ARRANGE" : LedEditing ? "PER-LED CANVAS" : "LIVE CASE MAP", new(18, h - 26), 10, Brush("#A1A1AA"));
        }
    }
    private static SolidColorBrush Brush(string hex) => new((Color)ColorConverter.ConvertFromString(hex));
    private void Label(DrawingContext dc, string text, Point position, double size, System.Windows.Media.Brush brush) => dc.DrawText(new FormattedText(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, new Typeface("Segoe UI"), size, brush, VisualTreeHelper.GetDpi(this).PixelsPerDip), position);
    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        if (model is null || effect.HasValue) return;
        var point = e.GetPosition(this);
        if (MapEditing && lampHits.FirstOrDefault(h => (h.Pixel - point).Length < 16) is var hitLamp && hitLamp.Id is not null)
        { draggingLamp = true; draggedLampId = hitLamp.Id; CaptureMouse(); return; }
        if (MapEditing && model.State?.Lamps is not { Count: > 0 } && (point - Pixel(CaseMap.LampPoint(CurrentMap, model.State?.Lighting.Devices ?? DemoDevices))).Length < 16)
        { draggingLamp = true; CaptureMouse(); return; }
        var hit = Nearest(point); if (hit is null) return;
        if (MapEditing)
        {
            SelectedPlacement = dragged = hit.Value.Placement; draft = CurrentMap; dragStart = point; CaptureMouse(); SelectionChanged?.Invoke();
        }
        else if (LedEditing)
        {
            model.Editor.Device = model.Editor.Devices.FirstOrDefault(d => d.Id == hit.Value.Device.Id);
            anchor = hit.Value.Index; anchorDevice = hit.Value.Device.Id;
            if (model.Editor.Tool == "Select range" || Keyboard.Modifiers.HasFlag(ModifierKeys.Shift))
            { model.Editor.RangeStart = anchor; model.Editor.RangeEnd = anchor; }
            else Paint(hit.Value.Device, anchor);
            CaptureMouse();
        }
        InvalidateVisual();
    }
    private (Point Pixel, RgbDeviceState Device, int Index, DevicePlacement Placement)? Nearest(Point point) => hits.Where(h => (h.Pixel - point).Length < 24).OrderBy(h => (h.Pixel - point).Length).Select(h => ((Point, RgbDeviceState, int, DevicePlacement)?)h).FirstOrDefault();
    protected override void OnMouseMove(MouseEventArgs e)
    {
        if (!IsMouseCaptured || model is null) return;
        var point = e.GetPosition(this);
        if (draggingLamp)
        {
            var position = new MapPoint(Math.Clamp((point.X - 22) / Math.Max(1, ActualWidth - 44), 0, 1), Math.Clamp((point.Y - 22) / Math.Max(1, ActualHeight - 44), 0, 1));
            if (draggedLampId is null) draft = CurrentMap with { Lamp = position };
            else { var points = new Dictionary<string, MapPoint>(CurrentMap.Lamps ?? new Dictionary<string, MapPoint>()) { [draggedLampId] = position }; draft = CurrentMap with { Lamps = points }; }
        }
        else if (dragged is { } selected)
        {
            var moved = selected with { X = Math.Clamp(selected.X + (point.X - dragStart.X) / Math.Max(1, ActualWidth - 44), 0, 1), Y = Math.Clamp(selected.Y + (point.Y - dragStart.Y) / Math.Max(1, ActualHeight - 44), 0, 1) };
            draft = CurrentMap with { Devices = CurrentMap.Devices.Select(d => d.Device == moved.Device && d.Zone == moved.Zone ? moved : d).ToArray() };
            SelectedPlacement = moved;
        }
        else if (LedEditing && Nearest(point) is { } hit && hit.Device.Id == anchorDevice)
        {
            if (model.Editor.Tool is "Brush" or "Eraser") Paint(hit.Device, hit.Index);
            if (model.Editor.Tool == "Select range") model.Editor.RangeEnd = hit.Index;
        }
        InvalidateVisual();
    }
    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        if (!IsMouseCaptured || model is null) return;
        if (dragged is not null || draggingLamp) { var map = CurrentMap; draft = null; _ = model.SetMapAsync(map); }
        else if (LedEditing && model.Editor.Tool == "Gradient" && Nearest(e.GetPosition(this)) is { } hit && hit.Device.Id == anchorDevice)
        {
            var indices = Enumerable.Range(Math.Min(anchor, hit.Index), Math.Abs(hit.Index - anchor) + 1);
            model.Editor.Paint(anchor > hit.Index ? indices.Reverse() : indices, gradient: true);
        }
        dragged = null; draggingLamp = false; draggedLampId = null; ReleaseMouseCapture(); InvalidateVisual();
    }
    private void Paint(RgbDeviceState device, int index)
    {
        if (model is null) return;
        var editor = model.Editor;
        if (editor.Tool == "Eyedropper") { editor.Color = editor.Colors[index][..7]; return; }
        if (editor.Tool == "Fill zone")
        {
            var zone = device.Zones?.FirstOrDefault(z => index >= z.FirstLed && index < z.FirstLed + z.LedCount);
            editor.Paint(Enumerable.Range(zone?.FirstLed ?? 0, zone?.LedCount ?? device.LedCount));
        }
        else if (editor.Tool is "Brush" or "Eraser") editor.Paint([index], editor.Tool == "Eraser");
    }
    public async Task TransformSelectionAsync(double rotation, bool flip)
    {
        if (SelectedPlacement is not { } selected || model is null) return;
        var changed = selected with { Rotation = rotation, Flip = flip };
        SelectedPlacement = changed;
        await model.SetMapAsync(CurrentMap with { Devices = CurrentMap.Devices.Select(d => d.Device == changed.Device && d.Zone == changed.Zone ? changed : d).ToArray() });
        InvalidateVisual();
    }
}
