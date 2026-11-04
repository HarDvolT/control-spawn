using System.Collections.Concurrent;
using System.Text.Json;
using Prism.Core;
using Prism.Core.Ipc;
namespace Prism.Service;

/// <summary>
/// What the Fans page shows beyond the curve: each fan's measured rpm per duty, whether a fan has stalled, and when the
/// overheat guard last stepped in. Measured maps live in their own file so they never ride along with settings edits.
/// </summary>
public sealed partial class ServiceRuntime
{
    // Duties tried while measuring, high to low so the fan ends near its quietest before it is handed back.
    private static readonly double[] MeasureSteps = [100, 80, 60, 50, 40, 30, 25, 20, 15];
    private static readonly TimeSpan MeasureSettle = TimeSpan.FromSeconds(5);
    private readonly ConcurrentDictionary<int, FanRpmMap> fanRpm = new();
    private readonly ConcurrentDictionary<int, string> measuring = new();
    private readonly StallWatch stalls = new();
    public const string PumpWarning = "The AIO pump has stopped spinning. Check the pump cable and power now: the CPU will overheat without it.";
    private DateTimeOffset? lastFailsafe; private string? lastFailsafeReason;
    private string FanRpmPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), Prism.Core.ProductNames.DataFolder, "fan-rpm.json");

    private void LoadFanRpm()
    {
        if (mock) return;
        try
        {
            if (File.Exists(FanRpmPath) && JsonSerializer.Deserialize<Dictionary<int, FanRpmMap>>(File.ReadAllText(FanRpmPath), PipeContract.Json) is { } saved)
                foreach (var (header, map) in saved) fanRpm[header] = map;
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException) { PrismLog.Failure("fans", e); }
    }

    /// <summary>Steps one fan through its duties (about 45 s), recording the rpm at each. Returns at once; progress is in the state.</summary>
    public void MeasureFan(int header)
    {
        if (Observe) throw new InvalidOperationException("Observe mode cannot drive fans.");
        if (header is < 1 or > 6 || !controls.Headers.Contains(header)) throw new ArgumentException("That header can't be measured.");
        if (!measuring.TryAdd(header, "starting")) throw new InvalidOperationException("That fan is already being measured.");
        _ = Task.Run(() => MeasureAsync(header, stop.Token));
    }
    private async Task MeasureAsync(int header, CancellationToken token)
    {
        var points = new List<FanRpmPoint>();
        try
        {
            foreach (var duty in MeasureSteps)
            {
                measuring[header] = $"{duty:0}%";
                fans.Hold(header, duty);
                await Task.Delay(MeasureSettle, token);
                points.Add(new(duty, mock ? Math.Round(duty * 16) : FanRpmOf(header) ?? 0));
            }
            // The lowest duty that still spins; a fan that never stopped has no floor to report.
            var spinning = points.Where(p => p.Rpm > 0).Select(p => p.Percent).DefaultIfEmpty(100).Min();
            var map = new FanRpmMap(points.OrderBy(p => p.Percent).ToArray(), points.Any(p => p.Rpm <= 0) ? spinning : null, DateTimeOffset.Now);
            fanRpm[header] = map;
            if (!mock)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(FanRpmPath)!);
                File.WriteAllText(FanRpmPath, JsonSerializer.Serialize(fanRpm.ToDictionary(p => p.Key, p => p.Value), PipeContract.Json));
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception e) { PrismLog.Failure("fans", e); }
        finally { fans.Hold(header, null); measuring.TryRemove(header, out _); }
    }
    private double? FanRpmOf(int header)
    {
        HardwareSnapshot current; lock (stateGate) current = snapshot;
        return current.Sensors.FirstOrDefault(s => s.Category == SensorCategory.Motherboard && s.Kind == "Fan" && s.Id.EndsWith($"/fan/{header - 1}"))?.Value;
    }

    /// <summary>Called once a second by the fan loop with fresh readings.</summary>
    private void WatchForStalls(HardwareSnapshot current)
    {
        lock (stateGate)
        {
            foreach (var header in stalls.Observe(current, controls.Headers, clock.Elapsed, measuring.ContainsKey,
                h => fanRpm.TryGetValue(h, out var map) && map.StopsBelow is { } stops ? stops : 25))
                PrismLog.Failure("fans", new InvalidOperationException(header == StallWatch.Pump ? "AIO pump reads below 500 rpm." : $"Fan header {header} reads 0 rpm while driven."));
            if (stalls.PumpStalled) warnings["pump"] = PumpWarning; else warnings.TryRemove("pump", out _);
        }
    }

    private void RecordFailsafe(bool active)
    {
        if (!active) return;
        lastFailsafe = DateTimeOffset.Now; lastFailsafeReason = safety.Reason;
    }

    private IReadOnlyList<FanState> WithInsight(IReadOnlyList<FanState> list)
    {
        int[] stuck; lock (stateGate) stuck = stalls.Stalled.ToArray();
        return list.Select(f =>
        {
            if (f.IsPump) return f with { Stalled = stuck.Contains(f.Header) };
            var (target, following) = fans.Status(f.Header);
            return f with { Target = f.Configuration is null ? null : target, Following = f.Configuration is null ? null : following,
                Measuring = measuring.GetValueOrDefault(f.Header), Stalled = stuck.Contains(f.Header) };
        }).ToArray();
    }
}
