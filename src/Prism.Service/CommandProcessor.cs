using System.Text.Json;
using Prism.Core;
using Prism.Core.Ipc;
using Prism.Router;
using Prism.Lighting;

namespace Prism.Service;

public interface IServiceRuntime
{
    void SetDeviceObservationHandler(Func<IReadOnlyList<DeviceState>, IReadOnlyList<string>, Task> handler) { }
    Task<IReadOnlyList<DeviceState>> FindDevicesAsync(CancellationToken token) => Task.FromResult<IReadOnlyList<DeviceState>>([]);
    Task<string> PrepareApplianceAsync(DeviceState device, int siid, int aiid, CancellationToken token) => Task.FromException<string>(new InvalidOperationException("Appliance unavailable."));
    Task SetApplianceAsync(DeviceState device, JsonElement values, CancellationToken token) => Task.FromException(new InvalidOperationException("Appliance unavailable."));
    Task ActApplianceAsync(DeviceState device, int siid, int aiid, string? nonce, CancellationToken token) => Task.FromException(new InvalidOperationException("Appliance unavailable."));
    StateMessage GetState(ServiceSettings settings);
    Task ApplyAsync(ServiceSettings settings, CancellationToken cancellationToken);
    Task<RgbDeviceState> ResizeZoneAsync(string device, int zone, int size, CancellationToken cancellationToken) =>
        Task.FromException<RgbDeviceState>(new InvalidOperationException("Zone resizing is unavailable."));
    void IdentifyZone(string device, int zone, int size) { }
    void EndIdentification(string device) { }
    Task<IReadOnlyList<MiioDevice>> DiscoverLampsAsync(CancellationToken token) => Task.FromResult<IReadOnlyList<MiioDevice>>([]);
    void SetLampResolutionHandler(Func<string, MiioDevice, Task> handler) { }
    void CredentialsChanged(bool lamp) { }
    void LampCredentialsChanged(string id) => CredentialsChanged(true);
    void TriggerLightingEvent(LightingEventKind kind) { }
    Task<SpeedTestResult?> StartSpeedTestAsync(CancellationToken cancellationToken);
}

