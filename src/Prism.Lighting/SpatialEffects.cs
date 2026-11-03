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
                d.Zone is < 0 || d.Rings is < 0 or > 8 || d.FirstRing is { } fr && (!Finite(fr.X, 0, 1) || !Finite(fr.Y, 0, 1)) || !Finite(d.X, 0, 1) || !Finite(d.Y, 0, 1) || !Finite(d.Length, 0.01, 1) || !Finite(d.Rotation, 0, 360)) ||
            map.Devices.Select(d => (d.Device, d.Zone)).Distinct().Count() != map.Devices.Count ||
            map.Lamp is { } lamp && (!Finite(lamp.X, 0, 1) || !Finite(lamp.Y, 0, 1)) ||
            map.Lamps is not null && (map.Lamps.Count > 256 || map.Lamps.Any(p => string.IsNullOrWhiteSpace(p.Key) || p.Value is null || !Finite(p.Value.X, 0, 1) || !Finite(p.Value.Y, 0, 1))))
            throw new ArgumentException("Invalid case map.");
    }
    private static bool Finite(double x, double min, double max) => double.IsFinite(x) && x >= min && x <= max;
}

public static class CaseMap
{
    public static LightingMap Default(IReadOnlyList<RgbDeviceState> devices)
    {
        // Matches the case picture: RAM beside the CPU, the graphics card under the board, the board's logo on its right.
        int ram = 0, other = 0;
        var placements = devices.Select(d => d.Kind.Contains("RAM", StringComparison.OrdinalIgnoreCase)
            ? new DevicePlacement(d.Id, 0.426 + 0.06 * (ram++ % 4), 0.478, 0.35, 90)
            : d.Kind.Contains("GPU", StringComparison.OrdinalIgnoreCase) || d.Name.Contains("GPU", StringComparison.OrdinalIgnoreCase)
                ? new DevicePlacement(d.Id, 0.36, 0.80, 0.56, 0)
                : d.Kind.Contains("strip", StringComparison.OrdinalIgnoreCase) || d.Name.Contains("strip", StringComparison.OrdinalIgnoreCase)
                    ? new DevicePlacement(d.Id, 0.5, 0.12, 0.66, 0)
                    : new DevicePlacement(d.Id, 0.62 - 0.05 * (other++ % 4), 0.47, 0.05, 90)).ToList();
        foreach (var device in devices)
            for (int z = 0; z < (device.Zones?.Count ?? 0); z++)
                if (device.Zones![z].MaxLeds > device.Zones[z].MinLeds) placements.Add(HeaderDefault(device, z));
        return new(placements);
    }
    /// <summary>
    /// Saved placements that are only the first layout's defaults (RAM at the far right, the board in the middle, the GPU
    /// above its slot) were never placed by anyone, so they follow the current defaults instead.
    /// </summary>
    private static bool IsLegacyDevicePlacement(DevicePlacement p)
    {
        static bool Near(double a, double b) => Math.Abs(a - b) < 1e-6;
        if (p.Zone is not null || p.Flip || p.Rings != 0) return false;
        return Near(p.Y, 0.35) && Near(p.Length, 0.42) && Near(p.Rotation, 90) && (Near(p.X, 0.58) || Near(p.X, 0.68) || Near(p.X, 0.78))
            || Near(p.X, 0.43) && Near(p.Y, 0.70) && Near(p.Length, 0.48) && Near(p.Rotation, 0)
            || Near(p.Y, 0.40) && Near(p.Length, 0.4) && Near(p.Rotation, 90) && (Near(p.X, 0.25) || Near(p.X, 0.30) || Near(p.X, 0.35) || Near(p.X, 0.40));
    }
    /// <summary>The map with every never-placed entry swapped for today's default, as the editor should show and drag it.</summary>
    public static LightingMap Normalize(LightingMap map, IReadOnlyList<RgbDeviceState> devices)
    {
        var defaults = Default(devices).Devices;
        DevicePlacement Fix(DevicePlacement p)
        {
            if (!IsLegacyDevicePlacement(p) && !IsLegacyHeaderLine(p)) return p;
            return defaults.FirstOrDefault(d => d.Device == p.Device && d.Zone == p.Zone) ?? p;
        }
        var saved = map.Devices.Select(Fix).ToList();
        saved.AddRange(defaults.Where(d => !saved.Any(s => s.Device == d.Device && s.Zone == d.Zone)));
        return map with { Devices = saved };
    }
    /// <summary>The line every header shared before headers had their own places; a saved one is treated as never placed.</summary>
    private static bool IsLegacyHeaderLine(DevicePlacement p) => p is { X: 0.5, Y: 0.12, Length: 0.66, Rotation: 0, Flip: false, Rings: 0 };
    /// <summary>
    /// Where a board ARGB header sits until the user places it, matching the case picture and the PC it was built for.
    /// The first used header carries the AIO: its pump fan, then the radiator fans chained after it (12 LEDs each)
    /// across the top. The second runs the back and front case fans down the right; any further one the bottom edge.
    /// </summary>
    public static DevicePlacement HeaderDefault(RgbDeviceState device, int zone)
    {
        var zones = device.Zones ?? [];
        var used = zones.Select((z, i) => (Zone: z, Index: i)).Where(z => z.Zone.MaxLeds > z.Zone.MinLeds && z.Zone.LedCount > 0).Select(z => z.Index).ToList();
        int leds = zone >= 0 && zone < zones.Count ? zones[zone].LedCount : 0;
        return used.IndexOf(zone) switch
        {
            0 when leds > 12 => new(device.Id, 0.32, 0.11, 0.48, 0, Zone: zone, Rings: Math.Min(8, Math.Max(2, leds / 12)), FirstRing: new(0.22, 0.47)),
            0 => new(device.Id, 0.22, 0.47, 0.16, 0, Zone: zone, Rings: 1),
            1 => new(device.Id, 0.86, 0.48, 0.84, 90, Zone: zone, Rings: 3),
            2 => new(device.Id, 0.40, 0.92, 0.62, 0, Zone: zone),
            _ => new(device.Id, 0.5, 0.06, 0.66, 0, Zone: zone)
        };
    }
    /// <summary>What a person calls a part of a device: the board's own LEDs, or what its ARGB headers drive here.</summary>
    public static string PartName(RgbDeviceState device, int zone)
    {
        var zones = device.Zones ?? [];
        if (zone < 0 || zone >= zones.Count) return device.Name;
        if (zones[zone].MaxLeds <= zones[zone].MinLeds) return device.Kind.Contains("Motherboard", StringComparison.OrdinalIgnoreCase) ? "Board logo" : zones[zone].Name;
        var used = zones.Select((z, i) => (Zone: z, Index: i)).Where(z => z.Zone.MaxLeds > z.Zone.MinLeds && z.Zone.LedCount > 0).Select(z => z.Index).ToList();
        return used.IndexOf(zone) switch
        {
            0 => zones[zone].LedCount > 12 ? "Pump & radiator" : "Pump fan",
            1 => "Case fans",
            _ => $"ARGB header {zone}"
        };
    }
    /// <summary>
    /// The pieces a person tells apart in one zone: the AIO header splits into its pump ring (the first 12 LEDs) and the
    /// radiator fans after it; any other zone is one piece. Each piece has its own part key (see SensorSources).
    /// </summary>
    public static IReadOnlyList<(string Name, int First, int Count, string Key)> Pieces(RgbDeviceState device, int zone)
    {
        var z = device.Zones![zone]; var name = PartName(device, zone);
        if (name == "Pump & radiator")
            return [("Pump", z.FirstLed, 12, SensorSources.RangeTarget(device.Id, z.FirstLed, 12)),
                ("Radiator", z.FirstLed + 12, z.LedCount - 12, SensorSources.RangeTarget(device.Id, z.FirstLed + 12, z.LedCount - 12))];
        return [(name, z.FirstLed, z.LedCount, SensorSources.ZoneTarget(device.Id, zone))];
    }
    public static DevicePlacement Placement(LightingMap? map, RgbDeviceState device, IReadOnlyList<RgbDeviceState> all) =>
        map?.Devices.FirstOrDefault(d => d.Device == device.Id && d.Zone is null) is { } saved && !IsLegacyDevicePlacement(saved)
            ? saved : Default(all).Devices.First(d => d.Device == device.Id && d.Zone is null);
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
            {
                effective = map?.Devices.FirstOrDefault(d => d.Device == device.Id && d.Zone == zoneIndex) ?? HeaderDefault(device, zoneIndex);
                if (IsLegacyHeaderLine(effective)) effective = HeaderDefault(device, zoneIndex);
            }
            if (effective.Flip) along = -along;
            double a = effective.Rotation * Math.PI / 180;
            double ringX = 0, ringY = 0; MapPoint? centre = null;
            if (effective.Rings > 0 && zone is { LedCount: > 0 })
            {
                // Fans: each ring's LEDs go round its centre, clockwise from the top like the real fan, and the rings sit along the line.
                int local = i - zone.FirstLed, perRing = Math.Max(1, zone.LedCount / effective.Rings), ring = Math.Min(effective.Rings - 1, local / perRing);
                int onLine = effective.FirstRing is null ? effective.Rings : Math.Max(1, effective.Rings - 1), slot = effective.FirstRing is null ? ring : ring - 1;
                along = (slot + 0.5) / onLine - 0.5; across = 0;
                if (effective.Flip) along = -along;
                double spin = (local - ring * perRing) * 2 * Math.PI / perRing - Math.PI / 2, radius = effective.Length / onLine * 0.4;
                ringX = radius * Math.Cos(spin); ringY = radius * Math.Sin(spin);
                if (effective.FirstRing is { } first && ring == 0) centre = first;
            }
            double x = (centre?.X ?? effective.X + effective.Length * (along * Math.Cos(a) - across * Math.Sin(a))) + ringX;
            double y = (centre?.Y ?? effective.Y + effective.Length * (along * Math.Sin(a) + across * Math.Cos(a))) + ringY;
            result[i] = new(Math.Clamp(x, 0, 1), Math.Clamp(y, 0, 1));
        }
        return result;
    }
    public static MapPoint LampPoint(LightingMap? map, IReadOnlyList<RgbDeviceState> devices, string id, int index)
    {
        if (map?.Lamps?.GetValueOrDefault(id) is { } point) return point;
        // Further lamps start in the free top-right corner, under the first, clear of the fans, until someone drags them.
        return index == 0 ? LampPoint(map, devices) : new(0.64, Math.Min(0.22, 0.07 + index * 0.13));
    }
    public static MapPoint LampPoint(LightingMap? map, IReadOnlyList<RgbDeviceState> devices)
    {
        // A lamp nobody placed starts in the free top-right corner of the case, between the radiator and the case fans.
        return map?.Lamp ?? new(0.64, 0.07);
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
    /// <summary>
    /// Samples a palette as a loop: after the last stop it blends back to the first over one stop's width, so effects
    /// that cycle through a palette (rainbow, colour cycle, gradient flow) never jump from purple straight to red.
    /// </summary>
    public static RgbColor SampleLoop(IReadOnlyList<PaletteStop> stops, double t)
    {
        t = double.IsFinite(t) ? t - Math.Floor(t) : 0;
        if (stops.Count < 2 || stops[0].Color.Equals(stops[^1].Color, StringComparison.OrdinalIgnoreCase)) return Sample(stops, t);
        double gap = 1d / (stops.Count - 1), u = t * (1 + gap);
        return u <= 1 ? Sample(stops, u) : RgbColor.Lerp(Parse(stops[^1].Color), Parse(stops[0].Color), (u - 1) / gap);
    }
    public static RgbColor Sample(IReadOnlyList<PaletteStop> stops, double t)
    {
        t = Math.Clamp(double.IsFinite(t) ? t : 0, 0, 1);
        if (t <= stops[0].Position) return Parse(stops[0].Color);
        for (int i = 1; i < stops.Count; i++) if (t <= stops[i].Position)
            return RgbColor.Lerp(Parse(stops[i - 1].Color), Parse(stops[i].Color), Math.Round((t - stops[i - 1].Position) / (stops[i].Position - stops[i - 1].Position), 12));
        return Parse(stops[^1].Color);
    }
}

