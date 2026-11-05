using ControlSpawn.Core;
using ControlSpawn.Core.Ipc;

namespace ControlSpawn.Lighting;

public sealed record SensorSource(string Id, string Group, string Name, string Unit, double Min, double Max);
public static class SensorSources
{
    public static IReadOnlyList<SensorSource> All { get; } = [
        new("gpu.temperature", "GPU", "GPU temperature", "°C", 35, 85),
        new("gpu.hotspot", "GPU", "GPU hotspot", "°C", 40, 100),
        new("gpu.memoryJunction", "GPU", "GPU memory junction", "°C", 40, 100),
        new("gpu.load", "GPU", "GPU usage", "%", 0, 100),
        new("gpu.power", "GPU", "GPU power", "W", 15, 300),
        new("gpu.clock", "GPU", "GPU clock", "MHz", 0, 3500),
        new("gpu.vram", "GPU", "VRAM use", "%", 0, 100),
        new("gpu.fan", "GPU", "GPU fan", "RPM", 0, 3500),
        new("cpu.temperature", "CPU", "CPU temperature", "°C", 30, 95),
        new("cpu.load", "CPU", "CPU load", "%", 0, 100),
        new("cpu.power", "CPU", "CPU power", "W", 0, 200),
        new("cpu.clock", "CPU", "CPU clock", "MHz", 0, 6000),
        new("cpu.coreMax", "CPU", "Per-core max load", "%", 0, 100),
        new("ram.use", "RAM", "RAM use", "%", 0, 100),
        new("ram.temperature", "RAM", "DIMM temperature (maximum)", "°C", 25, 80),
        new("board.temperature", "Board", "Board temperature", "°C", 25, 80),
        new("network.down", "Network", "Network down", "Mbps", 0, 1000),
        new("network.up", "Network", "Network up", "Mbps", 0, 1000),
        new("disk.activity", "Disk", "Disk activity (maximum)", "%", 0, 100),
        new("disk.temperature", "Disk", "Disk temperature (maximum)", "°C", 25, 80),
        new("router.signal", "Router", "Router signal score", "%", 0, 100),
        new("router.ping", "Router", "Ping", "ms", 0, 150)
    ];
    public static IEnumerable<SensorSource> Available(StateMessage state) => All.Concat(state.Fans.Select(f =>
        new SensorSource($"fan.{f.Header}", "Fans", f.Name, "RPM", 0, 3000)));
    public static bool Known(string id) => id is not null && (All.Any(s => s.Id == id) ||
        id.StartsWith("fan.", StringComparison.Ordinal) && int.TryParse(id.AsSpan(4), out var n) && n is >= 1 and <= 255);
    public static double? Read(StateMessage state, string id)
    {
        var s = state.System;
        double? value = id switch {
            "gpu.temperature" => s.GpuTemp, "gpu.hotspot" => s.GpuHotspot, "gpu.memoryJunction" => s.GpuMemoryJunction,
            "gpu.load" => s.GpuLoad, "gpu.power" => s.GpuPowerW, "gpu.clock" => s.GpuClockMhz,
            "gpu.vram" => s.VramTotalGb > 0 ? s.VramUsedGb / s.VramTotalGb * 100 : null, "gpu.fan" => s.GpuFanRpm,
            "cpu.temperature" => s.CpuTemp, "cpu.load" => s.CpuLoad, "cpu.power" => s.CpuPowerW, "cpu.clock" => s.CpuClockMhz,
            "cpu.coreMax" => s.CpuCoreLoads.Max(), "ram.use" => s.RamTotalGb > 0 ? s.RamUsedGb / s.RamTotalGb * 100 : null,
            "ram.temperature" => s.DimmTemps.Max(), "board.temperature" => s.BoardTemp,
            "network.down" => s.NetDownMbps, "network.up" => s.NetUpMbps,
            "disk.activity" => s.Disks.Select(d => d.ActivityPercent).DefaultIfEmpty().Max(),
            "disk.temperature" => s.Disks.Select(d => d.TempC).DefaultIfEmpty().Max(),
            "router.signal" => state.Router is { Connected: true } r ? r.Score : null,
            "router.ping" => state.Router is { Connected: true } r ? r.PingMs : null,
            _ => id.StartsWith("fan.", StringComparison.Ordinal) && int.TryParse(id.AsSpan(4), out var n) ? state.Fans.FirstOrDefault(f => f.Header == n)?.Rpm : null
        };
        return value is { } v && double.IsFinite(v) ? v : null;
    }
    public static void Validate(SensorBinding b)
    {
        if (!Known(b.Source) || !double.IsFinite(b.Min) || !double.IsFinite(b.Max) || b.Min >= b.Max || !double.IsFinite(b.Max - b.Min) ||
            !double.IsFinite(b.Smoothing) || b.Smoothing is < 0 or > 5 || !Enum.IsDefined(b.Style) ||
            !double.IsFinite(b.Hysteresis) || b.Hysteresis < 0 ||
            b.Alert is { } a && !double.IsFinite(a.Threshold)) throw new ArgumentException("Invalid sensor binding.");
        var palette = b.Palette ?? SensorLighting.DefaultPalette;
        if (palette.Count is < 2 or > 6 || palette.Any(p => p is null) || palette[0].Position != 0 || palette[^1].Position != 1 ||
            palette.Any(p => !double.IsFinite(p.Position) || p.Position is < 0 or > 1 || !LedLayers.Valid(p.Color, false)) ||
            palette.Zip(palette.Skip(1)).Any(p => p.First.Position >= p.Second.Position)) throw new ArgumentException("Invalid sensor palette (2–6 ordered stops from 0 to 1).");
    }
    public static string ZoneTarget(string device, int zone) => $"{device}/zone/{zone}";
    /// <summary>A run of a device's LEDs as its own part, such as the pump ring at the start of the AIO header.</summary>
    public static string RangeTarget(string device, int first, int count) => $"{device}/leds/{first}+{count}";
    public static bool TryRange(string key, string device, out int first, out int count)
    {
        first = count = 0; var prefix = device + "/leds/";
        if (!key.StartsWith(prefix, StringComparison.Ordinal) || key[prefix.Length..].Split('+') is not [var a, var b]) return false;
        return int.TryParse(a, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out first)
            && int.TryParse(b, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out count) && count > 0;
    }
    public static bool ValidTarget(string target, IReadOnlyList<RgbDeviceState> devices, IEnumerable<string> lamps) =>
        devices.Any(d => d.Id == target || d.Zones is not null && Enumerable.Range(0, d.Zones.Count).Any(z => ZoneTarget(d.Id, z) == target)
            || TryRange(target, d.Id, out var first, out var count) && first + count <= d.LedCount) || lamps.Contains(target);
}

