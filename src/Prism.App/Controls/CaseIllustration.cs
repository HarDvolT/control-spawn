using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Media;
using Prism.App.ViewModels;
using Prism.Core;
using Prism.Core.Ipc;
using Prism.Lighting;
using System.Diagnostics;
using System.Windows.Threading;
using Prism.App.Common;

namespace Prism.App.Controls;

/// <summary>The v2 whole-case illustration. Editing still uses the spatial canvas.</summary>
public sealed class CaseIllustration : FrameworkElement
{
    private LightingViewModel? model;
    private static readonly Stopwatch clock = Stopwatch.StartNew();
    private readonly SensorLighting sensorLighting = new();
    private readonly DispatcherTimer timer = Motion.FrameTimer();
    public CaseIllustration()
    {
        Height = 400;
        timer.Tick += (_, _) => { if (!Motion.ReduceAnimations) InvalidateVisual(); };
        DataContextChanged += (_, _) => { if (model is not null) model.PropertyChanged -= Changed; model = DataContext as LightingViewModel; if (model is not null) model.PropertyChanged += Changed; InvalidateVisual(); };
        Motion.WhileVisible(this, timer.Start, timer.Stop);
        Unloaded += (_, _) => { if (model is not null) model.PropertyChanged -= Changed; };
        Loaded += (_, _) => { if (model is not null) { model.PropertyChanged -= Changed; model.PropertyChanged += Changed; } };
    }
    /// <summary>Which lamp orb, if any, is at this point (orbs sit at x 584 in the 640 × 400 drawing).</summary>
    private LampState? LampAt(Point at)
    {
        var (scale, dx, dy) = Fit();
        double x = (at.X - dx) / scale, y = (at.Y - dy) / scale;
        var lamps = model?.State?.Lamps ?? [];
        for (int i = 0; i < lamps.Count && i < 2; i++)
            if (Math.Abs(x - 584) < 40 && Math.Abs(y - (118 + i * 140)) < 44) return lamps[i];
        return null;
    }
    protected override void OnMouseMove(System.Windows.Input.MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var lamp = LampAt(e.GetPosition(this));
        Cursor = lamp is { Connected: true } ? System.Windows.Input.Cursors.Hand : null;
        ToolTip = lamp is null ? null : lamp.Connected ? $"Click to turn {(lamp.On ? "off" : "on")}" : "Lamp offline";
    }
    protected override void OnMouseLeftButtonUp(System.Windows.Input.MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        if (model is not null && LampAt(e.GetPosition(this)) is { Connected: true } lamp) { _ = model.ToggleLampAsync(lamp); e.Handled = true; }
    }
    private void Changed(object? sender, PropertyChangedEventArgs e) => InvalidateVisual();
    private static Brush B(string value) => new SolidColorBrush((Color)ColorConverter.ConvertFromString(value));
    /// <summary>The name the user gave a lamp on the Devices page, falling back to "Lamp 1", "Lamp 2".</summary>
    public static string LampName(StateMessage? state, LampState lamp, int index)
    {
        var named = state?.Devices?.FirstOrDefault(d => d.Id == lamp.Id)?.Name;
        foreach (var candidate in new[] { named, lamp.Name })
            if (!string.IsNullOrWhiteSpace(candidate) && !candidate.Contains('.')) return candidate;
        return "Lamp " + (index + 1);
    }

