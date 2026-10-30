using System.Diagnostics;
using Prism.Core.Ipc;

namespace Prism.Service;

/// <summary>Samples CPU time deltas, normalized to the whole machine. The first poll has no rate.</summary>
internal sealed class ProcessTelemetry
{
    private readonly ProcessCpuRates rates = new();
    public IReadOnlyList<ProcessLoad>? Sample()
    {
        long now = Stopwatch.GetTimestamp();
        var current = new List<ProcessObservation>();
        foreach (var process in Process.GetProcesses())
        {
            using (process)
            {
                try
                {
                    int id = process.Id;
                    if (id == 0) continue; // Windows idle time is not process activity.
                    long started = process.StartTime.ToUniversalTime().Ticks;
                    double cpu = process.TotalProcessorTime.TotalSeconds;
                    current.Add(new(id, started, process.ProcessName, cpu));
                }
                catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException or NotSupportedException)
                { /* Protected processes and processes exiting mid-poll cannot be measured. */ }
            }
        }
        return rates.Sample(current, now / (double)Stopwatch.Frequency, Environment.ProcessorCount);
    }
}

public sealed record ProcessObservation(int Id, long Started, string Name, double CpuSeconds);

/// <summary>CPU deltas normalized to whole-machine capacity, with PID-reuse and counter-reset protection.</summary>
public sealed class ProcessCpuRates
{
    private Dictionary<int, ProcessObservation> previous = new();
    private double? previousTime;
    public IReadOnlyList<ProcessLoad>? Sample(IEnumerable<ProcessObservation> observations, double seconds, int processors)
    {
        if (!double.IsFinite(seconds) || processors <= 0) throw new ArgumentOutOfRangeException(nameof(seconds));
        var current = observations.Where(p => p.Id > 0 && double.IsFinite(p.CpuSeconds) && p.CpuSeconds >= 0).ToDictionary(p => p.Id);
        var elapsed = seconds - previousTime;
        var loads = new List<ProcessLoad>();
        if (elapsed is > 0)
            foreach (var p in current.Values)
                if (previous.TryGetValue(p.Id, out var last) && last.Started == p.Started && p.CpuSeconds >= last.CpuSeconds)
                    loads.Add(new(p.Id, p.Name, Math.Clamp((p.CpuSeconds - last.CpuSeconds) / elapsed.Value / processors * 100, 0, 100)));
        previous = current; previousTime = seconds;
        return elapsed is > 0 ? loads.OrderByDescending(p => p.CpuPercent).ThenBy(p => p.Id).Take(4).ToArray() : null;
    }
}
