using Prism.Core;
using Prism.Core.Ipc;

namespace Prism.Lighting;

public static class LightingValidation
{
    public static void Parameters(EffectParameters p)
    {
        if (p is null || !Palettes.BuiltIn.ContainsKey(p.Palette ?? "") ||
            !Finite(p.Angle, 0, 360) || !Finite(p.Scale, 0.1, 8) || !Finite(p.Intensity, 0, 1) ||
            p.CometCount is < 1 or > 8 || !Finite(p.TailLength, 0.02, 1) ||
            p.Devices?.Any(d => string.IsNullOrWhiteSpace(d.Key) || d.Key.Length > 256) == true)
            throw new ArgumentException("Invalid effect parameters.");
        if (p.Stops is { } stops && (stops.Count is < 2 or > 6 ||
            stops.Any(s => s is null || !Finite(s.Position, 0, 1) || !LedLayers.Valid(s.Color, false)) ||
            stops.Zip(stops.Skip(1)).Any(s => s.First.Position >= s.Second.Position)))
            throw new ArgumentException("Use 2 to 6 ordered palette stops with unique positions in 0..1.");
    }
    public static void Map(LightingMap map)
    {
        if (map is null || map.Devices is null || map.Devices.Count > 256 ||
            map.Devices.Any(d => d is null || string.IsNullOrWhiteSpace(d.Device) || d.Device.Length > 256 ||
                d.Zone is < 0 || !Finite(d.X, 0, 1) || !Finite(d.Y, 0, 1) || !Finite(d.Length, 0.01, 1) || !Finite(d.Rotation, 0, 360)) ||
            map.Devices.Select(d => (d.Device, d.Zone)).Distinct().Count() != map.Devices.Count ||
            map.Lamp is { } lamp && (!Finite(lamp.X, 0, 1) || !Finite(lamp.Y, 0, 1)))
            throw new ArgumentException("Invalid case map.");
    }
    private static bool Finite(double x, double min, double max) => double.IsFinite(x) && x >= min && x <= max;
}

public static class CaseMap
{
    public static LightingMap Default(IReadOnlyList<RgbDeviceState> devices)
    {
        int ram = 0, other = 0;
        var placements = devices.Select(d => d.Kind.Contains("RAM", StringComparison.OrdinalIgnoreCase)
            ? new DevicePlacement(d.Id, 0.58 + 0.10 * (ram++ % 3), 0.35, 0.42, 90)
            : d.Kind.Contains("GPU", StringComparison.OrdinalIgnoreCase) || d.Name.Contains("GPU", StringComparison.OrdinalIgnoreCase)
                ? new DevicePlacement(d.Id, 0.43, 0.70, 0.48, 0)
                : d.Kind.Contains("strip", StringComparison.OrdinalIgnoreCase) || d.Name.Contains("strip", StringComparison.OrdinalIgnoreCase)
                    ? new DevicePlacement(d.Id, 0.5, 0.12, 0.66, 0)
                    : new DevicePlacement(d.Id, 0.25 + 0.05 * (other++ % 4), 0.40, 0.4, 90)).ToList();
        foreach (var device in devices)
            for (int z = 0; z < (device.Zones?.Count ?? 0); z++)
                if (device.Zones![z].MaxLeds > device.Zones[z].MinLeds) placements.Add(new(device.Id, 0.5, 0.12, 0.66, 0, Zone: z));
        return new(placements);
    }
    public static DevicePlacement Placement(LightingMap? map, RgbDeviceState device, IReadOnlyList<RgbDeviceState> all) =>
        map?.Devices.FirstOrDefault(d => d.Device == device.Id && d.Zone is null) ?? Default(all).Devices.First(d => d.Device == device.Id && d.Zone is null);
    public static MapPoint[] Positions(RgbDeviceState device, DevicePlacement placement, LightingMap? map = null)
    {
        var result = new MapPoint[device.LedCount];
        for (int i = 0; i < result.Length; i++)
        {
            double along = result.Length <= 1 ? 0 : (double)i / (result.Length - 1) - 0.5, across = 0;
            var zone = device.Zones?.FirstOrDefault(z => i >= z.FirstLed && i < z.FirstLed + z.LedCount);
            // Board ARGB headers are an upper case line, separate from the board LEDs.
            if (zone is not null)
                along = zone.LedCount <= 1 ? 0 : (double)(i - zone.FirstLed) / (zone.LedCount - 1) - 0.5;
            if (zone?.Matrix is { } matrix && zone.MatrixWidth > 0 && zone.MatrixHeight > 0)
            {
                int cell = -1;
                for (int j = 0; j < matrix.Count; j++) if (matrix[j] == i) { cell = j; break; }
                if (cell >= 0) { along = (double)(cell % zone.MatrixWidth) / Math.Max(1, zone.MatrixWidth - 1) - 0.5; across = (double)(cell / zone.MatrixWidth) / Math.Max(1, zone.MatrixHeight - 1) - 0.5; }
            }
            var effective = placement;
            int zoneIndex = zone is null || device.Zones is null ? -1 : device.Zones.ToList().IndexOf(zone);
            if (zone is not null && zone.MaxLeds > zone.MinLeds)
                effective = map?.Devices.FirstOrDefault(d => d.Device == device.Id && d.Zone == zoneIndex)
                    ?? new(device.Id, 0.5, 0.12, 0.66, 0, Zone: zoneIndex);
            if (effective.Flip) along = -along;
            double a = effective.Rotation * Math.PI / 180;
            double x = effective.X + effective.Length * (along * Math.Cos(a) - across * Math.Sin(a));
            double y = effective.Y + effective.Length * (along * Math.Sin(a) + across * Math.Cos(a));
            result[i] = new(Math.Clamp(x, 0, 1), Math.Clamp(y, 0, 1));
        }
        return result;
    }
    public static MapPoint LampPoint(LightingMap? map, IReadOnlyList<RgbDeviceState> devices)
    {
        if (map?.Lamp is { } point) return point;
        var strip = devices.FirstOrDefault(d => d.Zones?.Any(z => z.MaxLeds > z.MinLeds) == true || d.Kind.Contains("strip", StringComparison.OrdinalIgnoreCase));
        if (strip?.Zones?.Any(z => z.MaxLeds > z.MinLeds) == true)
        {
            int index = strip.Zones.ToList().FindIndex(z => z.MaxLeds > z.MinLeds);
            var p = map?.Devices.FirstOrDefault(d => d.Device == strip.Id && d.Zone == index);
            return p is null ? new(0.5, 0.12) : new(p.X, p.Y);
        }
        if (strip is not null) { var p = Placement(map, strip, devices); return new(p.X, p.Y); }
        return new(0.5, 0.12);
    }
}