/// <summary>Serialized settings commands; connection-local hello/subscribe live in PipeSession.</summary>
public sealed partial class CommandProcessor
{
    private readonly ISettingsStore store;
    private readonly IServiceRuntime runtime;
    private readonly Action<byte[]> saveLampToken;
    private readonly Action<string> saveRouterPassword;
    private readonly Action<uint, byte[]> saveDeviceToken;
    private readonly Action<uint> removeDeviceToken;
    private readonly LampTokenStore lampTokens;
    private readonly SemaphoreSlim gate = new(1, 1);
    private ServiceSettings settings;
    public ServiceSettings Settings => Volatile.Read(ref settings);
    public CommandProcessor(ISettingsStore store, IServiceRuntime runtime, Action<byte[]>? saveLampToken = null, Action<string>? saveRouterPassword = null, Action<uint, byte[]>? saveDeviceToken = null, Action<uint>? removeDeviceToken = null, LampTokenStore? lampTokens = null)
    {
        this.lampTokens = lampTokens ?? new LampTokenStore();
        this.store = store; this.runtime = runtime;
        this.saveDeviceToken = saveDeviceToken ?? ((id, token) => this.lampTokens.Save(id, token));
        this.removeDeviceToken = removeDeviceToken ?? (id => this.lampTokens.Remove(id));
        this.saveLampToken = saveLampToken ?? (token => this.lampTokens.Save(token));
        this.saveRouterPassword = saveRouterPassword ?? (password => { var credentials = new CredentialStore(); credentials.SavePassword(password); credentials.ForgetSession(); });
        settings = store.Load(); Settings.Validate();
        runtime.SetLampResolutionHandler(CacheLampAsync);
        runtime.SetDeviceObservationHandler(ObserveDevicesAsync);
    }
    private async Task CacheLampAsync(string id, MiioDevice found)
    {
        await gate.WaitAsync();
        try
        {
            var lamps = Settings.ConfiguredLamps.ToList(); var index = lamps.FindIndex(l => l.Id == id);
            if (index < 0) return;
            var current = lamps[index];
            if (current.DeviceId is { } expected && expected != found.DeviceId) return;
            if (current.DeviceId is null)
            {
                if (lamps.Any(l => l.DeviceId == found.DeviceId))
                {
                    // A newly provisioned identity takes precedence over its redundant legacy record.
                    lamps.RemoveAt(index);
                    var merged = Settings with { Lamps = lamps, Lamp = lamps[0] };
                    merged.Validate(); store.Save(merged); Volatile.Write(ref settings, merged);
                    await runtime.ApplyAsync(merged, CancellationToken.None); lampTokens.DeleteLegacy(); return;
                }
                var legacy = lampTokens.Load();
                if (legacy is not null)
                {
                    try { saveDeviceToken(found.DeviceId, legacy); }
                    finally { System.Security.Cryptography.CryptographicOperations.ZeroMemory(legacy); }
                }
            }
            lamps[index] = current with { Id = $"lamp:{found.DeviceId}", DeviceId = found.DeviceId, LastHost = found.Ip };
            var next = Settings with { Lamps = lamps, Lamp = lamps[0] };
            next.Validate(); store.Save(next); Volatile.Write(ref settings, next); await runtime.ApplyAsync(next, CancellationToken.None);
            if (current.DeviceId is null) lampTokens.DeleteLegacy();
        }
        finally { gate.Release(); }
    }
    public StateMessage GetState() => DeviceNames.Apply(runtime.GetState(Settings), Settings.Devices, Settings.Discovery) with { Automations = Automations };
    public event Action? StateChanged;
    public async Task<Response> HandleAsync(Request request, CancellationToken cancellationToken = default)
    {
        var response = await HandleCoreAsync(request, cancellationToken);
        if (response.Ok && request.Command != Commands.GetState) StateChanged?.Invoke();
        return response;
    }
    private async Task<Response> HandleCoreAsync(Request request, CancellationToken cancellationToken)
    {
        // A speed test must not hold the settings gate while fans/scenes need to change.
        if (request.Command == Commands.StartSpeedTest) return await SpeedResponseAsync(request.Id, cancellationToken);
        // Lock reports only wake the automation check; they never change saved settings.
        if (request.Command == Commands.SetSessionLocked)
        {
            try { SetSessionLocked(request.Args ?? default); return new(request.Id, true); }
            catch (Exception e) when (e is InvalidOperationException or KeyNotFoundException) { return new(request.Id, false, "Expected { locked: true|false }."); }
        }
        await gate.WaitAsync(cancellationToken);
        try
        {
            if (request.Command == Commands.GetState) return new(request.Id, true, Data: JsonSerializer.SerializeToElement(GetState(), PipeContract.Json));
            if (request.Command == Commands.FindDevices)
            {
                var observations = await runtime.FindDevicesAsync(cancellationToken);
                await SaveAsync(MergeObservations(Settings, observations, ["miio", "yeelight"]));
                return new(request.Id, true);
            }
            if (request.Command == Commands.DiscoverLamps)
            {
                var found = await runtime.DiscoverLampsAsync(cancellationToken);
                return new(request.Id, true, Data: JsonSerializer.SerializeToElement(found.Select(d => new { ip = d.Ip, deviceId = d.DeviceId, alreadyConfigured = Settings.ConfiguredLamps.Any(l => l.DeviceId == d.DeviceId) }), PipeContract.Json));
            }
            var args = request.Args;
            if (args is null || args.Value.ValueKind != JsonValueKind.Object) throw new ArgumentException("Command args must be an object.");
            var a = args.Value;
            ServiceSettings next;
            uint? removedToken = null;
            bool removedLegacyToken = false;
            switch (request.Command)
            {
                case Commands.RenameDevice:
                {
                    string id = Text(a, "id"); var device = Settings.Devices.FirstOrDefault(d => d.Id == id) ?? throw new ArgumentException("Unknown device.");
                    bool reset = a.TryGetProperty("reset", out var r) && r.GetBoolean();
                    var renamed = DeviceRegistry.Rename(device, reset ? null : Text(a, "name"), reset);
                    next = Settings with { Devices = Settings.Devices.Select(d => d.Id == id ? renamed : d).ToArray(),
                        Lamps = Settings.ConfiguredLamps.Select(l => l.Id == id ? l with { Name = renamed.Name } : l).ToArray() }; break;
                }
                case Commands.DismissDevice:
                {
                    string id = Text(a, "id"); if (!Settings.Devices.Any(d => d.Id == id)) throw new ArgumentException("Unknown device.");
                    next = Settings with { Devices = Settings.Devices.Select(d => d.Id == id ? d with { Dismissed = true } : d).ToArray() }; break;
                }
                case Commands.SetupDevice:
                {
                    var device = RegistryDevice(a); next = Settings;
                    if (device.Status == DeviceStatus.NeedsToken) throw new ArgumentException("Import a token for this device first.");
                    if (device.Kind == DeviceKind.RgbController)
                        next = next with { SyncedDevices = new(Settings.SyncedDevices) { [device.Id] = true },
                            Map = Settings.Map with { Devices = Settings.Map.Devices.Any(p => p.Device == device.Id) ? Settings.Map.Devices : Settings.Map.Devices.Append(new DevicePlacement(device.Id, 0.5, 0.5)).ToArray() } };
                    if (device.Kind == DeviceKind.FanHeader && int.TryParse(device.Id.AsSpan(4), out var header) && header is not (2 or 6 or 7))
                        next = next with { HeaderFans = new(Settings.HeaderFans) { [header] = new() } };
                    if (device.Kind == DeviceKind.Lamp && device.Source == "yeelight" && !Settings.ConfiguredLamps.Any(l => l.Id == device.Id))
                        next = next with { Lamps = Settings.ConfiguredLamps.Where(l => l.DeviceId is not null || l.Host is not null).Append(new LampConfiguration(Host: device.Ip, Id: device.Id, Name: device.Name, DeviceId: device.DeviceId, LastHost: device.Ip, Transport: "yeelight")).ToArray() };
                    next = next with { Devices = next.Devices.Select(d => d.Id == device.Id ? d with { Configured = true } : d).ToArray() }; break;
                }
                case Commands.ImportTokens:
                    next = Import(a); break;
                case Commands.SetDiscoveryOptions:
                    next = Settings with { Discovery = a.GetProperty("options").Deserialize<DiscoveryOptions>(PipeContract.Json) ?? throw new ArgumentException("Missing options.") }; break;
                case Commands.PrepareApplianceStart:
                {
                    if (Text(a, "origin") != "app") throw new ArgumentException("Start is available only in the app.");
                    var nonce = await runtime.PrepareApplianceAsync(RegistryDevice(a), a.GetProperty("siid").GetInt32(), a.GetProperty("aiid").GetInt32(), cancellationToken);
                    return new(request.Id, true, Data: JsonSerializer.SerializeToElement(new { confirmation = nonce }));
                }
                case Commands.ConfirmApplianceStart:
                    if (Text(a, "origin") != "app" || !a.GetProperty("confirmed").GetBoolean()) throw new ArgumentException("Explicit app confirmation required.");
                    await runtime.ActApplianceAsync(RegistryDevice(a), a.GetProperty("siid").GetInt32(), a.GetProperty("aiid").GetInt32(), Text(a, "confirmation"), cancellationToken); return new(request.Id, true);
                case Commands.ApplianceAction:
                {
                    var device = RegistryDevice(a); int siid = a.GetProperty("siid").GetInt32(), aiid = a.GetProperty("aiid").GetInt32();
                    var action = MiotSpec.Load(device.Model ?? "")?.Actions.FirstOrDefault(x => x.Siid == siid && x.Aiid == aiid);
                    if (action?.Name is not ("cancel-cooking" or "pause")) throw new ArgumentException("Cooking actions require explicit app confirmation.");
                    await runtime.ActApplianceAsync(device, siid, aiid, null, cancellationToken); return new(request.Id, true);
                }
                case Commands.ApplianceProperties:
                    await runtime.SetApplianceAsync(RegistryDevice(a), a.GetProperty("values"), cancellationToken); return new(request.Id, true);
                case Commands.SetSensorBinding:
                {
                    var target = Text(a, "target"); var state = runtime.GetState(Settings);
                    if (!SensorSources.ValidTarget(target, state.Lighting.Devices, Settings.ConfiguredLamps.Select(l => l.Id))) throw new ArgumentException("Unknown sensor target.");
                    var bindings = new Dictionary<string, SensorBinding>(Settings.Bindings);
                    if (a.TryGetProperty("source", out var source) && source.ValueKind != JsonValueKind.Null)
                    {
                        var binding = a.Deserialize<SensorBinding>(PipeContract.Json) ?? throw new ArgumentException("Missing binding.");
                        SensorSources.Validate(binding); bindings[target] = binding;
                    }
                    else bindings.Remove(target);
                    next = Settings with { Bindings = bindings }; break;
                }
                case Commands.SetEffectParams:
                    next = Settings with { Lighting = Settings.Lighting with { Parameters = a.GetProperty("parameters").Deserialize<EffectParameters>(PipeContract.Json) ?? throw new ArgumentException("Missing parameters.") } }; break;
                case Commands.SetLightingMap:
                    var map = a.GetProperty("map").Deserialize<LightingMap>(PipeContract.Json) ?? throw new ArgumentException("Missing map.");
                    next = Settings with { Map = map, Lamps = Settings.Lamps?.Select(l => l with { MapPoint = map.Lamps?.GetValueOrDefault(l.Id) }).ToArray() }; break;
                case Commands.SetEventEffects:
                    next = Settings with { Events = a.GetProperty("events").Deserialize<EventEffectOptions>(PipeContract.Json) ?? throw new ArgumentException("Missing events.") }; break;
                case Commands.NotifyLighting:
                    var notification = EnumValue<LightingEventKind>(a, "kind");
                    if (notification != LightingEventKind.Notification) throw new ArgumentException("Only Notification can be sent externally.");
                    runtime.TriggerLightingEvent(notification); return new(request.Id, true);
                case Commands.SaveLightingScene:
                    var sceneName = Text(a, "name");
                    if (!Settings.Scenes.TryGetValue(sceneName, out var saved)) throw new ArgumentException("Unknown scene.");
                    var scenes = new Dictionary<string, SceneConfiguration>(Settings.Scenes) { [sceneName] = saved with { Lighting = Settings.Lighting } };
                    next = Settings with { Scenes = scenes }; break;
                case Commands.SetLedColors:
                {
                    var device = Device(a); var layer = Text(a, "layer");
                    if (layer is not ("custom" or "overlay")) throw new ArgumentException("Invalid LED layer.");
                    var colors = a.GetProperty("colors").EnumerateArray().Select(c => c.GetString() ?? "").ToArray();
                    if (colors.Length != device.LedCount || colors.Any(c => !LedLayers.Valid(c, layer == "overlay")))
                        throw new ArgumentException("Provide one valid colour per device LED.");
                    var layers = new Dictionary<string, LedConfiguration>(Settings.LedLayers);
                    var current = layers.GetValueOrDefault(device.Id) ?? new();
                    layers[device.Id] = layer == "custom" ? current with { Custom = colors, CustomEnabled = true } : current with { Overlay = colors, CustomEnabled = false };
                    next = Settings with { LedLayers = layers }; break;
                }
                case Commands.ClearLedColors:
                {
                    var device = Device(a); var layers = new Dictionary<string, LedConfiguration>(Settings.LedLayers);
                    layers.Remove(device.Id); next = Settings with { LedLayers = layers }; break;
                }
                case Commands.IdentifyZone:
                case Commands.ResizeZone:
                {
                    var device = Device(a); int zone = a.GetProperty("zone").GetInt32(), size = a.GetProperty("size").GetInt32();
                    if (device.Zones is null || zone < 0 || zone >= device.Zones.Count) throw new ArgumentException("Unknown zone.");
                    var z = device.Zones[zone];
                    if (z.MinLeds == z.MaxLeds || size < z.MinLeds || size > z.MaxLeds || size > ushort.MaxValue || device.LedCount - z.LedCount + size > ushort.MaxValue)
                        throw new ArgumentException("Invalid zone size.");
                    var resized = await runtime.ResizeZoneAsync(device.Id, zone, size, cancellationToken);
                    var sizes = Settings.ZoneSizes.ToDictionary(p => p.Key, p => new Dictionary<int, int>(p.Value));
                    if (!sizes.ContainsKey(device.Id)) sizes[device.Id] = new();
                    sizes[device.Id][zone] = size;
                    var layers = new Dictionary<string, LedConfiguration>(Settings.LedLayers);
                    if (layers.TryGetValue(device.Id, out var current)) layers[device.Id] = current with {
                        Custom = current.Custom is null ? null : LedLayers.Resize(current.Custom, resized.LedCount, false),
                        Overlay = current.Overlay is null ? null : LedLayers.Resize(current.Overlay, resized.LedCount, true) };
                    next = Settings with { ZoneSizes = sizes, LedLayers = layers };
                    if (request.Command == Commands.IdentifyZone) runtime.IdentifyZone(device.Id, zone, size);
                    else runtime.EndIdentification(device.Id);
                    break;
                }
                case Commands.SetLampToken:
                {
                    var host = a.TryGetProperty("host", out var h) ? h.GetString() : null;
                    if (host is not null) ServiceSettings.ValidateHost(host);
                    uint? deviceId = a.TryGetProperty("deviceId", out var did) && did.ValueKind != JsonValueKind.Null ? ParseDeviceId(did) : null;
                    if (deviceId is null && host is not null) deviceId = (await runtime.DiscoverLampsAsync(cancellationToken)).FirstOrDefault(d => d.Ip == host)?.DeviceId;
                    if (deviceId is null && host is null) throw new ArgumentException("Device id or host required.");
                    var token = Text(a, "token");
                    if (token.Length != 32 || !token.All(Uri.IsHexDigit)) throw new ArgumentException("Token must be 32 hexadecimal digits.");
                    var lamps = Settings.ConfiguredLamps.ToList();
                    var id = deviceId is { } d ? $"lamp:{d}" : "unresolved";
                    var index = lamps.FindIndex(l => l.Id == id);
                    if (index < 0 && lamps.Count == 1 && lamps[0] is { Id: "unresolved", Host: null, LastHost: null }) index = 0;
                    var existing = index >= 0 ? lamps[index] : new LampConfiguration();
                    var config = existing with { Id = id, DeviceId = deviceId, Host = host ?? existing.Host, LastHost = host ?? existing.LastHost, Name = a.TryGetProperty("name", out var n) ? n.GetString() ?? "Lamp" : existing.Name };
                    ServiceSettings.ValidateLamp(config);
                    var secret = Convert.FromHexString(token);
                    try { if (deviceId is { } key) saveDeviceToken(key, secret); else saveLampToken(secret); }
                    finally { System.Security.Cryptography.CryptographicOperations.ZeroMemory(secret); }
                    if (index < 0) lamps.Add(config); else lamps[index] = config;
                    runtime.LampCredentialsChanged(id);
                    next = Settings with { Lamps = lamps, Lamp = lamps[0] }; break;
                }
                case Commands.RemoveLamp:
                {
                    var id = Text(a, "lamp"); var lamps = Settings.ConfiguredLamps.ToList();
                    var target = lamps.FirstOrDefault(l => l.Id == id) ?? throw new ArgumentException("Unknown lamp.");
                    lamps.Remove(target);
                    next = Settings with { Lamps = lamps, Lamp = lamps.FirstOrDefault() ?? new(),
                        Map = Settings.Map with { Lamps = Settings.Map.Lamps?.Where(p => p.Key != id).ToDictionary(p => p.Key, p => p.Value) } };
                    removedToken = target.DeviceId; removedLegacyToken = target.DeviceId is null;
                    break;
                }
                case Commands.SetRouterPassword:
                {
                    var password = Text(a, "password");
                    if (string.IsNullOrWhiteSpace(password) || password.Length > 1024) throw new ArgumentException("Invalid router password.");
                    saveRouterPassword(password); runtime.CredentialsChanged(false); next = Settings; break;
                }
                case Commands.SetServiceOptions:
                {
                    next = Settings with { OpenRgbPath = Text(a, "openRgbPath"), LaunchOpenRgb = a.GetProperty("launchOpenRgb").GetBoolean(),
                        RouterEnabled = a.GetProperty("routerEnabled").GetBoolean(), RouterHost = Text(a, "routerHost") }; break;
                }
                case Commands.SetFanMode:
                {
                    var group = Group(a); var mode = EnumValue<FanMode>(a, "mode");
                    if (group.StartsWith("fan:", StringComparison.Ordinal))
                    {
                        int header = int.Parse(group.AsSpan(4)); var headers = new Dictionary<int, FanConfiguration>(Settings.HeaderFans);
                        var current = headers.GetValueOrDefault(header) ?? new();
                        if (mode == FanMode.Custom && current.Points is null) throw new ArgumentException("Set a custom curve first.");
                        headers[header] = current with { Mode = mode }; next = Settings with { HeaderFans = headers }; break;
                    }
                    var fans = new Dictionary<string, FanConfiguration>(Settings.Fans);
                    if (mode == FanMode.Custom && fans[group].Points is null) throw new ArgumentException("Set a custom curve first.");
                    fans[group] = fans[group] with { Mode = mode }; next = Settings with { Fans = fans }; break;
                }
                case Commands.SetFanCurve:
                {
                    var group = Group(a); var source = EnumValue<TemperatureSource>(a, "source");
                    var points = a.GetProperty("points").EnumerateArray().Select(p =>
                    {
                        if (p.ValueKind != JsonValueKind.Array || p.GetArrayLength() != 2) throw new ArgumentException("Points must be [temperature,percent].");
                        return new FanPoint(p[0].GetDouble(), p[1].GetDouble());
                    }).ToArray();
                    if (group.StartsWith("fan:", StringComparison.Ordinal))
                    { var headers = new Dictionary<int, FanConfiguration>(Settings.HeaderFans) { [int.Parse(group.AsSpan(4))] = new(FanMode.Custom, source, points) }; next = Settings with { HeaderFans = headers }; break; }
                    var fans = new Dictionary<string, FanConfiguration>(Settings.Fans) { [group] = new(FanMode.Custom, source, points) };
                    next = Settings with { Fans = fans }; break;
                }
                case Commands.SetAllLighting:
                {
                    var lighting = Settings.Lighting with {
                        Effect = a.TryGetProperty("effect", out _) ? EnumValue<LightingEffect>(a, "effect") : Settings.Lighting.Effect,
                        Color = a.TryGetProperty("color", out _) ? Text(a, "color") : Settings.Lighting.Color,
                        Brightness = a.TryGetProperty("brightness", out var b) ? b.GetDouble() : Settings.Lighting.Brightness,
                        Speed = a.TryGetProperty("speed", out var sp) ? sp.GetDouble() : Settings.Lighting.Speed };
                    bool reset = a.TryGetProperty("resetOverrides", out var r) && r.GetBoolean();
                    var lamps = Settings.ConfiguredLamps.Select(l => l with { Follow = "pc", On = lighting.Effect != LightingEffect.Off && lighting.Brightness > 0,
                        Brightness = Math.Max(1, (int)Math.Round(lighting.Brightness * 100)), Color = lighting.Color, Kelvin = null }).ToArray();
                    var parameters = lighting.Parameters;
                    if (parameters is not null) lighting = lighting with { Parameters = parameters with { Devices = null } };
                    next = Settings with { Lighting = lighting, Lamps = lamps,
                        SyncedDevices = runtime.GetState(Settings).Lighting.Devices.ToDictionary(d => d.Id, _ => true),
                        LedLayers = reset ? new() : Settings.LedLayers, Bindings = reset ? new() : Settings.Bindings };
                    break;
                }
                case Commands.SetLighting:
                    next = Settings with { Lighting = new(EnumValue<LightingEffect>(a, "effect"), Text(a, "color"), a.GetProperty("brightness").GetDouble(), a.GetProperty("speed").GetDouble(), Settings.Lighting.Parameters) }; break;
                case Commands.SetDeviceSync:
                    var sync = new Dictionary<string, bool>(Settings.SyncedDevices) { [Text(a, "device")] = a.GetProperty("enabled").GetBoolean() };
                    next = Settings with { SyncedDevices = sync }; break;
                case Commands.SetLamp:
                {
                    var lamps = Settings.ConfiguredLamps.ToList();
                    var id = a.TryGetProperty("lamp", out var selected) ? selected.GetString() ?? lamps.FirstOrDefault()?.Id : lamps.FirstOrDefault()?.Id;
                    var index = lamps.FindIndex(l => l.Id == id);
                    if (index < 0) throw new ArgumentException("Unknown lamp.");
                    var current = lamps[index];
                    lamps[index] = current with { On = a.TryGetProperty("on", out var on) ? on.GetBoolean() : current.On,
                        Brightness = a.TryGetProperty("brightness", out var brightness) ? brightness.GetInt32() : current.Brightness,
                        Follow = a.TryGetProperty("follow", out var follow) ? follow.GetString()! : current.Follow,
                        Color = a.TryGetProperty("color", out var color) ? color.GetString()! : current.Color, Kelvin = a.TryGetProperty("color", out _) ? null : current.Kelvin,
                        Name = a.TryGetProperty("name", out var name) ? name.GetString()! : current.Name };
                    next = Settings with { Lamps = lamps, Lamp = lamps[0] }; break;
                }
                case Commands.SaveScene:
                {
                    var savedName = Text(a, "name").Trim();
                    if (savedName.Length is 0 or > 32) throw new ArgumentException("Scene name must be 1-32 characters.");
                    if (!Enum.TryParse<FanMode>(Text(a, "fans"), true, out var sceneFans) || sceneFans == FanMode.Custom) throw new ArgumentException("Unknown fan mode.");
                    if (!Enum.TryParse<LightingEffect>(Text(a, "effect"), true, out var sceneEffect)) throw new ArgumentException("Unknown effect.");
                    var sceneColor = Text(a, "color");
                    if (!Prism.Lighting.LedLayers.Valid(sceneColor, false)) throw new ArgumentException("Invalid colour.");
                    var sceneBrightness = a.TryGetProperty("brightness", out var sb) && sb.TryGetDouble(out var sbv) ? Math.Clamp(sbv, 0.05, 1) : 1;
                    var sceneLamp = Text(a, "lamps") switch
                    {
                        "off" => new LampConfiguration(On: false),
                        "pc" => new LampConfiguration(Follow: "pc"),
                        "cool" => new LampConfiguration(Brightness: 80, Color: "#DCEEFF", Kelvin: 5500),
                        "warm" => new LampConfiguration(Brightness: 80, Color: "#FFD5A8", Kelvin: 3000),
                        _ => throw new ArgumentException("Unknown lamp setting.")
                    };
                    var existing = Settings.Scenes.GetValueOrDefault(savedName);
                    var lighting = (existing?.Lighting ?? new LightingConfiguration()) with { Effect = sceneEffect, Color = sceneColor, Brightness = sceneBrightness };
                    next = Settings with { Scenes = new Dictionary<string, SceneConfiguration>(Settings.Scenes) { [savedName] = new(sceneFans, lighting, sceneLamp) } };
                    break;
                }
                case Commands.ApplyScene:
                    if (!Settings.Scenes.TryGetValue(Text(a, "name"), out var scene)) throw new ArgumentException("Unknown scene.");
                    next = WithScene(Settings, scene); break;
                case Commands.SetAutomations:
                    next = Settings with { Automations = UpdateAutomations(Settings.Automations, a) }; break;
                default: throw new ArgumentException("Unknown command.");
            }
            if (next.Lamps is not null) next = next with { Lamp = next.Lamps.FirstOrDefault() ?? new() };
            if (request.Command == Commands.SetLampToken)
            {
                var now = DateTimeOffset.UtcNow;
                next = next with { Devices = DeviceRegistry.Observe(next.Devices, next.ConfiguredLamps.Where(l => l.DeviceId is not null).Select(l =>
                    new DeviceState(l.Id, DeviceKind.Lamp, l.Name, "miio", now, now, DeviceStatus.Ready, ["color", "brightness"], Model: "yeelink.light.color5", DeviceId: l.DeviceId, Ip: l.LastHost ?? l.Host, Configured: true)), now) };
            }
            if (request.Command == Commands.SetLamp && a.TryGetProperty("name", out var renamedName))
            {
                string id = a.TryGetProperty("lamp", out var selected) ? selected.GetString() ?? next.Lamp.Id : next.Lamp.Id;
                next = next with { Devices = next.Devices.Select(d => d.Id == id ? DeviceRegistry.Rename(d, renamedName.GetString()) : d).ToArray() };
            }
            if (request.Command == Commands.RemoveLamp)
                next = next with { Devices = next.Devices.Select(d => d.Id == Text(a, "lamp") ? d with { Configured = false } : d).ToArray() };
            next.Validate(); store.Save(next); NoteUserChange(Settings, next); Volatile.Write(ref settings, next);
            // Once persisted, a disconnect must not leave the running adapters on the old settings.
            await runtime.ApplyAsync(next, CancellationToken.None);
            if (removedToken is { } removed) removeDeviceToken(removed);
            if (removedLegacyToken) lampTokens.DeleteLegacy();
            if (request.Command == Commands.ApplyScene) runtime.TriggerLightingEvent(LightingEventKind.SceneChange);
            return new(request.Id, true);
        }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException or KeyNotFoundException or JsonException or FormatException or OverflowException)
        { return new(request.Id, false, request.Command is Commands.SetLampToken or Commands.SetRouterPassword or Commands.ImportTokens ? "Credential command failed." : e.Message); }
        catch (System.Security.Cryptography.CryptographicException) { return new(request.Id, false, "Credentials could not be protected."); }
        catch (System.Net.Sockets.SocketException) { return new(request.Id, false, "Device connection failed."); }
        catch (IOException) { return new(request.Id, false, "Settings could not be saved."); }
        catch (UnauthorizedAccessException) { return new(request.Id, false, "Settings access denied."); }
        finally { gate.Release(); }
    }
    private async Task<Response> SpeedResponseAsync(int id, CancellationToken token)
    {
        try
        {
            var result = await runtime.StartSpeedTestAsync(token);
            return result is null ? new(id, false, "Speed test cancelled.") : new(id, true, Data: JsonSerializer.SerializeToElement(result, PipeContract.Json));
        }
        catch (Exception e) when (e is InvalidOperationException or SpeedTestError or IOException)
        { return new(id, false, e is InvalidOperationException ? e.Message : "Speed test failed."); }
    }
    private RgbDeviceState Device(JsonElement a)
    {
        var id = Text(a, "device");
        return runtime.GetState(Settings).Lighting.Devices.FirstOrDefault(d => d.Id == id && d.Connected)
            ?? throw new ArgumentException("Unknown or disconnected device.");
    }
    public static uint ParseDeviceId(JsonElement value)
    {
        var text = value.ValueKind == JsonValueKind.Number ? value.GetRawText() : (value.GetString() ?? "").Trim();
        var hex = text.StartsWith("0x", StringComparison.OrdinalIgnoreCase);
        if (!uint.TryParse(hex ? text[2..] : text, hex ? System.Globalization.NumberStyles.HexNumber : System.Globalization.NumberStyles.None,
            System.Globalization.CultureInfo.InvariantCulture, out var id) || id is 0 or uint.MaxValue) throw new ArgumentException("Invalid device id.");
        return id;
    }
    private static string Text(JsonElement a, string key) => a.GetProperty(key).GetString() ?? throw new ArgumentException($"Missing {key}.");
    private static T EnumValue<T>(JsonElement a, string key) where T : struct, Enum =>
        Enum.TryParse<T>(Text(a, key), false, out var value) && Enum.IsDefined(value) && !int.TryParse(Text(a, key), out _)
            ? value : throw new ArgumentException($"Invalid {key}.");
    private string Group(JsonElement a)
    {
        string group = Text(a, "group");
        if (group is "Radiator" or "Case fans") return group;
        if (group.StartsWith("fan:", StringComparison.Ordinal) && int.TryParse(group.AsSpan(4), out var header) && header is >= 1 and <= 6 && Settings.Devices.Any(d => d.Id == group && d.Kind == DeviceKind.FanHeader))
            return header == 2 ? "Radiator" : header == 6 ? "Case fans" : group;
        throw new ArgumentException("Unknown or read-only fan group.");
    }
}
