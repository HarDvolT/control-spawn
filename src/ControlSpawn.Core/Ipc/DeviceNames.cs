namespace ControlSpawn.Core.Ipc;

public static class DeviceNames
{
    public static StateMessage Apply(StateMessage state, IReadOnlyList<DeviceState> registry, DiscoveryOptions options)
    {
        string Name(string id, string fallback) => registry.FirstOrDefault(d => d.Id == id)?.Name ?? fallback;
        var lamps = state.Lamps?.Select(l => l with { Name = Name(l.Id ?? "", l.Name ?? "Lamp") }).ToArray();
        return state with { Devices = registry, NewDevices = DeviceRegistry.NewDevices(registry), Discovery = options,
            Lighting = state.Lighting with { Devices = state.Lighting.Devices.Select(d => d with { Name = Name(d.Id, d.Name) }).ToArray() },
            Fans = state.Fans.Select(f => f with { Name = Name($"fan:{f.Header}", f.Name) }).ToArray(),
            Lamps = lamps, Lamp = lamps?.FirstOrDefault() ?? state.Lamp,
            Appliances = state.Appliances?.Select(a => a with { Name = Name(a.Id, a.Name) }).ToArray() };
    }
}
