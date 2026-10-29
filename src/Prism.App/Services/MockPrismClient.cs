using System.Text.Json;
using Prism.App.Common;
using Prism.App.Models;
using Prism.Core;
using Prism.Lighting;
using Prism.Core.Ipc;

namespace Prism.App.Services;

/// <summary>
/// Realistic changing data for `--mock`, based on the verified hardware in docs/HARDWARE.md.
/// It never touches hardware, the router or the network.
/// </summary>
public sealed class MockPrismClient : PrismClientBase
{
    private readonly object _gate = new();
    private readonly Random _random = new(20261029);
    private Timer? _timer;
    private StateMessage? _last;
    private int _tick;
    private int _stripSize;
    private readonly HashSet<string> _customEnabled = new();
    private readonly Dictionary<string, string[]> _custom = new(), _overlay = new();
    private bool _disposed;

    private double _cpu = 22;
    private double _gpu = 35;
    private double _cpuTemp = 48;
    private double _gpuTemp = 41;
    private double _ram = 14.2;
    private double _vram = 3.1;
    private double _netDown = 60;
    private double _netUp = 8;
    private double _ping = 21;
    private double _diskRead = 120;
    private double _diskWrite = 40;

    private FanMode _fanMode = FanMode.Standard;
    private EffectParameters _parameters = new();
    private LightingMap _map = new([]);
    private EventEffectOptions _events = new();
    private readonly Dictionary<string, (LightingEffect Effect, string Color, double Brightness, double Speed, EffectParameters Parameters)> _lightingScenes = new();
    private LightingEffect _effect = LightingEffect.Smart;
    private string _lightColor = "#FF4D1A";
    private double _brightness = 0.8;
    private double _speed = 1;
    private readonly HashSet<string> _unsyncedDevices = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, IReadOnlyList<FanPoint>> _curves = new(StringComparer.OrdinalIgnoreCase);
    private string _curveSource = "Cpu";

    private readonly Dictionary<string, LampState> extraLamps = new() { ["lamp:470124183"] = new("yeelink.light.color5", true, true, 60, "#5AA7FF", "own", "miio", "lamp:470124183", "Desk lamp", 470124183, "192.0.2.2") };
    private string firstLampName = "Room lamp";
    private bool firstRemoved;
    private bool _lampOn = true;
    private int _lampBrightness = 70;
    private string _lampFollow = "pc";
    private string _lampColor = "#FF4D1A";

    public MockPrismClient()
    {
        _last = BuildState();
    }

    public string ActiveScene { get; private set; } = "Gaming";

    public StateMessage CurrentState
    {
        get { lock (_gate) return _last ?? BuildState(); }
    }

