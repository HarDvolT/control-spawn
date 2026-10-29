using Prism.Core;

namespace Prism.Hardware;

public sealed record FanSettings(FanCurve Curve, TemperatureSource Source = TemperatureSource.Cpu,
    double Minimum = 20, double Hysteresis = 2, double SpinUpPerSecond = 30, double SpinDownPerSecond = 10);

public sealed class FanCurveEngine
{
    private double? acceptedTemperature;
    private double? previous;
    public static FanCurve Preset(FanMode mode) => new(new[] { 20d, 30, 40, 50, 60, 67, 70, 100 }
        .Zip(mode switch {
            FanMode.Standard => new[] { 20d, 26, 34, 45, 60, 80, 100, 100 },
            FanMode.Silent => new[] { 20d, 20, 25, 35, 50, 70, 100, 100 },
            FanMode.Turbo => new[] { 35d, 45, 55, 65, 80, 95, 100, 100 },
            FanMode.Full => Enumerable.Repeat(100d, 8).ToArray(),
            _ => throw new ArgumentException("Custom mode requires an explicit curve.", nameof(mode))
        }, (t, p) => new FanPoint(t, p)));

    public static double Interpolate(FanCurve curve, double temperature)
    {
        if (!double.IsFinite(temperature)) return 100;
        var points = curve.Points;
        if (temperature <= points[0].Temperature) return points[0].Percent;
        for (int i = 1; i < points.Count; i++)
            if (temperature <= points[i].Temperature)
            {
                var a = points[i - 1]; var b = points[i];
                return a.Percent + (b.Percent - a.Percent) * (temperature - a.Temperature) / (b.Temperature - a.Temperature);
            }
        return points[^1].Percent;
    }

    public static double? SelectTemperature(HardwareSnapshot snapshot, TemperatureSource source) => source switch
    {
        TemperatureSource.Cpu => snapshot.Temperature(SensorCategory.Cpu),
        TemperatureSource.Gpu => snapshot.Temperature(SensorCategory.Gpu),
        TemperatureSource.Motherboard => snapshot.Temperature(SensorCategory.Motherboard),
        _ => new[] { snapshot.Temperature(SensorCategory.Cpu), snapshot.Temperature(SensorCategory.Gpu), snapshot.Temperature(SensorCategory.Motherboard) }.Max()
    };

    public double Evaluate(double temperature, FanSettings settings, TimeSpan elapsed)
    {
        Validate(settings);
        if (!double.IsFinite(temperature)) return ForceFull();
        if (acceptedTemperature is null || Math.Abs(temperature - acceptedTemperature.Value) >= settings.Hysteresis)
            acceptedTemperature = temperature;
        double target = Math.Clamp(Interpolate(settings.Curve, acceptedTemperature.Value), settings.Minimum, 100);
        double start = previous ?? 100; // Begin conservatively until a curve has settled.
        double step = (target > start ? settings.SpinUpPerSecond : settings.SpinDownPerSecond) * Math.Max(0, elapsed.TotalSeconds);
        previous = Math.Clamp(start + Math.Clamp(target - start, -step, step), settings.Minimum, 100);
        return previous.Value;
    }
    public double ForceFull() { previous = 100; acceptedTemperature = null; return 100; }
    private static void Validate(FanSettings s)
    {
        if (!double.IsFinite(s.Minimum) || s.Minimum < 0 || s.Minimum > 100 ||
            !double.IsFinite(s.Hysteresis) || s.Hysteresis < 0 ||
            !double.IsFinite(s.SpinUpPerSecond) || s.SpinUpPerSecond <= 0 ||
            !double.IsFinite(s.SpinDownPerSecond) || s.SpinDownPerSecond <= 0)
            throw new ArgumentOutOfRangeException(nameof(s));
    }
}
