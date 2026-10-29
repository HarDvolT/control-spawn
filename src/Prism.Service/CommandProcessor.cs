using System.Text.Json;
using Prism.Core;
using Prism.Core.Ipc;
using Prism.Router;
using Prism.Lighting;

namespace Prism.Service;

public interface IServiceRuntime
{
    StateMessage GetState(ServiceSettings settings);
    Task ApplyAsync(ServiceSettings settings, CancellationToken cancellationToken);
    Task<RgbDeviceState> ResizeZoneAsync(string device, int zone, int size, CancellationToken cancellationToken) =>
        Task.FromException<RgbDeviceState>(new InvalidOperationException("Zone resizing is unavailable."));
    Task<IReadOnlyList<MiioDevice>> DiscoverLampsAsync(CancellationToken token) => Task.FromResult<IReadOnlyList<MiioDevice>>([]);
    void SetLampResolutionHandler(Func<string, MiioDevice, Task> handler) { }
    void CredentialsChanged(bool lamp) { }
    void LampCredentialsChanged(string id) => CredentialsChanged(true);
    void TriggerLightingEvent(LightingEventKind kind) { }
    Task<SpeedTestResult?> StartSpeedTestAsync(CancellationToken cancellationToken);
}

/// <summary>Serialized settings commands; connection-local hello/subscribe live in PipeSession.</summary>
public sealed class CommandProcessor
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
    public StateMessage GetState() => runtime.GetState(Settings);
    public async Task<Response> HandleAsync(Request request, CancellationToken cancellationToken = default)
    {
        // A speed test must not hold the settings gate while fans/scenes need to change.
        if (request.Command == Commands.StartSpeedTest) return await SpeedResponseAsync(request.Id, cancellationToken);
        await gate.WaitAsync(cancellationToken);
        try
        {
            if (request.Command == Commands.GetState) return new(request.Id, true, Data: JsonSerializer.SerializeToElement(GetState(), PipeContract.Json));
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
                    next = Settings with { ZoneSizes = sizes, LedLayers = layers }; break;
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
                    var fans = new Dictionary<string, FanConfiguration>(Settings.Fans) { [group] = new(FanMode.Custom, source, points) };
                    next = Settings with { Fans = fans }; break;
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
                case Commands.ApplyScene:
                    if (!Settings.Scenes.TryGetValue(Text(a, "name"), out var scene)) throw new ArgumentException("Unknown scene.");
                    next = Settings with { Fans = Settings.Fans.ToDictionary(f => f.Key, f => f.Value with { Mode = scene.Fans }),
                        Lighting = scene.Lighting, Lamp = scene.Lamp with { Host = Settings.Lamp.Host },
                        Lamps = Settings.Lamps?.Select(l => l with { On = scene.Lamp.On, Brightness = scene.Lamp.Brightness, Color = scene.Lamp.Color, Follow = scene.Lamp.Follow, Kelvin = scene.Lamp.Kelvin }).ToArray() }; break;
                default: throw new ArgumentException("Unknown command.");
            }
            if (next.Lamps is not null) next = next with { Lamp = next.Lamps.FirstOrDefault() ?? new() };
            next.Validate(); store.Save(next); Volatile.Write(ref settings, next);
            // Once persisted, a disconnect must not leave the running adapters on the old settings.
            await runtime.ApplyAsync(next, CancellationToken.None);
            if (removedToken is { } removed) removeDeviceToken(removed);
            if (removedLegacyToken) lampTokens.DeleteLegacy();
            if (request.Command == Commands.ApplyScene) runtime.TriggerLightingEvent(LightingEventKind.SceneChange);
            return new(request.Id, true);
        }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException or KeyNotFoundException or JsonException or FormatException or OverflowException)
        { return new(request.Id, false, request.Command is Commands.SetLampToken or Commands.SetRouterPassword ? "Credential command failed." : e.Message); }
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
    private static string Group(JsonElement a) => Text(a, "group") is "Radiator" or "Case fans" ? Text(a, "group") : throw new ArgumentException("Unknown or read-only fan group.");
}