    /// <summary>
    /// What a device's LEDs show right now, as the service renders them: the effect, painted layers, sensor bindings,
    /// and parts switched off under "Applies to" left dark. <paramref name="overlay"/> stands in for an unsaved paint draft.
    /// </summary>
    /// <summary>One clock for every live preview in the app, so speed changes carry on smoothly there too.</summary>
    public static readonly PhaseClock Phases = new();
    public static RgbColor[] LiveColors(LightingViewModel model, StateMessage state, RgbDeviceState device, IReadOnlyList<RgbDeviceState> devices,
        double time, SensorLighting sensorLighting, IReadOnlyList<string>? overlay = null)
    {
        var scene = new Scene("Preview", state.Lighting.Effect, Palettes.Parse(state.Lighting.Color), state.Lighting.Speed, state.Lighting.Brightness);
        var positions = CaseMap.Positions(device, CaseMap.Placement(model.Map, device, devices), model.Map);
        var input = state.Lighting.Inputs;
        var frame = new ReactiveFrame(state.System.CpuLoad ?? 0, state.System.GpuLoad ?? 0,
                state.System.RamTotalGb > 0 ? (state.System.RamUsedGb ?? 0) / state.System.RamTotalGb.Value : 0,
                state.System.CpuTemp ?? double.NaN, state.System.GpuTemp ?? double.NaN, input?.AudioBands ?? [], input?.Beat ?? 0,
                input is null ? RgbColor.Black : Palettes.Parse(input.ScreenLeft), input is null ? RgbColor.Black : Palettes.Parse(input.ScreenCentre), input is null ? RgbColor.Black : Palettes.Parse(input.ScreenRight));
        // Parts with their own look render as the service renders them (see ServiceRuntime.RenderDevice).
        var looks = state.Lighting.PartLooks;
        RgbColor[] Look(string key, PartLook look) => SpatialEffects.Render(new("Part", look.Effect, Palettes.Parse(look.Color), 1, look.Brightness),
            (state.Lighting.Effect == look.Effect ? model.Parameters : new EffectParameters { Palette = Palettes.ForEffect(look.Effect) }) with { Devices = null },
            Phases.Time(key, time, look.Speed), positions, device.Id, device.Kind, frame);
        var colors = looks?.GetValueOrDefault(device.Id) is { } own ? Look(device.Id, own)
            : SpatialEffects.Render(PhaseClock.Steady(scene), model.Parameters, Phases.Time("case", time, scene.Speed), positions, device.Id, device.Kind, frame);
        for (int z = 0; z < (device.Zones?.Count ?? 0) && looks is not null; z++)
            if (looks.GetValueOrDefault(SensorSources.ZoneTarget(device.Id, z)) is { } zoneLook)
            {
                var part = Look(SensorSources.ZoneTarget(device.Id, z), zoneLook); var zone = device.Zones![z];
                for (int i = zone.FirstLed; i < zone.FirstLed + zone.LedCount && i < colors.Length; i++) colors[i] = part[i];
            }
        foreach (var (key, rangeLook) in looks ?? new Dictionary<string, PartLook>())
            if (SensorSources.TryRange(key, device.Id, out var first, out var count))
            {
                var part = Look(key, rangeLook);
                for (int i = first; i < first + count && i < colors.Length; i++) colors[i] = part[i];
            }
        colors = LedLayers.Composite(colors, device.CustomColors, overlay ?? device.OverlayColors, device.CustomEnabled).ToArray();
        colors = sensorLighting.Composite(device, state.Lighting.Bindings, state, time, colors, scene.Brightness).ToArray();
        for (int z = 0; z < (device.Zones?.Count ?? 0); z++)
            if (model.Parameters.Devices?.GetValueOrDefault(SensorSources.ZoneTarget(device.Id, z), true) == false)
                for (int i = device.Zones![z].FirstLed; i < device.Zones[z].FirstLed + device.Zones[z].LedCount && i < colors.Length; i++) colors[i] = RgbColor.Black;
        foreach (var (key, on) in model.Parameters.Devices ?? new Dictionary<string, bool>())
            if (!on && SensorSources.TryRange(key, device.Id, out var first, out var count))
                for (int i = first; i < first + count && i < colors.Length; i++) colors[i] = RgbColor.Black;
        return colors;
    }

    // One scale for both axes, centred: stretching each axis on its own widened the case and its text at full screen.
    private (double Scale, double X, double Y) Fit()
    {
        double scale = Math.Max(0.01, Math.Min(ActualWidth / 640, ActualHeight / 400));
        return (scale, (ActualWidth - 640 * scale) / 2, (ActualHeight - 400 * scale) / 2);
    }

