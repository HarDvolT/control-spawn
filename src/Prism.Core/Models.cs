namespace Prism.Core;

public enum SensorCategory { Cpu, Gpu, Memory, Motherboard, Storage, Network, Other }
public sealed record SensorReading(string Id, string Name, SensorCategory Category, string Kind, string Unit, double? Value);
public sealed record HardwareSnapshot(DateTimeOffset Timestamp, IReadOnlyList<SensorReading> Sensors, bool Success = true, string? Error = null)
{
    public double? Temperature(SensorCategory category) => Sensors
        .Where(s => s.Category == category && s.Kind == "Temperature" && s.Value.HasValue && double.IsFinite(s.Value.Value))
        .Select(s => s.Value).DefaultIfEmpty(null).Max();
}
public enum FanMode { Silent, Standard, Turbo, Full, Custom }
public enum TemperatureSource { Cpu, Motherboard, Gpu, Maximum }
public sealed record FanGroup(int Header, string Name, double Rpm, bool CanControl, bool IsPump = false);
public readonly record struct FanPoint(double Temperature, double Percent);
public sealed class FanCurve
{
    public IReadOnlyList<FanPoint> Points { get; }
    public FanCurve(IEnumerable<FanPoint> points)
    {
        var p = points.ToArray();
        if (p.Length != 8 || p.Any(x => !double.IsFinite(x.Temperature) || !double.IsFinite(x.Percent)) ||
            p.Zip(p.Skip(1)).Any(x => x.First.Temperature >= x.Second.Temperature))
            throw new ArgumentException("Exactly eight finite points in increasing temperature order are required.", nameof(points));
        Points = Array.AsReadOnly(p);
    }
}
public readonly record struct RgbColor(byte R, byte G, byte B)
{
    public static RgbColor Black => new(0, 0, 0);
    public int Packed => (R << 16) | (G << 8) | B;
    public static RgbColor Lerp(RgbColor a, RgbColor b, double t)
    {
        t = double.IsFinite(t) ? Math.Clamp(t, 0, 1) : 0;
        return new((byte)Math.Round(a.R + (b.R - a.R) * t), (byte)Math.Round(a.G + (b.G - a.G) * t), (byte)Math.Round(a.B + (b.B - a.B) * t));
    }
}
public enum LightingEffect { Static, Breathing, Strobing, ColorCycle, RainbowWave, StarryNight, Smart, Music, Adaptive, Off }
public sealed record Scene(string Name, LightingEffect Effect, RgbColor Color, double Speed = 1, double Brightness = 1);
