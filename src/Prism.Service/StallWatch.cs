using Prism.Core;

namespace Prism.Service;

/// <summary>
/// The stall rules, fed once a second by the fan loop. A fan that has spun before but reads 0 rpm for 5 s while driven
/// above its measured floor is stalled. The AIO pump always runs at 100%, so it counts as stopped below
/// <see cref="PumpStoppedBelow"/> rpm (it idles near 2,800): a dead pump is the one failure the other fans can't cover.
/// </summary>
public sealed class StallWatch
{
    public const int Pump = 7;
    public const double PumpStoppedBelow = 500;
    public static readonly TimeSpan After = TimeSpan.FromSeconds(5);
    private readonly Dictionary<int, TimeSpan> stoppedSince = new();
    private readonly HashSet<int> seenSpinning = new(), stalled = new();
    public IReadOnlyCollection<int> Stalled => stalled;
    public bool PumpStalled => stalled.Contains(Pump);

    /// <returns>Headers that stalled on this call, for logging once.</returns>
    public IReadOnlyList<int> Observe(HardwareSnapshot current, IEnumerable<int> headers, TimeSpan now, Func<int, bool> measuring, Func<int, double> floor)
    {
        var started = new List<int>();
        // A failed read has no readings at all; the overheat guard covers it, and it says nothing about the fans.
        if (!current.Success) return started;
        foreach (int header in headers)
        {
            var rpm = Reading(current, "Fan", $"/fan/{header - 1}");
            bool pump = header == Pump;
            if (pump ? rpm >= PumpStoppedBelow : rpm > 0) { seenSpinning.Add(header); Clear(header); continue; }
            // Below its measured floor a fan is meant to stop, so that isn't a stall. A reading that vanished counts as stopped.
            var duty = Reading(current, "Control", $"/control/{header - 1}");
            bool driven = pump || duty is { } d && d >= floor(header);
            if (!seenSpinning.Contains(header) || measuring(header) || !driven) { Clear(header); continue; }
            if (!stoppedSince.TryGetValue(header, out var since)) stoppedSince[header] = since = now;
            if (now - since >= After && stalled.Add(header)) started.Add(header);
        }
        return started;
    }
    private void Clear(int header) { stoppedSince.Remove(header); stalled.Remove(header); }
    private static double? Reading(HardwareSnapshot snapshot, string kind, string suffix) =>
        snapshot.Sensors.FirstOrDefault(s => s.Category == SensorCategory.Motherboard && s.Kind == kind && s.Id.EndsWith(suffix, StringComparison.Ordinal))?.Value;
}