public static class Palettes
{
    public static IReadOnlyDictionary<string, IReadOnlyList<PaletteStop>> BuiltIn { get; } = new Dictionary<string, IReadOnlyList<PaletteStop>> {
        ["Aurora"] = Stops("#063C38", "#29FCA8", "#28AACC", "#A665F6"),
        ["Fire"] = Stops("#210600", "#CA2907", "#FF9A13", "#FFF0A0"),
        ["Ocean"] = Stops("#031C4B", "#096DAD", "#30DDD0", "#C7FFFF"),
        ["Spectrum"] = Stops("#FF315A", "#FFBA32", "#53E08E", "#588AFF", "#BD64F5"),
        ["Candle"] = Stops("#9E3106", "#FFB94D", "#FFE2A3") };
    public static string ForEffect(LightingEffect effect) => effect switch {
        LightingEffect.Fire => "Fire", LightingEffect.Candle => "Candle", LightingEffect.OceanRipple => "Ocean",
        LightingEffect.Rain => "Aurora", LightingEffect.RainbowWave or LightingEffect.ColorCycle or LightingEffect.GradientFlow or LightingEffect.Plasma => "Spectrum", _ => "Aurora" };
    private static PaletteStop[] Stops(params string[] colors) => colors.Select((c, i) => new PaletteStop((double)i / (colors.Length - 1), c)).ToArray();
    public static RgbColor Parse(string hex) => new(Convert.ToByte(hex.Substring(1, 2), 16), Convert.ToByte(hex.Substring(3, 2), 16), Convert.ToByte(hex.Substring(5, 2), 16));
    public static RgbColor Sample(IReadOnlyList<PaletteStop> stops, double t)
    {
        t = Math.Clamp(double.IsFinite(t) ? t : 0, 0, 1);
        if (t <= stops[0].Position) return Parse(stops[0].Color);
        for (int i = 1; i < stops.Count; i++) if (t <= stops[i].Position)
            return RgbColor.Lerp(Parse(stops[i - 1].Color), Parse(stops[i].Color), Math.Round((t - stops[i - 1].Position) / (stops[i].Position - stops[i - 1].Position), 12));
        return Parse(stops[^1].Color);
    }
}

