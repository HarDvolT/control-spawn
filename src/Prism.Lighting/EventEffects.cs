using Prism.Core;
using Prism.Core.Ipc;

namespace Prism.Lighting;

public sealed class EventEffects
{
    private readonly object gate = new();
    private readonly Dictionary<LightingEventKind, double> started = new();
    public void Trigger(LightingEventKind kind, double time) { lock (gate) started[kind] = time; }
    public RgbColor[] Composite(IReadOnlyList<RgbColor> source, IReadOnlyList<MapPoint> positions, double time, EventEffectOptions options, IReadOnlySet<int>? sensorAlerts = null)
    {
        KeyValuePair<LightingEventKind, double>? active;
        lock (gate)
        {
            foreach (var key in started.Where(e => time - e.Value >= 3).Select(e => e.Key).ToArray()) started.Remove(key);
            active = started.Where(e => options.Enabled(e.Key) && time >= e.Value)
                .OrderByDescending(e => Priority(e.Key)).ThenByDescending(e => e.Value).Cast<KeyValuePair<LightingEventKind, double>?>().FirstOrDefault();
        }
        if (active is not { } selected) return source.ToArray();
        double age = time - selected.Value, fade = Math.Clamp((3 - age) / 0.8, 0, 1);
        var result = source.ToArray();
        for (int i = 0; i < result.Length; i++)
        {
            if (selected.Key != LightingEventKind.FanFailsafe && sensorAlerts?.Contains(i) == true) continue;
            double radius = Math.Sqrt(Math.Pow(positions[i].X - 0.5, 2) + Math.Pow(positions[i].Y - 0.5, 2));
            double amount = selected.Key switch {
                LightingEventKind.FanFailsafe => (int)(age * 10) % 2 == 0 ? 1 : 0.1,
                LightingEventKind.SceneChange => Math.Exp(-Math.Pow((positions[i].Y - age / 2) * 8, 2)),
                _ => Math.Exp(-Math.Pow((radius - age * 0.4) * 10, 2)) };
            var color = selected.Key switch { LightingEventKind.FanFailsafe or LightingEventKind.PoorSignal => new RgbColor(255, 20, 10),
                LightingEventKind.PingSpike => new(255, 160, 10), _ => new(255, 255, 255) };
            result[i] = RgbColor.Lerp(result[i], color, amount * fade);
        }
        return result;
    }
    private static int Priority(LightingEventKind kind) => kind switch {
        LightingEventKind.FanFailsafe => 100, LightingEventKind.PoorSignal => 60, LightingEventKind.PingSpike => 50,
        LightingEventKind.Notification => 30, _ => 20 };
}

public interface IAudioLevels { IReadOnlyList<double> Bands { get; } double Beat { get; } }
public interface IScreenZones { RgbColor Left { get; } RgbColor Centre { get; } RgbColor Right { get; } }

/// <summary>Elapsed-time smoothing keeps load bars moving at frame rate between sensor polls.</summary>
public sealed class ReactiveSmoother
{
    private readonly object gate = new();
    private ReactiveFrame current = new();
    private double previous;
    public ReactiveFrame Sample(ReactiveFrame target, double time)
    {
        lock (gate)
        {
            double elapsed = Math.Max(0, time - previous); previous = time;
            double blend = 1 - Math.Exp(-elapsed / 0.3);
            double Smooth(double a, double b) => a + ((double.IsFinite(b) ? b : 0) - a) * blend;
            current = target with { CpuLoad = Smooth(current.CpuLoad, target.CpuLoad), GpuLoad = Smooth(current.GpuLoad, target.GpuLoad), RamUse = Smooth(current.RamUse, target.RamUse) };
            return current;
        }
    }
}
