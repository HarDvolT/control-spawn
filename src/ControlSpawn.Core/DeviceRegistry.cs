using ControlSpawn.Core.Ipc;

namespace ControlSpawn.Core;

/// <summary>Pure registry reducer. Only identity/MAC can merge records; IP is just an endpoint cache.</summary>
public static class DeviceRegistry
{
    public static string? NormalizeMac(string? mac)
    {
        var value = new string((mac ?? "").Where(Uri.IsHexDigit).ToArray()).ToUpperInvariant();
        return value.Length == 12 && value != "000000000000" ? value : null;
    }
    public static IReadOnlyList<DeviceState> Observe(IReadOnlyList<DeviceState> known, IEnumerable<DeviceState> observations,
        DateTimeOffset now, IEnumerable<string>? completedSources = null)
    {
        var list = known.ToList();
        foreach (var observation in observations)
        {
            var incoming = observation with { Mac = NormalizeMac(observation.Mac) };
            // Enriching a previously anonymous network entry may connect two existing records.
            var matches = list.Where(d => d.Id == incoming.Id || incoming.DeviceId is not null && d.DeviceId == incoming.DeviceId || incoming.Mac is not null && NormalizeMac(d.Mac) == incoming.Mac).ToArray();
            if (matches.Length > 1)
            {
                var primary = matches.FirstOrDefault(d => d.DeviceId is not null) ?? matches[0];
                var named = matches.FirstOrDefault(d => d.UserName is not null);
                primary = primary with { FirstSeen = matches.Min(d => d.FirstSeen), UserName = named?.UserName ?? primary.UserName,
                    Name = named?.Name ?? primary.Name, Dismissed = matches.Any(d => d.Dismissed), Configured = matches.Any(d => d.Configured),
                    Capabilities = matches.SelectMany(d => d.Capabilities).Distinct().ToArray(),
                    Sources = matches.SelectMany(d => d.Sources ?? [d.Source]).Distinct().ToArray() };
                list.RemoveAll(d => matches.Any(m => m.Id == d.Id)); list.Add(primary);
            }
            int i = list.FindIndex(d => d.Id == incoming.Id ||
                incoming.DeviceId is not null && d.DeviceId == incoming.DeviceId ||
                incoming.Mac is not null && NormalizeMac(d.Mac) == incoming.Mac);
            if (i < 0) list.Add(incoming with { FirstSeen = now, LastSeen = now, DetectedName = incoming.DetectedName ?? incoming.Name, OnlineStatus = incoming.Status });
            else
            {
                var old = list[i];
                bool richer = (incoming.Source != "router" || old.Source == "router") && !(old.Model is not null && incoming.Model is null);
                list[i] = old with {
                    Kind = richer ? incoming.Kind : old.Kind, Source = richer ? incoming.Source : old.Source,
                    DetectedName = richer ? incoming.DetectedName ?? incoming.Name : old.DetectedName,
                    Name = old.UserName ?? (richer ? incoming.DetectedName ?? incoming.Name : old.DetectedName ?? old.Name),
                    LastSeen = now, Status = richer ? incoming.Status : old.Status == DeviceStatus.Offline ? old.OnlineStatus ?? incoming.Status : old.Status,
                    OnlineStatus = richer ? incoming.Status : old.OnlineStatus ?? old.Status,
                    Capabilities = old.Capabilities.Union(incoming.Capabilities).ToArray(),
                    Model = old.Model?.Contains('.', StringComparison.Ordinal) == true && incoming.Model?.Contains('.', StringComparison.Ordinal) == false ? old.Model : incoming.Model ?? old.Model,
                    Firmware = incoming.Firmware ?? old.Firmware,
                    DeviceId = incoming.DeviceId ?? old.DeviceId, Ip = incoming.Ip ?? old.Ip,
                    Mac = incoming.Mac ?? old.Mac, Vendor = incoming.Vendor ?? old.Vendor,
                    Configured = old.Configured || incoming.Configured,
                    Sources = (old.Sources ?? [old.Source]).Union(incoming.Sources ?? [incoming.Source]).ToArray() };
            }
        }
        var completed = (completedSources ?? []).ToHashSet();
        return list.Select(d => completed.Contains(d.Source) && now - d.LastSeen >= TimeSpan.FromSeconds(150)
            ? d with { Status = DeviceStatus.Offline } : d).ToArray();
    }
    public static DeviceState Rename(DeviceState device, string? name, bool reset = false)
    {
        var trimmed = name?.Trim();
        if (!reset && (string.IsNullOrEmpty(trimmed) || trimmed.Length > 40 || trimmed.Any(char.IsControl)))
            throw new ArgumentException("Name must contain 1–40 characters.");
        return device with { UserName = reset ? null : trimmed, Name = reset ? device.DetectedName ?? device.Name : trimmed! };
    }
    public static IReadOnlyList<DeviceState> NewDevices(IEnumerable<DeviceState> devices) =>
        devices.Where(d => !d.Dismissed && !d.Configured).ToArray();
}
