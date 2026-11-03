using System.Collections.Concurrent;
using Prism.Core.Ipc;
using Prism.Lighting;

namespace Prism.Service;

/// <summary>
/// Where each LED sits only changes with the case map or the device list, yet it was worked out for every device on every
/// frame (60 a second). The lighting and lamp loops both read this, so it is safe across threads; an entry is reused
/// only while the map, the device and the device list are the very same objects, so any edit recomputes it.
/// </summary>
public sealed class PositionCache
{
    private sealed record Entry(LightingMap Map, RgbDeviceState Device, IReadOnlyList<RgbDeviceState> All, MapPoint[] Positions);
    private readonly ConcurrentDictionary<string, Entry> entries = new();
    private int computed;
    public int Computed => Volatile.Read(ref computed);
    public MapPoint[] Get(LightingMap map, RgbDeviceState device, IReadOnlyList<RgbDeviceState> all)
    {
        if (entries.TryGetValue(device.Id, out var e) && ReferenceEquals(e.Map, map) && ReferenceEquals(e.Device, device) && ReferenceEquals(e.All, all))
            return e.Positions;
        Interlocked.Increment(ref computed);
        var positions = CaseMap.Positions(device, CaseMap.Placement(map, device, all), map);
        entries[device.Id] = new(map, device, all, positions);
        return positions;
    }
}