    public override Task StartAsync(CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            if (_timer is not null) return Task.CompletedTask;
            SetConnection(ServiceConnection.Connected);
            _timer = new Timer(_ => Tick(), null, TimeSpan.Zero, TimeSpan.FromSeconds(1));
        }
        return Task.CompletedTask;
    }

    private void Tick()
    {
        if (_disposed) return;
        StateMessage state;
        lock (_gate)
        {
            _cpu = Jitter(_cpu, 9, 3, 92);
            _gpu = Jitter(_gpu, 13, 2, 99);
            _cpuTemp = Jitter(_cpuTemp, 1.6, 40, 72);
            _gpuTemp = Jitter(_gpuTemp, 1.2, 34, 70);
            _ram = Jitter(_ram, 0.35, 10, 24);
            _vram = Jitter(_vram, 0.25, 2, 9);
            _netDown = Jitter(_netDown, 40, 2, 300);
            _netUp = Jitter(_netUp, 6, 1, 40);
            _ping = _random.NextDouble() < 0.08 ? 45 + _random.NextDouble() * 30 : Jitter(21, 3, 15, 32);
            _diskRead = Jitter(_diskRead, 90, 0, 400);
            _diskWrite = Jitter(_diskWrite, 45, 0, 220);
            state = BuildState();
            _last = state;
        }
        Publish(state);
    }

    private double Jitter(double value, double amount, double low, double high) =>
        Math.Clamp(value + (_random.NextDouble() - 0.5) * amount, low, high);

    private StateMessage BuildState()
    {
        var now = DateTimeOffset.Now;
        var t = _tick++;
        double Wave(double baseValue, double amplitude, double k) =>
            baseValue + amplitude * Math.Sin(t * k) + amplitude * 0.35 * Math.Sin(t * k * 2.7);

        var coreLoads = Enumerable.Range(0, 16)
            .Select(i => (double?)Math.Clamp(_cpu + Math.Sin(t * 0.7 + i * 0.9) * 14 + (_random.NextDouble() - 0.5) * 8, 1, 100))
            .ToList();

        var system = new SystemState(
            CpuLoad: Math.Round(_cpu, 1),
            CpuCoreLoads: coreLoads,
            CpuClockMhz: Math.Round(4700 + _cpu * 6),
            CpuTemp: Math.Round(_cpuTemp, 1),
            CpuPowerW: Math.Round(30 + _cpu * 0.9),
            GpuLoad: Math.Round(_gpu, 1),
            GpuTemp: Math.Round(_gpuTemp, 1),
            GpuHotspot: Math.Round(_gpuTemp + 11, 1),
            GpuClockMhz: Math.Round(1800 + _gpu * 12),
            GpuPowerW: Math.Round(40 + _gpu * 2.6),
            GpuFanRpm: _gpu > 50 ? Math.Round(_gpu * 18) : 0,
            VramUsedGb: Math.Round(_vram, 1),
            VramTotalGb: 16,
            RamUsedGb: Math.Round(_ram, 1),
            RamTotalGb: 32,
            DimmTemps: new List<double?> { Math.Round(Wave(38.5, 0.6, 0.2), 1), Math.Round(Wave(38.3, 0.6, 0.25), 1) },
            BoardTemp: 39,
            Disks: new List<DiskState>
            {
                new("WD_BLACK SN850X 2TB", 41, Math.Round(_diskRead), Math.Round(_diskWrite), 62),
                new("Samsung 870 EVO 1TB", 34, 5, 2, 45)
            },
            NetDownMbps: Math.Round(_netDown, 1),
            NetUpMbps: Math.Round(_netUp, 1));

        var fans = new List<FanState>
        {
            new(2, "Radiator", RpmFor(1268, _fanMode), FanPercent(_fanMode), true, false, _curveSource),
            new(6, "Case fans", RpmFor(622, _fanMode), FanPercent(_fanMode), true, false, _curveSource),
            new(7, "AIO pump", 2801, 100, false, true, "Fixed 100%"),
            new(1, "Fan #1", 0, 0, true, false, "None"),
            new(3, "Fan #3", 0, 0, true, false, "None"),
            new(4, "Fan #4", 0, 0, true, false, "None"),
            new(5, "Fan #5", 0, 0, true, false, "None")
        };

        RgbDeviceState Device(string id, string name, string kind, int count, params RgbZoneState[] zones) =>
            new(id, name, kind, true, !_unsyncedDevices.Contains(id), count, zones,
                _custom.GetValueOrDefault(id), _overlay.GetValueOrDefault(id), _customEnabled.Contains(id));
        static RgbZoneState Zone(string name, int first, int count, int max = 0) =>
            new(name, "Linear", first, count, max == 0 ? count : 0, max == 0 ? count : max,
                Enumerable.Range(1, count).Select(i => $"LED {i}").ToArray());
        var devices = new List<RgbDeviceState>
        {
            Device("ram", "RAM 1", "RAM", 10, Zone("Vengeance RGB DDR5", 0, 10)),
            Device("ram2", "RAM 2", "RAM", 10, Zone("Vengeance RGB DDR5", 0, 10)),
            Device("gpu", "RX 9070 XT Taichi", "GPU", 4, Zone("GPU", 0, 4)),
            Device("board", "ROG STRIX B850-F", "Motherboard", 2 + _stripSize,
                Zone("Board", 0, 2), Zone("LED strip / ARGB header", 2, _stripSize, 120))
        };

        var lighting = new LightingState(_effect, _lightColor, _brightness, _speed, devices, _parameters, _map, _events);
        var lamp = new LampState("yeelink.light.color5", true, _lampOn, _lampBrightness, _lampColor, _lampFollow, "miio", "lamp:470875761", firstLampName, 470875761, "192.0.2.1");

        var rsrp5 = Math.Round(-97 + Math.Sin(t * 0.11) * 3);
        var rsrp4 = Math.Round(-92 + Math.Sin(t * 0.09) * 3);
        var sinr5 = Math.Round(7 + Math.Sin(t * 0.17) * 1.6, 1);
        var sinr4 = Math.Round(0.5 + Math.Sin(t * 0.13) * 0.8, 1);
        var rsrq5 = Math.Round(-11 + Math.Sin(t * 0.07) * 0.5, 1);
        var rsrq4 = Math.Round(-13 + Math.Sin(t * 0.05) * 0.5, 1);
        var score = SignalScore(rsrp5, sinr5, rsrq5);
        var verdict = score >= 65 ? "Good" : score >= 40 ? "Fair" : "Poor";
        var bars = score >= 80 ? 5 : score >= 65 ? 4 : score >= 45 ? 3 : score >= 25 ? 2 : 1;
        var sentence = verdict == "Good"
            ? "Strong and stable. Everything should feel fast."
            : verdict == "Fair"
                ? "Usable, speed may drop at busy times."
                : "Weak signal. Try moving the router near a window.";

        var router = new RouterState(true, "5G NSA · n78", "n78", score, bars, verdict, sentence,
            rsrp5, sinr5, rsrq5, rsrp4, sinr4, rsrq4,
            Math.Round(_ping), 0, null);

        var lamps = (firstRemoved ? Array.Empty<LampState>() : new[] { lamp }).Concat(extraLamps.Values).ToArray();
        return new StateMessage(now, system, fans, _fanMode, lighting, lamps.FirstOrDefault(), router,
            new ServiceHealth(true, false, Array.Empty<string>(), false), Lamps: lamps);
    }

    private static double RpmFor(double idleRpm, FanMode mode)
    {
        var percent = FanPercent(mode);
        if (percent >= 100) return Math.Round(idleRpm / 44 * 100);
        return Math.Round(idleRpm * percent / 44);
    }

    private static double FanPercent(FanMode mode) => mode switch
    {
        FanMode.Silent => 30,
        FanMode.Standard => 44,
        FanMode.Turbo => 70,
        FanMode.Full => 100,
        FanMode.Custom => 50,
        _ => 44
    };

    public static int SignalScore(double rsrp, double sinr, double rsrq)
    {
        static double Pos(double value, double min, double max) =>
            Math.Clamp((value - min) / (max - min) * 100, 0, 100);
        return (int)Math.Round(Pos(rsrp, -140, -44) * 0.35 + Pos(sinr, -10, 30) * 0.4 + Pos(rsrq, -20, -3) * 0.25);
    }

    public override Task<Response> SendAsync(string command, object? args = null, CancellationToken cancellationToken = default)
    {
        StateMessage? publish = null;
        Response response;
        var element = args is JsonElement json ? (JsonElement?)json : JsonLineProtocol.ToArgs(args);
        lock (_gate)
        {
            switch (command)
            {
                case Commands.Hello:
                case Commands.Subscribe:
                case Commands.StartSpeedTest:
                    response = new Response(0, true);
                    break;
                case Commands.GetState:
                    response = new Response(0, true, null,
                        JsonSerializer.SerializeToElement(_last ?? BuildState(), PipeContract.Json));
                    break;
                case Commands.SetEffectParams:
                case Commands.SetLightingMap:
                case Commands.SetEventEffects:
                    try
                    {
                        if (element is not { } a) throw new ArgumentException("Missing args.");
                        if (command == Commands.SetEffectParams) { var p = a.GetProperty("parameters").Deserialize<EffectParameters>(PipeContract.Json)!; LightingValidation.Parameters(p); _parameters = p; }
                        if (command == Commands.SetLightingMap) { var m = a.GetProperty("map").Deserialize<LightingMap>(PipeContract.Json)!; LightingValidation.Map(m); _map = m; }
                        if (command == Commands.SetEventEffects) _events = a.GetProperty("events").Deserialize<EventEffectOptions>(PipeContract.Json) ?? throw new ArgumentException("Missing events.");
                        publish = BuildState(); response = new(0, true);
                    }
                    catch (Exception e) when (e is ArgumentException or JsonException or KeyNotFoundException or InvalidOperationException)
                    { response = new(0, false, "Invalid lighting configuration."); }
                    break;
                case Commands.NotifyLighting:
                    response = TryString(element, "kind", out var eventKind) && eventKind == "Notification" ? new(0, true) : new(0, false, "Invalid notification."); break;
                case Commands.SaveLightingScene:
                    if (TryString(element, "name", out var lightingName) && SceneCatalog.All.Any(s => s.Name == lightingName))
                    { _lightingScenes[lightingName] = (_effect, _lightColor, _brightness, _speed, _parameters); response = new(0, true); }
                    else response = new(0, false, "Unknown scene.");
                    break;
                case Commands.SetLedColors:
                {
                    if (!TryString(element, "device", out var id) || !TryString(element, "layer", out var layer) || layer is not ("custom" or "overlay") ||
                        element is not { } a || !a.TryGetProperty("colors", out var colors) || colors.ValueKind != JsonValueKind.Array)
                    { response = new(0, false, "Invalid LED arguments"); break; }
                    var d = (_last ?? BuildState()).Lighting.Devices.FirstOrDefault(d => d.Id == id);
                    var values = colors.EnumerateArray().Select(c => c.ValueKind == JsonValueKind.String ? c.GetString() ?? "" : "").ToArray();
                    if (d is null || values.Length != d.LedCount || values.Any(c => c.Length != (layer == "custom" ? 7 : 9) || c[0] != '#' || !c.Skip(1).All(Uri.IsHexDigit)))
                    { response = new(0, false, "Provide one valid colour per LED"); break; }
                    (layer == "custom" ? _custom : _overlay)[id] = values;
                    if (layer == "custom") _customEnabled.Add(id); else _customEnabled.Remove(id);
                    publish = BuildState(); response = new(0, true); break;
                }
                case Commands.ClearLedColors:
                {
                    if (!TryString(element, "device", out var id) || !(_last ?? BuildState()).Lighting.Devices.Any(d => d.Id == id))
                    { response = new(0, false, "Unknown device"); break; }
                    _custom.Remove(id); _overlay.Remove(id); _customEnabled.Remove(id); publish = BuildState(); response = new(0, true); break;
                }
                case Commands.ResizeZone:
                {
                    if (!TryString(element, "device", out var id) || id != "board" || !TryNumber(element, "zone", out var zone) || zone != 1 ||
                        !TryNumber(element, "size", out var size) || !double.IsFinite(size) || size != Math.Truncate(size) || size < 0 || size > 120)
                    { response = new(0, false, "Invalid zone size"); break; }
                    _stripSize = (int)size;
                    if (_custom.TryGetValue(id, out var c)) _custom[id] = ViewModels.LedEditorViewModel.Resize(c, 2 + _stripSize, "custom");
                    if (_overlay.TryGetValue(id, out var o)) _overlay[id] = ViewModels.LedEditorViewModel.Resize(o, 2 + _stripSize, "overlay");
                    publish = BuildState(); response = new(0, true); break;
                }
                case Commands.DiscoverLamps:
                    response = new(0, true, Data: JsonSerializer.SerializeToElement(new[] {
                        new { ip = "192.0.2.1", deviceId = 470875761u, alreadyConfigured = !firstRemoved },
                        new { ip = "192.0.2.2", deviceId = 470124183u, alreadyConfigured = extraLamps.ContainsKey("lamp:470124183") } }, PipeContract.Json)); break;
                case Commands.RemoveLamp:
                    if (TryString(element, "lamp", out var removed) && (removed == "lamp:470875761" && !firstRemoved || extraLamps.ContainsKey(removed)))
                    { if (removed == "lamp:470875761") firstRemoved = true; else extraLamps.Remove(removed); publish = BuildState(); response = new(0, true); }
                    else response = new(0, false, "Unknown lamp");
                    break;
                case Commands.SetLampToken:
                    if (TryString(element, "token", out var token) && token.Length == 32 && token.All(Uri.IsHexDigit) &&
                        (TryString(element, "deviceId", out _) || TryNumber(element, "deviceId", out _) || TryString(element, "host", out _)))
                    {
                        if (TryString(element, "deviceId", out var did) || TryNumber(element, "deviceId", out _))
                        {
                            if (string.IsNullOrEmpty(did)) did = element!.Value.GetProperty("deviceId").GetRawText();
                            bool hex = did.StartsWith("0x", StringComparison.OrdinalIgnoreCase);
                            if (!uint.TryParse(hex ? did[2..] : did, hex ? System.Globalization.NumberStyles.HexNumber : System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var key) || key is 0 or uint.MaxValue)
                            { response = new(0, false, "Invalid lamp credential"); break; }
                            var id = $"lamp:{key}"; var name = TryString(element, "name", out var named) ? named : "Lamp";
                            if (key == 470875761) { firstRemoved = false; firstLampName = name; }
                            else extraLamps[id] = new("yeelink.light.color5", true, true, 60, "#FF4D1A", "own", "miio", id, name, key, "192.0.2.2");
                        }
                        publish = BuildState(); response = new(0, true);
                    }
                    else response = new(0, false, "Invalid lamp credential"); break;
                case Commands.SetRouterPassword:
                    response = TryString(element, "password", out var password) && !string.IsNullOrWhiteSpace(password) && password.Length <= 1024
                        ? new(0, true) : new(0, false, "Invalid router credential"); break;
                case Commands.SetServiceOptions:
                    response = TryString(element, "openRgbPath", out _) && TryBool(element, "launchOpenRgb", out _) &&
                        TryBool(element, "routerEnabled", out _) && TryString(element, "routerHost", out var routerHost) && Uri.CheckHostName(routerHost) != UriHostNameType.Unknown
                        ? new(0, true) : new(0, false, "Invalid service options"); break;
                case Commands.SetFanMode:
                    if (TryString(element, "mode", out var modeText) && Enum.TryParse<FanMode>(modeText, true, out var mode))
                    {
                        _fanMode = mode;
                        publish = BuildState();
                        response = new Response(0, true);
                    }
                    else response = new Response(0, false, "mode is required");
                    break;
                case Commands.SetFanCurve:
                {
                    var group = TryString(element, "group", out var g) ? g : "Radiator";
                    var points = ReadPoints(element);
                    if (points is { Count: 8 })
                    {
                        _curves[group] = points;
                        if (TryString(element, "source", out var source)) _curveSource = source;
                        publish = BuildState();
                        response = new Response(0, true);
                    }
                    else response = new Response(0, false, "eight points are required");
                    break;
                }
                case Commands.SetLighting:
                    if (TryString(element, "effect", out var effectText) && Enum.TryParse<LightingEffect>(effectText, true, out var effect))
                        _effect = effect;
                    if (TryString(element, "color", out var color)) _lightColor = color;
                    if (TryNumber(element, "brightness", out var brightness)) _brightness = Math.Clamp(brightness, 0, 1);
                    if (TryNumber(element, "speed", out var speed)) _speed = Math.Clamp(speed, 0, 2);
                    publish = BuildState();
                    response = new Response(0, true);
                    break;
                case Commands.SetDeviceSync:
                    if (TryString(element, "device", out var device) && TryBool(element, "enabled", out var enabled))
                    {
                        if (enabled) _unsyncedDevices.Remove(device); else _unsyncedDevices.Add(device);
                        publish = BuildState();
                        response = new Response(0, true);
                    }
                    else response = new Response(0, false, "device and enabled are required");
                    break;
                case Commands.SetLamp:
                    var target = TryString(element, "lamp", out var selectedLamp) ? selectedLamp : firstRemoved ? extraLamps.Keys.FirstOrDefault() : "lamp:470875761";
                    if (target is null) { response = new(0, false, "Unknown lamp"); break; }
                    if (target != "lamp:470875761")
                    {
                        if (!extraLamps.TryGetValue(target, out var current)) { response = new(0, false, "Unknown lamp"); break; }
                        extraLamps[target] = current with {
                            On = TryBool(element, "on", out var powered) ? powered : current.On,
                            Brightness = TryNumber(element, "brightness", out var level) ? (int)Math.Clamp(level, 1, 100) : current.Brightness,
                            Color = TryString(element, "color", out var colour) ? colour : current.Color,
                            Follow = TryString(element, "follow", out var followed) ? followed : current.Follow,
                            Name = TryString(element, "name", out var renamed) ? renamed : current.Name };
                        publish = BuildState(); response = new(0, true); break;
                    }
                    if (TryString(element, "name", out var firstName)) firstLampName = firstName;
                    if (TryBool(element, "on", out var on)) _lampOn = on;
                    if (TryNumber(element, "brightness", out var lampBrightness)) _lampBrightness = (int)Math.Clamp(lampBrightness, 1, 100);
                    if (TryString(element, "follow", out var follow)) _lampFollow = follow;
                    if (TryString(element, "color", out var lampColor)) _lampColor = lampColor;
                    publish = BuildState();
                    response = new Response(0, true);
                    break;
                case Commands.ApplyScene:
                {
                    if (!TryString(element, "name", out var name)) { response = new Response(0, false, "name is required"); break; }
                    var scene = SceneCatalog.Get(name);
                    ActiveScene = scene.Name;
                    _effect = scene.Effect;
                    _lightColor = scene.Color;
                    _fanMode = scene.FanMode;
                    _lampOn = scene.LampOn;
                    _lampBrightness = scene.LampBrightness;
                    _lampFollow = scene.LampFollow;
                    _lampColor = scene.Color;
                    foreach (var key in extraLamps.Keys.ToArray()) extraLamps[key] = extraLamps[key] with { On = scene.LampOn, Brightness = scene.LampBrightness, Follow = scene.LampFollow, Color = scene.Color };
                    if (_lightingScenes.TryGetValue(name, out var savedLighting))
                        (_effect, _lightColor, _brightness, _speed, _parameters) = savedLighting;
                    publish = BuildState();
                    response = new Response(0, true);
                    break;
                }
                default:
                    response = new Response(0, false, "Unknown command: " + command);
                    break;
            }
            if (publish is not null) _last = publish;
        }
        if (publish is not null) Publish(publish);
        return Task.FromResult(response);
    }

    private static bool TryString(JsonElement? args, string name, out string value)
    {
        value = string.Empty;
        if (args is not { } element || element.ValueKind != JsonValueKind.Object ||
            !element.TryGetProperty(name, out var property)) return false;
        if (property.ValueKind != JsonValueKind.String) return false;
        value = property.GetString() ?? string.Empty;
        return true;
    }

    private static bool TryNumber(JsonElement? args, string name, out double value)
    {
        value = 0;
        if (args is not { } element || element.ValueKind != JsonValueKind.Object ||
            !element.TryGetProperty(name, out var property)) return false;
        return property.ValueKind == JsonValueKind.Number && property.TryGetDouble(out value);
    }

    private static bool TryBool(JsonElement? args, string name, out bool value)
    {
        value = false;
        if (args is not { } element || element.ValueKind != JsonValueKind.Object ||
            !element.TryGetProperty(name, out var property)) return false;
        if (property.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) return false;
        value = property.GetBoolean();
        return true;
    }

    private static List<FanPoint>? ReadPoints(JsonElement? args)
    {
        if (args is not { } element || element.ValueKind != JsonValueKind.Object ||
            !element.TryGetProperty("points", out var points)) return null;
        if (points.ValueKind != JsonValueKind.Array) return null;
        var result = new List<FanPoint>();
        foreach (var point in points.EnumerateArray())
        {
            if (point.ValueKind != JsonValueKind.Array) return null;
            var values = point.EnumerateArray().ToArray();
            if (values.Length != 2) return null;
            result.Add(new FanPoint(values[0].GetDouble(), values[1].GetDouble()));
        }
        return result;
    }

    public override ValueTask DisposeAsync()
    {
        _disposed = true;
        lock (_gate)
        {
            _timer?.Dispose();
            _timer = null;
        }
        SetConnection(ServiceConnection.Disconnected);
        return ValueTask.CompletedTask;
    }
}