public sealed record ReactiveFrame(double CpuLoad = 0, double GpuLoad = 0, double RamUse = 0,
    double CpuTemp = 40, double GpuTemp = 40, IReadOnlyList<double>? Audio = null, double Beat = 0,
    RgbColor? ScreenLeft = null, RgbColor? ScreenCentre = null, RgbColor? ScreenRight = null);

/// <summary>Pure, seek-safe rendering. No I/O, wall clock, random state or device-index phase.</summary>
public static class SpatialEffects
{
    public static RgbColor[] Render(Scene scene, EffectParameters p, double time, IReadOnlyList<MapPoint> positions,
        string device = "", string kind = "", ReactiveFrame? input = null)
    {
        LightingValidation.Parameters(p);
        if (!double.IsFinite(time) || !double.IsFinite(scene.Speed) || scene.Speed is < 0 or > 2 || !double.IsFinite(scene.Brightness) || scene.Brightness is < 0 or > 1)
            throw new ArgumentException("Invalid render values.");
        input ??= new();
        if (p.Devices?.GetValueOrDefault(device, true) == false) return new RgbColor[positions.Count];
        double t = time * scene.Speed, angle = p.Angle * Math.PI / 180;
        var stops = p.Stops ?? Palettes.BuiltIn[p.Palette];
        RgbColor Palette(double v) => Palettes.Sample(stops, v);
        var result = new RgbColor[positions.Count];
        for (int i = 0; i < result.Length; i++)
        {
            var pos = positions[i]; double x = pos.X * p.Scale, y = pos.Y * p.Scale;
            double path = (pos.X * Math.Cos(angle) + pos.Y * Math.Sin(angle)) * p.Scale;
            double n = Noise(x * 3 + t * 0.25, y * 3 - t * 0.4);
            RgbColor c;
            switch (scene.Effect)
            {
                case LightingEffect.Off: case LightingEffect.Custom: c = RgbColor.Black; break;
                case LightingEffect.Static: c = scene.Color; break;
                case LightingEffect.Breathing: c = Scale(scene.Color, (1 - Math.Cos(t * Math.PI)) / 2); break;
                case LightingEffect.Strobing: c = Frac(t * 2) < 0.5 ? scene.Color : RgbColor.Black; break;
                case LightingEffect.ColorCycle: c = Palette(Frac(t / 8)); break;
                case LightingEffect.RainbowWave: case LightingEffect.GradientFlow: c = Palette(Frac(path - t / 5)); break;
                case LightingEffect.Aurora:
                    c = Scale(Palette(n), 0.25 + 0.75 * Math.Pow((Math.Sin(y * 9 + Noise(x * 2, t * 0.12) * 5 + t * 0.3) + 1) / 2, 2)); break;
                case LightingEffect.Comet:
                    double distance = Frac(t * 0.3 - path * p.CometCount);
                    c = Scale(Palette(Frac(path)), Math.Pow(Math.Max(0, 1 - distance / p.TailLength), 2)); break;
                case LightingEffect.Fire: c = Scale(Palette(Math.Clamp((1 - pos.Y) * 0.35 + n * 0.75, 0, 1)), 0.5 + n * 0.5); break;
                case LightingEffect.OceanRipple:
                    double radius = Math.Sqrt(Math.Pow(pos.X - 0.5, 2) + Math.Pow(pos.Y - 0.5, 2));
                    c = Palette((Math.Sin(radius * 22 * p.Scale - t * 2) + 1) / 2); break;
                case LightingEffect.Plasma: c = Palette((Math.Sin(x * 8 + t) + Math.Sin(y * 7 - t * 0.7) + Math.Sin((x + y) * 6 + t * 0.4) + 3) / 6); break;
                case LightingEffect.Scanner:
                    double head = 1 - Math.Abs(Frac(t / 3) * 2 - 1);
                    c = Scale(Palette(head), Math.Exp(-Math.Abs(Frac(path) - head) * 22)); break;
                case LightingEffect.Rain:
                    double column = Math.Floor(pos.X * 16), drop = Frac(y * 1.5 - t * 0.65 + Hash(column, 3));
                    c = Scale(Palette(drop), Math.Pow(drop, 10)); break;
                case LightingEffect.Heartbeat:
                    double rate = p.HeartbeatCpu ? 0.8 + Clamp(input.CpuLoad / 100) * 1.6 : 1;
                    double phase = Frac(t * rate);
                    c = Scale(scene.Color, Math.Exp(-Math.Pow((phase - 0.12) / 0.045, 2)) + 0.65 * Math.Exp(-Math.Pow((phase - 0.32) / 0.065, 2))); break;
                case LightingEffect.Candle: c = Scale(Palette(0.55 + 0.25 * Noise(0, t * 2)), 0.7 + 0.3 * Noise(4, t * 5)); break;
                case LightingEffect.Sparkle: case LightingEffect.StarryNight:
                    double star = Math.Pow(Math.Max(0, Math.Sin(t * 1.7 + Hash(pos.X * 97, pos.Y * 101) * 15)), 16);
                    c = RgbColor.Lerp(Scale(scene.Color, 0.08), Palette(n), star); break;
                case LightingEffect.LoadMeters:
                    double load = kind.Contains("RAM", StringComparison.OrdinalIgnoreCase) ? input.CpuLoad / 100 : kind.Contains("GPU", StringComparison.OrdinalIgnoreCase) ? input.GpuLoad / 100 : input.RamUse;
                    bool vertical = kind.Contains("RAM", StringComparison.OrdinalIgnoreCase);
                    double min = positions.Min(v => vertical ? v.Y : v.X), max = positions.Max(v => vertical ? v.Y : v.X);
                    double level = max - min < 0.00001 ? 0.5 : vertical ? (max - pos.Y) / (max - min) : (pos.X - min) / (max - min);
                    c = Scale(MeterColor(level), Clamp((Clamp(load) - level) * 24 + 0.5));
                    c = RgbColor.Lerp(c, new(255, 255, 220), Math.Exp(-Math.Pow((level - Clamp(load)) * 45, 2))); break;
                case LightingEffect.TemperatureFlow: case LightingEffect.Smart:
                    double temp = kind.Contains("GPU", StringComparison.OrdinalIgnoreCase) ? input.GpuTemp : input.CpuTemp;
                    c = Scale(RgbColor.Lerp(new(0, 128, 255), new(255, 32, 0), double.IsFinite(temp) ? (temp - 40) / 45 : 1), 0.85 + 0.15 * Math.Sin(t + path * 4)); break;
                case LightingEffect.AudioSpectrum: case LightingEffect.Music:
                    var bands = input.Audio; double band = bands is { Count: > 0 } ? bands[Math.Min(bands.Count - 1, (int)(Clamp(pos.X) * bands.Count))] : 0;
                    c = RgbColor.Lerp(Scale(Palette(1 - pos.Y), Clamp((band - (1 - pos.Y)) * 12)), new(255, 255, 255), Clamp(input.Beat) * 0.4); break;
                case LightingEffect.ScreenAmbient: case LightingEffect.Adaptive:
                    var left = input.ScreenLeft ?? RgbColor.Black; var centre = input.ScreenCentre ?? left; var right = input.ScreenRight ?? centre;
                    c = pos.X <= 0.5 ? RgbColor.Lerp(left, centre, pos.X * 2) : RgbColor.Lerp(centre, right, (pos.X - 0.5) * 2); break;
                default: c = scene.Color; break;
            }
            result[i] = Scale(c, scene.Brightness * p.Intensity);
        }
        return result;
    }
    public static RgbColor MeterColor(double t) => t < 0.65 ? RgbColor.Lerp(new(30, 220, 90), new(255, 170, 20), t / 0.65) : RgbColor.Lerp(new(255, 170, 20), new(255, 25, 25), (t - 0.65) / 0.35);
    public static RgbColor Scale(RgbColor c, double v) => RgbColor.Lerp(RgbColor.Black, c, Clamp(v));
    private static double Clamp(double x) => double.IsFinite(x) ? Math.Clamp(x, 0, 1) : 0;
    private static double Frac(double x) => x - Math.Floor(x);
    private static double Hash(double x, double y) => Frac(Math.Sin(x * 127.1 + y * 311.7) * 43758.5453);
    private static double Noise(double x, double y)
    {
        double ix = Math.Floor(x), iy = Math.Floor(y), fx = Frac(x), fy = Frac(y);
        fx = fx * fx * (3 - 2 * fx); fy = fy * fy * (3 - 2 * fy);
        double a = Hash(ix, iy) * (1 - fx) + Hash(ix + 1, iy) * fx, b = Hash(ix, iy + 1) * (1 - fx) + Hash(ix + 1, iy + 1) * fx;
        return a * (1 - fy) + b * fy;
    }
}
