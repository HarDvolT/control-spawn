using System.Security.Cryptography;
using System.Text.Json;
using Prism.Core;
using Prism.Core.Ipc;
using Prism.Lighting;

namespace Prism.Service;

public sealed partial class CommandProcessor
{
    private DeviceState RegistryDevice(JsonElement a) => Settings.Devices.FirstOrDefault(d => d.Id == Text(a, "id")) ?? throw new ArgumentException("Unknown device.");
    private async Task SaveAsync(ServiceSettings next)
    {
        if (next.Lamps is not null) next = next with { Lamp = next.Lamps.FirstOrDefault() ?? new() };
        next.Validate(); store.Save(next); Volatile.Write(ref settings, next); await runtime.ApplyAsync(next, CancellationToken.None);
    }
    private async Task ObserveDevicesAsync(IReadOnlyList<DeviceState> observations, IReadOnlyList<string> completed)
    {
        await gate.WaitAsync();
        try { await SaveAsync(MergeObservations(Settings, observations, completed)); }
        finally { gate.Release(); }
    }
    private static ServiceSettings MergeObservations(ServiceSettings settings, IReadOnlyList<DeviceState> observations, IReadOnlyList<string> completed)
    {
        var now = DateTimeOffset.UtcNow;
        // Authenticated settings and LAN-control lamps are already provisioned.
        var enriched = observations.Select(d => settings.ConfiguredLamps.FirstOrDefault(l => l.Id == d.Id) is { } lamp
            ? d with { Configured = true, Name = d.Model is null ? settings.Devices.FirstOrDefault(old => old.Id == d.Id)?.DetectedName ?? lamp.Name : d.Name,
                Status = d.Model is null ? DeviceStatus.Ready : d.Status } : d).ToArray();
        var registry = DeviceRegistry.Observe(settings.Devices, enriched, now, completed);
        var synced = new Dictionary<string, bool>(settings.SyncedDevices);
        var headers = new Dictionary<int, FanConfiguration>(settings.HeaderFans);
        var lamps = settings.ConfiguredLamps.ToList();
        foreach (var detected in registry.Where(d => d.Kind == DeviceKind.Lamp && d.Configured && d.DeviceId is not null))
            if (lamps.All(l => l.DeviceId != detected.DeviceId))
            {
                lamps.RemoveAll(l => l.DeviceId is null && l.Host is null);
                lamps.Add(new(Id: detected.Id, DeviceId: detected.DeviceId, Name: detected.Name, LastHost: detected.Ip));
            }
        // A model learned from miIO.info can disqualify a record provisioned through the old lamp-only command.
        lamps.RemoveAll(l => registry.Any(d => d.Id == l.Id && d.Kind is not DeviceKind.Lamp));
        foreach (var added in registry.Where(d => settings.Devices.All(old => old.Id != d.Id)))
        {
            if (added.Kind == DeviceKind.RgbController) synced.TryAdd(added.Id, settings.Discovery.AutoJoinNewRgb);
            if (added.Kind == DeviceKind.FanHeader && int.TryParse(added.Id.AsSpan(4), out var header) && header is not (2 or 6 or 7)) headers.TryAdd(header, new());
        }
        return settings with { Devices = registry, SyncedDevices = synced, HeaderFans = headers, Lamps = lamps, Lamp = lamps.FirstOrDefault() ?? new() };
    }
    private ServiceSettings Import(JsonElement args)
    {
        var records = TokenExtractorParser.Parse(Text(args, "text"));
        var selected = args.GetProperty("selected").EnumerateArray().Select(ParseDeviceId).ToHashSet();
        if (selected.Any(id => records.All(r => r.DeviceId != id))) throw new ArgumentException("Invalid selection.");
        var devices = Settings.Devices.ToList(); var lamps = Settings.ConfiguredLamps.Where(l => l.DeviceId is not null || l.Host is not null).ToList();
        var chosen = records.Where(r => selected.Contains(r.DeviceId)).ToArray();
        foreach (var record in chosen)
        {
            var old = devices.FirstOrDefault(d => d.DeviceId == record.DeviceId);
            bool isLamp = record.Model.StartsWith("yeelink.light.", StringComparison.Ordinal);
            bool appliance = record.Model.Contains(".fryer.", StringComparison.Ordinal);
            string name = record.Name.Trim(); if (name.Length == 0) name = record.Model.Length > 0 ? record.Model : $"Mi Home {record.DeviceId}";
            name = name[..Math.Min(40, name.Length)];
            var device = old ?? new DeviceState($"lamp:{record.DeviceId}", DeviceKind.NetworkDevice, name, "miio", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, DeviceStatus.NeedsToken, [], DeviceId: record.DeviceId);
            if (!string.IsNullOrEmpty(record.Ip)) ServiceSettings.ValidateHost(record.Ip);
            device = device with { Model = record.Model, DetectedName = name, Name = device.UserName ?? name,
                Ip = old?.Ip ?? (string.IsNullOrEmpty(record.Ip) ? null : record.Ip), Kind = isLamp ? DeviceKind.Lamp : appliance ? DeviceKind.Appliance : DeviceKind.NetworkDevice,
                Status = isLamp || MiotSpec.Load(record.Model) is not null ? DeviceStatus.Ready : DeviceStatus.Unsupported, Configured = true };
            devices.RemoveAll(d => d.Id == device.Id); devices.Add(device);
            if (isLamp)
            {
                int i = lamps.FindIndex(l => l.DeviceId == record.DeviceId);
                var config = (i >= 0 ? lamps[i] : new LampConfiguration()) with { Id = device.Id, DeviceId = record.DeviceId, LastHost = device.Ip, Name = device.Name };
                if (i < 0) lamps.Add(config); else lamps[i] = config;
            }
        }
        var next = Settings with { Devices = devices, Lamps = lamps, Lamp = lamps.FirstOrDefault() ?? new() };
        next.Validate(); // Validate the complete import before saving any secret.
        foreach (var record in chosen)
        {
            var secret = Convert.FromHexString(record.Token);
            try { saveDeviceToken(record.DeviceId, secret); }
            finally { CryptographicOperations.ZeroMemory(secret); }
            runtime.LampCredentialsChanged(devices.First(d => d.DeviceId == record.DeviceId).Id);
        }
        return next;
    }
}