/// <summary>
/// Keeps each look's animation continuous when its speed changes. Effects move by time × speed, so changing the speed
/// used to jump every LED to a different point of the loop; here the position carries on from where it was and only
/// its rate changes. Render with the scene's Speed set to 1 and <see cref="Time"/> as the time.
/// </summary>
public sealed class PhaseClock
{
    private readonly Dictionary<string, (double At, double Phase, double Speed)> looks = new();
    private readonly object gate = new();

    public double Time(string key, double seconds, double speed)
    {
        lock (gate)
        {
            if (!looks.TryGetValue(key, out var look)) looks[key] = look = (seconds, seconds * speed, speed);
            else if (look.Speed != speed) looks[key] = look = (seconds, look.Phase + (seconds - look.At) * look.Speed, speed);
            return look.Phase + (seconds - look.At) * speed;
        }
    }

    /// <summary>The scene to render with <see cref="Time"/>: same look, speed folded into the time.</summary>
    public static Scene Steady(Scene scene) => scene with { Speed = 1 };
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
        RgbColor Loop(double v) => Palettes.SampleLoop(stops, v);
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
                // LEDs are linear but eyes are not: squaring the wave makes the fade look even instead of snapping near black,
                // and a faint floor keeps the case from blinking fully off between breaths.
                case LightingEffect.Breathing: c = Scale(scene.Color, 0.04 + 0.96 * Math.Pow((1 - Math.Cos(t * Math.PI)) / 2, 2)); break;
                case LightingEffect.Strobing: c = Frac(t * 2) < 0.5 ? scene.Color : RgbColor.Black; break;
                case LightingEffect.ColorCycle: c = Loop(t / 8); break;
                case LightingEffect.RainbowWave: case LightingEffect.GradientFlow: c = Loop(path - t / 5); break;
                case LightingEffect.Aurora:
                    c = Scale(Palette(n), 0.25 + 0.75 * Math.Pow((Math.Sin(y * 9 + Noise(x * 2, t * 0.12) * 5 + t * 0.3) + 1) / 2, 2)); break;
                case LightingEffect.Comet:
                    // A white-hot head, a palette tail that fades out, and a dim trail of the palette so the case never goes black.
                    double distance = Frac(t * 0.3 - path * p.CometCount), tail = Math.Pow(Math.Max(0, 1 - distance / p.TailLength), 2);
                    c = RgbColor.Lerp(Scale(Loop(path), 0.08 + 0.92 * tail), new(255, 255, 255), Math.Exp(-distance * 60) * 0.6); break;
                case LightingEffect.Fire: c = Scale(Palette(Math.Clamp((1 - pos.Y) * 0.35 + n * 0.75, 0, 1)), 0.5 + n * 0.5); break;
                case LightingEffect.OceanRipple:
                    double radius = Math.Sqrt(Math.Pow(pos.X - 0.5, 2) + Math.Pow(pos.Y - 0.5, 2));
                    c = Palette((Math.Sin(radius * 22 * p.Scale - t * 2) + 1) / 2); break;
                case LightingEffect.Plasma: c = Palette((Math.Sin(x * 8 + t) + Math.Sin(y * 7 - t * 0.7) + Math.Sin((x + y) * 6 + t * 0.4) + 3) / 6); break;
                case LightingEffect.Scanner:
                    // An eased sweep (it slows at each end like a real scanner) with a wider soft beam over a faint glow.
                    double sweep = 1 - Math.Abs(Frac(t / 3) * 2 - 1), head = sweep * sweep * (3 - 2 * sweep);
                    c = Scale(Palette(head), 0.06 + 0.94 * Math.Exp(-Math.Pow((Frac(path) - head) * 7, 2))); break;
                case LightingEffect.Rain:
                    double column = Math.Floor(pos.X * 16), drop = Frac(y * 1.5 - t * 0.65 + Hash(column, 3));
                    c = Scale(Palette(drop), 0.05 + 0.95 * Math.Pow(drop, 6)); break;
                case LightingEffect.Heartbeat:
                    double rate = p.HeartbeatCpu ? 0.8 + Clamp(input.CpuLoad / 100) * 1.6 : 1;
                    double phase = Frac(t * rate);
                    // Below a few percent the scaled colour loses its hue (an orange becomes a dull red), so the tail snaps to black.
                    double beat = Math.Exp(-Math.Pow((phase - 0.12) / 0.045, 2)) + 0.65 * Math.Exp(-Math.Pow((phase - 0.32) / 0.065, 2));
                    c = beat < 0.04 ? RgbColor.Black : Scale(scene.Color, beat); break;
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
