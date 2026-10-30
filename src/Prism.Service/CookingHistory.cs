using Prism.Core.Ipc;

namespace Prism.Service;

/// <summary>Sessions actually observed by this service; never reconstructs cooks before observation.</summary>
public sealed class CookingHistory
{
    private readonly Dictionary<string, (DateTimeOffset Since, double? Temperature, double? Minutes)> active = new();
    private readonly Dictionary<string, List<CookRecord>> recent = new();
    public IReadOnlyList<CookRecord> Observe(ApplianceState state, DateTimeOffset now)
    {
        if (!recent.TryGetValue(state.Id, out var records)) recent[state.Id] = records = new();
        if (state.Connected)
        {
            if (state.Status is "cooking" or "preheating" or "paused")
                active.TryAdd(state.Id, (now, state.TargetTemperature, state.TargetMinutes));
            else if (active.Remove(state.Id, out var session))
            {
                records.Insert(0, new(session.Since, now, session.Temperature, session.Minutes,
                    state.RemainingMinutes == 0 || state.Status is "finished" or "completed" ? "Finished" : "Ended"));
                if (records.Count > 20) records.RemoveAt(records.Count - 1);
            }
        }
        return records.ToArray();
    }
}
