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
    void CredentialsChanged(bool lamp) { }
    Task<SpeedTestResult?> StartSpeedTestAsync(CancellationToken cancellationToken);
}

/// <summary>Serialized settings commands; connection-local hello/subscribe live in PipeSession.</summary>
public sealed class CommandProcessor
{
    private readonly ISettingsStore store;
    private readonly IServiceRuntime runtime;
    private readonly Action<byte[]> saveLampToken;
    private readonly Action<string> saveRouterPassword;
    private readonly SemaphoreSlim gate = new(1, 1);
    private ServiceSettings settings;
    public ServiceSettings Settings => Volatile.Read(ref settings);
    public CommandProcessor(ISettingsStore store, IServiceRuntime runtime, Action<byte[]>? saveLampToken = null, Action<string>? saveRouterPassword = null)
    {
        this.store = store; this.runtime = runtime;
        this.saveLampToken = saveLampToken ?? (token => new LampTokenStore().Save(token));
        this.saveRouterPassword = saveRouterPassword ?? (password => { var credentials = new CredentialStore(); credentials.SavePassword(password); credentials.ForgetSession(); });
        settings = store.Load(); Settings.Validate();
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
            var args = request.Args;
            if (args is null || args.Value.ValueKind != JsonValueKind.Object) throw new ArgumentException("Command args must be an object.");
            var a = args.Value;
            ServiceSettings next;
            switch (request.Command)
            {
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
                    var host = Text(a, "host"); ServiceSettings.ValidateHost(host);
                    var token = Text(a, "token");
                    if (token.Length != 32 || !token.All(Uri.IsHexDigit)) throw new ArgumentException("Token must be 32 hexadecimal digits.");
                    saveLampToken(Convert.FromHexString(token));
                    runtime.CredentialsChanged(true);
                    next = Settings with { Lamp = Settings.Lamp with { Host = host } }; break;
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
                    next = Settings with { Lighting = new(EnumValue<LightingEffect>(a, "effect"), Text(a, "color"), a.GetProperty("brightness").GetDouble(), a.GetProperty("speed").GetDouble()) }; break;
                case Commands.SetDeviceSync:
                    var sync = new Dictionary<string, bool>(Settings.SyncedDevices) { [Text(a, "device")] = a.GetProperty("enabled").GetBoolean() };
                    next = Settings with { SyncedDevices = sync }; break;
                case Commands.SetLamp:
                    next = Settings with { Lamp = Settings.Lamp with { On = a.GetProperty("on").GetBoolean(), Brightness = a.GetProperty("brightness").GetInt32(), Follow = Text(a, "follow"), Color = Text(a, "color"), Kelvin = null } }; break;
                case Commands.ApplyScene:
                    if (!Settings.Scenes.TryGetValue(Text(a, "name"), out var scene)) throw new ArgumentException("Unknown scene.");
                    next = Settings with { Fans = Settings.Fans.ToDictionary(f => f.Key, f => f.Value with { Mode = scene.Fans }),
                        Lighting = scene.Lighting, Lamp = scene.Lamp with { Host = Settings.Lamp.Host } }; break;
                default: throw new ArgumentException("Unknown command.");
            }
            next.Validate(); store.Save(next); Volatile.Write(ref settings, next);
            // Once persisted, a disconnect must not leave the running adapters on the old settings.
            await runtime.ApplyAsync(next, CancellationToken.None);
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
    private static string Text(JsonElement a, string key) => a.GetProperty(key).GetString() ?? throw new ArgumentException($"Missing {key}.");
    private static T EnumValue<T>(JsonElement a, string key) where T : struct, Enum =>
        Enum.TryParse<T>(Text(a, key), false, out var value) && Enum.IsDefined(value) && !int.TryParse(Text(a, key), out _)
            ? value : throw new ArgumentException($"Invalid {key}.");
    private static string Group(JsonElement a) => Text(a, "group") is "Radiator" or "Case fans" ? Text(a, "group") : throw new ArgumentException("Unknown or read-only fan group.");
}
