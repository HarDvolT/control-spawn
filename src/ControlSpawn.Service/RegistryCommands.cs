using System.Security.Cryptography;
using System.Text.Json;
using ControlSpawn.Core;
using ControlSpawn.Core.Ipc;
using ControlSpawn.Lighting;

namespace ControlSpawn.Service;

/// <summary>A token that can't be used, with a reason safe to show: it never contains the token.</summary>
public sealed class TokenProblem(string message) : Exception(message);

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
    /// <summary>
    /// One pasted token for one device. It is tried on the device before it is saved, so a wrong token or IP says so
    /// instead of saving and then never connecting; the model the device reports decides lamp or appliance.
    /// </summary>
    private async Task<ServiceSettings> AddTokenAsync(JsonElement args)
    {
        var text = new string(Text(args, "token").Where(c => !char.IsWhiteSpace(c) && c is not '-' and not ':').ToArray());
        if (text.Length != 32 || !text.All(Uri.IsHexDigit)) throw new TokenProblem($"A token is 32 characters of 0-9 and a-f. This one has {text.Length}.");
        var device = args.TryGetProperty("id", out var id) && id.GetString() is { Length: > 0 } key ? Settings.Devices.FirstOrDefault(d => d.Id == key) ?? throw new TokenProblem("That device is no longer listed. Scan again.") : null;
        var ip = args.TryGetProperty("ip", out var given) && given.GetString() is { Length: > 0 } typed ? typed.Trim() : device?.Ip;
        if (ip is null) throw new TokenProblem("Control Spawn doesn't know this device's IP address yet. Enter it from your router or the Mi Home app.");
        try { ServiceSettings.ValidateHost(ip); } catch (ArgumentException) { throw new TokenProblem($"“{ip}” isn't an IP address."); }
        var secret = Convert.FromHexString(text);
        try
        {
            var (deviceId, model) = await runtime.IdentifyMiioAsync(ip, secret, CancellationToken.None);
            if (device?.DeviceId is { } expected && expected != deviceId) throw new TokenProblem($"A different device answers at {ip} now. Scan again.");
            string name = device?.UserName ?? (device?.Name is { } found && !found.StartsWith("Mi Home ", StringComparison.Ordinal) ? found : MiHomeSupport.Describe(model));
            var record = new ExtractedToken { DeviceId = deviceId, Ip = ip, Model = model, Name = name, Token = text };
            return Import([record], [deviceId]);
        }
        finally { CryptographicOperations.ZeroMemory(secret); }
    }
    private ServiceSettings Import(JsonElement args) =>
        Import(TokenExtractorParser.Parse(Text(args, "text")), args.GetProperty("selected").EnumerateArray().Select(ParseDeviceId).ToHashSet());
    private ServiceSettings Import(IReadOnlyList<ExtractedToken> records, HashSet<uint> selected)
    {
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
                Status = isLamp || MiotSpec.Load(record.Model) is not null ? DeviceStatus.Ready : DeviceStatus.Unsupported, Configured = true, Dismissed = false };
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
