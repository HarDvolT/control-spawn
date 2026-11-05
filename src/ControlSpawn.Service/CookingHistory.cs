using System.Text.Json;
using ControlSpawn.Core;
using ControlSpawn.Core.Ipc;

namespace ControlSpawn.Service;

/// <summary>
/// Sessions actually observed by this service; never reconstructs cooks before observation.
/// Kept on disk so Recent cooks survives a restart, and written only when a cook ends.
/// </summary>
public sealed class CookingHistory(string? path = null)
{
    public static string DefaultPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), ControlSpawn.Core.ProductNames.DataFolder, "cooks.json");
    private const int Kept = 20;
    private readonly Dictionary<string, (DateTimeOffset Since, double? Temperature, double? Minutes, double? Left)> active = new();
    private Dictionary<string, List<CookRecord>>? recent;
    public IReadOnlyList<CookRecord> Observe(ApplianceState state, DateTimeOffset now)
    {
        var all = Loaded();
        if (!all.TryGetValue(state.Id, out var records)) all[state.Id] = records = new();
        if (state.Connected)
        {
            if (state.Status is "cooking" or "preheating" or "paused")
            {
                // Follow the targets through the cook: a time added halfway is the time that was cooked.
                var since = active.TryGetValue(state.Id, out var session) ? session.Since : now;
                active[state.Id] = (since, state.TargetTemperature ?? session.Temperature, state.TargetMinutes ?? session.Minutes, state.RemainingMinutes ?? session.Left);
            }
            else if (active.Remove(state.Id, out var session))
            {
                // A cancel can zero the timer too, so "finished" means the fryer said done or the last minute was seen.
                bool finished = state.Status is "done" or "finished" or "completed" || session.Left is <= 1 || session.Left is null && state.RemainingMinutes == 0;
                records.Insert(0, new(session.Since, now, session.Temperature, session.Minutes, finished ? "Finished" : "Ended"));
                if (records.Count > Kept) records.RemoveAt(records.Count - 1);
                Save(all);
            }
        }
        return records.ToArray();
    }

    private Dictionary<string, List<CookRecord>> Loaded()
    {
        if (recent is not null) return recent;
        try { recent = path is not null && File.Exists(path) ? JsonSerializer.Deserialize<Dictionary<string, List<CookRecord>>>(File.ReadAllText(path), PipeContract.Json) : null; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException) { ControlSpawnLog.Failure("appliances", e); }
        return recent ??= new();
    }
    private void Save(Dictionary<string, List<CookRecord>> all)
    {
        if (path is null) return;
        try { Directory.CreateDirectory(Path.GetDirectoryName(path)!); AtomicFile.WriteAllText(path, JsonSerializer.Serialize(all, PipeContract.Json)); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { ControlSpawnLog.Failure("appliances", e); }
    }
}
