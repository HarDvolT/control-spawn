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
    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromSeconds(1d / 20) };
    public CaseIllustration()
    {
        Height = 400;
        timer.Tick += (_, _) => { if (!Motion.ReduceAnimations) InvalidateVisual(); };
        DataContextChanged += (_, _) => { if (model is not null) model.PropertyChanged -= Changed; model = DataContext as LightingViewModel; if (model is not null) model.PropertyChanged += Changed; InvalidateVisual(); };
        Motion.WhileVisible(this, timer.Start, timer.Stop);
        Unloaded += (_, _) => { if (model is not null) model.PropertyChanged -= Changed; };
        Loaded += (_, _) => { if (model is not null) { model.PropertyChanged -= Changed; model.PropertyChanged += Changed; } };
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

    protected override void OnRender(DrawingContext dc)
    {
        dc.PushTransform(new ScaleTransform(ActualWidth / 640, ActualHeight / 400));
        var devices = model?.State?.Lighting.Devices ?? [];
        var rendered = new Dictionary<string, RgbColor[]>();
        double time = Motion.ReduceAnimations ? 1.25 : clock.Elapsed.TotalSeconds;
        var ram = devices.Where(d => d.Kind.Contains("RAM", StringComparison.OrdinalIgnoreCase)).ToArray();
        var gpu = devices.FirstOrDefault(d => d.Kind.Contains("GPU", StringComparison.OrdinalIgnoreCase));
        var board = devices.FirstOrDefault(d => d.Kind.Contains("Motherboard", StringComparison.OrdinalIgnoreCase));
        var strip = devices.FirstOrDefault(d => d.Kind.Contains("strip", StringComparison.OrdinalIgnoreCase));
        // On most boards the ARGB headers are zones of the motherboard. Used headers light, in order: pump, side fans, bottom edge.
        var headers = (board?.Zones ?? []).Select((z, i) => (Zone: z, Index: i))
            .Where(z => z.Zone.MaxLeds > z.Zone.MinLeds && z.Zone.LedCount > 0).ToArray();
        (RgbDeviceState Device, RgbZoneState Zone, int Header)? Part(int order) => board is not null && order < headers.Length ? (board, headers[order].Zone, headers[order].Index) : null;
        var pump = Part(0); var fans = Part(1); var tail = Part(2);

        // Case shell and motherboard tray.
        dc.DrawRoundedRectangle(B("#0E0F13"), new Pen(B("#2A2C33"), 1.5), new Rect(18, 18, 500, 364), 24, 24);
        dc.DrawRoundedRectangle(B("#121318"), new Pen(B("#1E2026"), 1), new Rect(40, 40, 318, 236), 16, 16);
        Label(board is null ? "MOTHERBOARD" : Short(board.Name), 56, 52, 10);
        if (board is not null)
        {
            dc.PushTransform(new TranslateTransform(318, 50));
            dc.DrawGeometry(Colour(board, 0), null, Geometry.Parse("M0,0 L26,-8 16,16Z")); dc.Pop();
        }
        // AIO pump ring on the CPU.
        FanRing(new Point(126, 150), 46, pump, 18);
        Label(pump is { } p1 ? $"AIO PUMP · HEADER {p1.Header}" : "AIO PUMP", 74, 210, 10);
        // RAM sticks.
        for (int i = 0; i < 2; i++)
        {
            var stick = new Rect(222 + i * 30, 82, 18, 150);
            dc.DrawRoundedRectangle(B("#17181D"), new Pen(B("#24262D"), 1), stick, 5, 5);
            if (i < ram.Length)
                for (int j = 0; j < ram[i].LedCount && j < 10; j++)
                    dc.DrawRoundedRectangle(Colour(ram[i], j), null, new Rect(stick.X + 4, stick.Y + 6 + j * 14, 10, 10), 3, 3);
        }
        Label("RAM", 237, 240, 10);
        // Graphics card.
        dc.DrawRoundedRectangle(B("#15161B"), new Pen(B("#24262D"), 1), new Rect(40, 290, 318, 48), 12, 12);
        if (gpu is not null)
            for (int i = 0; i < 24; i++)
                dc.DrawRoundedRectangle(Colour(gpu, i * gpu.LedCount / 24), null, new Rect(54 + i * 12.2, 298, 9, 5), 2, 2);
        Label(gpu is null ? "GRAPHICS CARD" : Short(gpu.Name), 56, 314, 11);
        // Radiator fans on the side.
        for (int i = 0; i < 3; i++)
            FanRing(new Point(446, 92 + i * 102), 40, fans is { } f ? (f.Device, Slice(f.Zone, i, 3), f.Header) : null, 16);
        Label(fans is { } f2 ? $"SIDE FANS · HEADER {f2.Header}" : "SIDE FANS", 390, 356, 10);
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
        dc.Pop();

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
            var scene = new Scene("Preview", state.Lighting.Effect, Palettes.Parse(state.Lighting.Color), state.Lighting.Speed, state.Lighting.Brightness);
            var positions = CaseMap.Positions(device, CaseMap.Placement(model.Map, device, devices), model.Map);
            var input = state.Lighting.Inputs;
            var colors = SpatialEffects.Render(scene, model.Parameters, time, positions, device.Id, device.Kind,
                new ReactiveFrame(state.System.CpuLoad ?? 0, state.System.GpuLoad ?? 0,
                    state.System.RamTotalGb > 0 ? (state.System.RamUsedGb ?? 0) / state.System.RamTotalGb.Value : 0,
                    state.System.CpuTemp ?? double.NaN, state.System.GpuTemp ?? double.NaN, input?.AudioBands ?? [], input?.Beat ?? 0,
                    input is null ? RgbColor.Black : Palettes.Parse(input.ScreenLeft), input is null ? RgbColor.Black : Palettes.Parse(input.ScreenCentre), input is null ? RgbColor.Black : Palettes.Parse(input.ScreenRight)));
            colors = LedLayers.Composite(colors, device.CustomColors, device.OverlayColors, device.CustomEnabled).ToArray();
            colors = sensorLighting.Composite(device, state.Lighting.Bindings, state, time, colors, scene.Brightness).ToArray();
            if (colors.Length == 0) return B("#252731");
            rendered[device.Id] = colors;
            var c = colors[Math.Clamp(index, 0, colors.Length - 1)]; return new SolidColorBrush(Color.FromRgb(c.R, c.G, c.B));
        }
    }
}