/// <summary>Session-only state. No sensor acquisition or output I/O.</summary>
public sealed class SensorLighting
{
    public static IReadOnlyList<PaletteStop> DefaultPalette { get; } = [new(0, "#0080FF"), new(0.33, "#00D060"), new(0.67, "#FFB020"), new(1, "#FF2000")];
    private sealed class Memory(SensorBinding binding) { public SensorBinding Binding = binding; public double? Value; public double Time; public double? Min, Max; public bool High; public SensorBindingReading? Last; }
    private readonly Dictionary<string, Memory> memory = new();
    public SensorBindingReading Observe(string target, SensorBinding binding, double? raw, double time)
    {
        if (!memory.TryGetValue(target, out var m) || m.Binding.Source != binding.Source) memory[target] = m = new(binding);
        else if (m.Binding != binding)
        {
            if (m.Binding.AutoLearn != binding.AutoLearn) { m.Min = null; m.Max = null; }
            m.Binding = binding;
        }
        if (raw is not { } v || !double.IsFinite(v)) { m.Value = null; return m.Last = new(null, binding.Min, binding.Max, 0, false, $"{binding.Source} unavailable; using normal effect"); }
        if (binding.AutoLearn) { m.Min = Math.Min(m.Min ?? v, v); m.Max = Math.Max(m.Max ?? v, v); }
        double min = binding.AutoLearn ? m.Min!.Value : binding.Min, max = binding.AutoLearn ? m.Max!.Value : binding.Max;
        double dt = Math.Max(0, time - m.Time);
        m.Value = m.Value is { } previous && binding.Smoothing > 0 ? previous + (v - previous) * (1 - Math.Exp(-dt / binding.Smoothing)) : v;
        m.Time = time;
        double threshold = (binding.Min + binding.Max) / 2;
        if (m.High ? m.Value < threshold - binding.Hysteresis : m.Value >= threshold) m.High = !m.High;
        return m.Last = new(m.Value, min, max, max > min ? Math.Clamp((m.Value.Value - min) / (max - min), 0, 1) : 0.5,
            binding.Alert is { } a && v >= a.Threshold, null);
    }
    public IReadOnlyDictionary<string, SensorBindingReading> Readings(IReadOnlyDictionary<string, SensorBinding> bindings)
    {
        lock (memory) return memory.Where(p => bindings.TryGetValue(p.Key, out var binding) && binding == p.Value.Binding && p.Value.Last is not null)
            .ToDictionary(p => p.Key, p => p.Value.Last!);
    }
    public IReadOnlyList<RgbColor> Render(string target, SensorBinding binding, double? raw, double time, IReadOnlyList<RgbColor> fallback, double brightness = 1)
    {
        var sample = Observe(target, binding, raw, time);
        if (sample.Reason is not null) return fallback;
        var palette = binding.Palette ?? DefaultPalette;
        double n = sample.Normalized;
        var color = Palettes.Sample(palette, n);
        var result = new RgbColor[fallback.Count];
        for (int i = 0; i < result.Length; i++)
        {
            var c = binding.Style switch {
                SensorStyle.FillBar => i < Math.Ceiling(n * result.Length) ? (i == Math.Ceiling(n * result.Length) - 1 ? new RgbColor(255, 255, 255) : color) : RgbColor.Black,
                SensorStyle.BreathingSpeed => RgbColor.Lerp(RgbColor.Black, color, (1 - Math.Cos(time * Math.PI * (0.3 + n * 3))) / 2),
                SensorStyle.Brightness => RgbColor.Lerp(RgbColor.Black, Palettes.Sample(palette, 1), 0.05 + n * 0.95),
                SensorStyle.Threshold => Palettes.Sample(palette, memory[target].High ? 1 : 0),
                _ => color
            };
            if (sample.Alert) c = new((byte)Math.Round(255 * (0.3 + 0.7 * (1 - Math.Cos(time * Math.PI * 4)) / 2)), 0, 0);
            result[i] = RgbColor.Lerp(RgbColor.Black, c, brightness);
        }
        return result;
    }
    public static IReadOnlySet<int> AlertLeds(RgbDeviceState device, IReadOnlyDictionary<string, SensorBinding>? bindings, StateMessage state)
    {
        bool Active(string target) => bindings?.GetValueOrDefault(target) is { Alert: { } alert } b && SensorSources.Read(state, b.Source) >= alert.Threshold;
        var result = new HashSet<int>();
        for (int i = 0; i < device.LedCount; i++)
        {
            bool active = Active(device.Id);
            for (int z = 0; z < (device.Zones?.Count ?? 0); z++)
            {
                var zone = device.Zones![z]; var target = SensorSources.ZoneTarget(device.Id, z);
                if (i >= zone.FirstLed && i < zone.FirstLed + zone.LedCount && bindings?.ContainsKey(target) == true) active = Active(target);
            }
            // A run of LEDs (the pump apart from the radiator) is the narrowest part, so its own binding decides last.
            foreach (var key in bindings?.Keys ?? [])
                if (SensorSources.TryRange(key, device.Id, out var first, out var count) && i >= first && i < first + count) active = Active(key);
            if (active) result.Add(i);
        }
        return result;
    }
    public IReadOnlyList<RgbColor> Composite(RgbDeviceState device, IReadOnlyDictionary<string, SensorBinding>? bindings, StateMessage state, double time, IReadOnlyList<RgbColor> fallback, double brightness)
    {
        if (bindings is null) return fallback;
        lock (memory)
        {
            foreach (var key in memory.Keys.Where(k => !bindings.ContainsKey(k)).ToArray()) memory.Remove(key);
            var colors = fallback;
            if (bindings.TryGetValue(device.Id, out var binding)) colors = Render(device.Id, binding, SensorSources.Read(state, binding.Source), time, colors, brightness);
            var result = colors.ToArray();
            for (int z = 0; z < (device.Zones?.Count ?? 0); z++)
            {
                var target = SensorSources.ZoneTarget(device.Id, z); var zone = device.Zones![z];
                if (!bindings.TryGetValue(target, out binding)) continue;
                var normal = fallback.Skip(zone.FirstLed).Take(zone.LedCount).ToArray();
                var rendered = Render(target, binding, SensorSources.Read(state, binding.Source), time, normal, brightness);
                for (int i = 0; i < rendered.Count && zone.FirstLed + i < result.Length; i++) result[zone.FirstLed + i] = rendered[i];
            }
            foreach (var (key, range) in bindings)
            {
                if (!SensorSources.TryRange(key, device.Id, out var first, out var count) || first >= result.Length) continue;
                var rendered = Render(key, range, SensorSources.Read(state, range.Source), time, fallback.Skip(first).Take(count).ToArray(), brightness);
                for (int i = 0; i < rendered.Count && first + i < result.Length; i++) result[first + i] = rendered[i];
            }
            return result;
        }
    }
}
