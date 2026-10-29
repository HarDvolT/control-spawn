using System.Diagnostics;
using Prism.Core.Ipc;

namespace Prism.Service;

/// <summary>Samples CPU time deltas, normalized to the whole machine. The first poll has no rate.</summary>
internal sealed class ProcessTelemetry
{
    private Dictionary<int, (long Started, double Cpu)> previous = new();
    private long previousTime;
    public IReadOnlyList<ProcessLoad>? Sample()
    {
        long now = Stopwatch.GetTimestamp();
        var current = new Dictionary<int, (long Started, double Cpu)>();
        var loads = new List<ProcessLoad>();
        double elapsed = previousTime == 0 ? 0 : (now - previousTime) / (double)Stopwatch.Frequency;
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
                    current[id] = (started, cpu);
                    if (elapsed > 0 && previous.TryGetValue(id, out var last) && last.Started == started && cpu >= last.Cpu)
                        loads.Add(new(id, process.ProcessName, Math.Clamp((cpu - last.Cpu) / elapsed / Environment.ProcessorCount * 100, 0, 100)));
                }
                catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException or NotSupportedException)
                { /* Protected processes and processes exiting mid-poll cannot be measured. */ }
            }
        }
        previous = current; previousTime = now;
        return elapsed <= 0 ? null : loads.OrderByDescending(p => p.CpuPercent).Take(4).ToArray();
    }
}