    protected override void OnRender(DrawingContext dc)
    {
        var (scale, dx, dy) = Fit();
        dc.PushTransform(new TranslateTransform(dx, dy));
        dc.PushTransform(new ScaleTransform(scale, scale));
        var devices = model?.State?.Lighting.Devices ?? [];
        var rendered = new Dictionary<string, RgbColor[]>();
        double time = Motion.ReduceAnimations ? 1.25 : clock.Elapsed.TotalSeconds;
        var ram = devices.Where(d => d.Kind.Contains("RAM", StringComparison.OrdinalIgnoreCase)).ToArray();
        var gpu = devices.FirstOrDefault(d => d.Kind.Contains("GPU", StringComparison.OrdinalIgnoreCase));
        var board = devices.FirstOrDefault(d => d.Kind.Contains("Motherboard", StringComparison.OrdinalIgnoreCase));
        var strip = devices.FirstOrDefault(d => d.Kind.Contains("strip", StringComparison.OrdinalIgnoreCase));
        // On most boards the ARGB headers are zones of the motherboard. Used headers light, in order: the AIO (pump fan, then
        // radiator fans chained after it, 12 LEDs each), the back and front case fans, then the bottom edge.
        var headers = (board?.Zones ?? []).Select((z, i) => (Zone: z, Index: i))
            .Where(z => z.Zone.MaxLeds > z.Zone.MinLeds && z.Zone.LedCount > 0).ToArray();
        (RgbDeviceState Device, RgbZoneState Zone, int Header)? Part(int order) => board is not null && order < headers.Length ? (board, headers[order].Zone, headers[order].Index) : null;
        var aio = Part(0); var fans = Part(1); var tail = Part(2);
        int aioRings = aio is { } chain && chain.Zone.LedCount > 12 ? Math.Clamp(chain.Zone.LedCount / 12, 2, 8) : 1;
        (RgbDeviceState Device, RgbZoneState Zone, int Header)? pump = aio is { } c0 ? (c0.Device, aioRings > 1 ? Slice(c0.Zone, 0, aioRings) : c0.Zone, c0.Header) : null;
        (RgbDeviceState Device, RgbZoneState Zone, int Header)? Radiator(int i) => aio is { } c && aioRings > 1 && i + 1 < aioRings ? (c.Device, Slice(c.Zone, i + 1, aioRings), c.Header) : null;

        // Case shell and motherboard tray.
        dc.DrawRoundedRectangle(B("#0E0F13"), new Pen(B("#2A2C33"), 1.5), new Rect(18, 18, 500, 364), 24, 24);
        dc.DrawRoundedRectangle(B("#121318"), new Pen(B("#1E2026"), 1), new Rect(40, 108, 318, 172), 16, 16);
        Label(board is null ? "MOTHERBOARD" : Short(board.Name), 56, 118, 10);
        if (board is not null)
        {
            // The board's own lit logo, on its left-hand side beside the CPU block, as on the real board.
            dc.PushTransform(new TranslateTransform(56, 182));
            dc.DrawGeometry(Colour(board, 0), null, Geometry.Parse("M0,0 L34,-10 21,21Z")); dc.Pop();
        }
        // Pump fan ring on the CPU block.
        FanRing(new Point(146, 190), 34, pump, 16);
        Label(pump is { } p1 ? $"PUMP FAN · HEADER {p1.Header}" : "PUMP FAN", 98, 240, 10);
        // Radiator fans across the top of the case.
        for (int i = 0; i < 3; i++)
            FanRing(new Point(100 + i * 80, 60), 28, Radiator(i), 14);
        Label("RADIATOR", 300, aioRings > 1 ? 46 : 54, 10);
        if (aioRings > 1 && aio is { } a2) Label($"HEADER {a2.Header}", 300, 60, 9);
        // RAM sticks.
        for (int i = 0; i < 2; i++)
        {
            var stick = new Rect(246 + i * 30, 128, 18, 128);
            dc.DrawRoundedRectangle(B("#17181D"), new Pen(B("#24262D"), 1), stick, 5, 5);
            if (i < ram.Length)
                for (int j = 0; j < ram[i].LedCount && j < 10; j++)
                    dc.DrawRoundedRectangle(Colour(ram[i], j), null, new Rect(stick.X + 4, stick.Y + 5 + j * 12, 10, 9), 3, 3);
        }
        Label("RAM", 261, 262, 10);
        // Graphics card.
        dc.DrawRoundedRectangle(B("#15161B"), new Pen(B("#24262D"), 1), new Rect(40, 290, 318, 48), 12, 12);
        if (gpu is not null)
            for (int i = 0; i < 24; i++)
                dc.DrawRoundedRectangle(Colour(gpu, i * gpu.LedCount / 24), null, new Rect(54 + i * 12.2, 298, 9, 5), 2, 2);
        Label(gpu is null ? "GRAPHICS CARD" : Short(gpu.Name), 56, 314, 11);
        // Case fans down the front.
        for (int i = 0; i < 3; i++)
            FanRing(new Point(446, 92 + i * 102), 40, fans is { } f ? (f.Device, Slice(f.Zone, i, 3), f.Header) : null, 16);
        Label(fans is { } f2 ? $"CASE FANS · HEADER {f2.Header}" : "CASE FANS", 390, 356, 10);
        // A further header, or a separate strip device, lights the bottom edge.
        var bottom = tail ?? (strip is not null ? (strip, new RgbZoneState("Strip", "Linear", 0, strip.LedCount, 0, strip.LedCount), -1) : null);
        if (bottom is { } b)
        {
            for (int i = 0; i < 26; i++)
                dc.DrawRoundedRectangle(Colour(b.Device, b.Zone.FirstLed + i * Math.Max(1, b.Zone.LedCount) / 26), null, new Rect(46 + i * 12, 350, 8, 5), 2, 2);
            Label(b.Header > 0 ? $"HEADER {b.Header} · {b.Zone.LedCount} LEDS" : "LED STRIP", 46, 360, 9);
        }

        int lampIndex = 0;
        foreach (var lamp in model?.State?.Lamps ?? [])
        {
            if (lampIndex >= 2) break;
            double y = 118 + lampIndex * 140;
            var color = (Color)ColorConverter.ConvertFromString(lamp.Connected && lamp.On ? lamp.Color : "#24262D");
            var glow = new RadialGradientBrush(Color.FromArgb(110, color.R, color.G, color.B), Color.FromArgb(0, color.R, color.G, color.B));
            dc.DrawEllipse(glow, null, new Point(584, y), 54, 54);
            var orb = new RadialGradientBrush { GradientOrigin = new(.3, .2), Center = new(.4, .35), RadiusX = .75, RadiusY = .75 };
            orb.GradientStops.Add(new(Colors.White, 0)); orb.GradientStops.Add(new(color, .3)); orb.GradientStops.Add(new(Color.FromRgb((byte)(color.R * .3), (byte)(color.G * .3), (byte)(color.B * .3)), 1));
            dc.DrawEllipse(orb, null, new Point(584, y), 28, 28);
            CenteredLabel(LampName(model?.State, lamp, lampIndex), 584, y + 38, 11, "#C9CBD1");
            CenteredLabel(!lamp.Connected ? "Offline" : !lamp.On ? "Off" : lamp.Follow switch { "pc" or "map" => "Follows PC", "average" => "PC average", _ => "Own colour" }, 584, y + 54, 9, "#7C808A");
            lampIndex++;
        }
        dc.Pop(); dc.Pop();

        static string Short(string name) => name.Length > 34 ? name[..34].TrimEnd() + "…" : name;
        static RgbZoneState Slice(RgbZoneState zone, int part, int parts) =>
            zone with { FirstLed = zone.FirstLed + part * zone.LedCount / parts, LedCount = Math.Max(1, zone.LedCount / parts) };
        void FanRing(Point centre, double radius, (RgbDeviceState Device, RgbZoneState Zone, int Header)? part, int dots)
        {
            dc.DrawEllipse(B("#15161B"), new Pen(B("#1E2026"), 1), centre, radius + 9, radius + 9);
            dc.DrawEllipse(null, new Pen(B("#1A1B21"), 10), centre, radius, radius);
            for (int k = 0; k < dots; k++)
            {
                double a = k * 2 * Math.PI / dots - Math.PI / 2;
                var at = new Point(centre.X + radius * Math.Cos(a), centre.Y + radius * Math.Sin(a));
                var brush = part is { } z ? Colour(z.Device, z.Zone.FirstLed + k * Math.Max(1, z.Zone.LedCount) / dots) : B("#24262F");
                dc.DrawEllipse(brush, null, at, 4.2, 4.2);
            }
            // Hub and blades, so the ring reads as a fan.
            var blade = new Pen(B("#20222A"), 5) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
            for (int k = 0; k < 7; k++)
            {
                double a = k * 2 * Math.PI / 7;
                dc.DrawLine(blade, new Point(centre.X + 9 * Math.Cos(a), centre.Y + 9 * Math.Sin(a)), new Point(centre.X + (radius - 12) * Math.Cos(a + 0.5), centre.Y + (radius - 12) * Math.Sin(a + 0.5)));
            }
            dc.DrawEllipse(B("#1C1D23"), null, centre, 9, 9);
        }
        void CenteredLabel(string text, double x, double y, double size, string colour)
        {
            var ft = new FormattedText(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, Typefaces.Body(FontWeights.Normal), size, B(colour), VisualTreeHelper.GetDpi(this).PixelsPerDip);
            dc.DrawText(ft, new Point(x - ft.Width / 2, y));
        }
        void Label(string text, double x, double y, double size)
        {
            var ft = new FormattedText(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, Typefaces.Body(FontWeights.Normal), size, B("#7C808A"), VisualTreeHelper.GetDpi(this).PixelsPerDip);
            dc.DrawText(ft, new Point(x, y));
        }
        Brush Colour(RgbDeviceState device, int index)
        {
            if (model?.State is not { } state || !device.Connected || device.LedCount == 0 || !device.Synced && !device.CustomEnabled) return B("#252731");
            if (rendered.TryGetValue(device.Id, out var cached))
            { var known = cached[Math.Clamp(index, 0, cached.Length - 1)]; return new SolidColorBrush(Color.FromRgb(known.R, known.G, known.B)); }
            var colors = LiveColors(model, state, device, devices, time, sensorLighting);
            if (colors.Length == 0) return B("#252731");
            rendered[device.Id] = colors;
            var c = colors[Math.Clamp(index, 0, colors.Length - 1)]; return new SolidColorBrush(Color.FromRgb(c.R, c.G, c.B));
        }
    }
}
