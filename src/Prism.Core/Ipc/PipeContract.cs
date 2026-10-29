using System.Text.Json;
using System.Text.Json.Serialization;

namespace Prism.Core.Ipc;

/// <summary>
/// Wire contract between Prism.Service (elevated, owns all hardware) and Prism.App / desktop widgets.
/// Transport: named pipe <see cref="PipeName"/>, UTF-8 JSON, one message per line ("\n"-delimited).
/// Client sends <see cref="Request"/>; service answers each with a <see cref="Response"/> carrying the same Id,
/// and pushes a <see cref="StateMessage"/> (Type = "state") about once per second to every subscribed client.
/// </summary>
public static class PipeContract
{
    public const string PipeName = "prism";
    public const int ProtocolVersion = 1;

    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };
}

/// <summary>Commands a client may send. Anything else is answered with Ok = false.</summary>
public static class Commands
{
    public const string FindDevices = "findDevices";
    public const string DismissDevice = "dismissDevice";
    public const string SetupDevice = "setupDevice";
    public const string RenameDevice = "renameDevice";
    public const string ImportTokens = "importTokens";
    public const string SetDiscoveryOptions = "setDiscoveryOptions";
    public const string ApplianceProperties = "applianceProperties";
    public const string PrepareApplianceStart = "prepareApplianceStart";
    public const string ConfirmApplianceStart = "confirmApplianceStart";
    public const string ApplianceAction = "applianceAction";
    public const string Hello = "hello";                 // args: { "version": 1, "client": "app|widget" }
    public const string Subscribe = "subscribe";         // start receiving state pushes
    public const string GetState = "getState";           // one StateMessage in Response.Data
    public const string SetFanMode = "setFanMode";       // args: { "group": "Radiator|Case fans", "mode": "Silent|Standard|Turbo|Full|Custom" }
    public const string SetFanCurve = "setFanCurve";     // args: { "group": "...", "points": [[t,%] x8], "source": "Cpu|Motherboard|Gpu|Maximum" }
    public const string SetSensorBinding = "setSensorBinding";
    public const string SetLighting = "setLighting";     // args: { "effect": "Static|...", "color": "#RRGGBB", "brightness": 0..1, "speed": 0..2 }
    public const string SetDeviceSync = "setDeviceSync"; // args: { "device": "<id>", "enabled": true }
    public const string SetLamp = "setLamp";             // args: { "lamp"?: id (default first), "on"?: true, "brightness"?: 1..100, "follow"?: "pc|average|own|map", "color"?: "#RRGGBB", "name"?: string }
    public const string ApplyScene = "applyScene";       // args: { "name": "Gaming" }
    public const string SetEffectParams = "setEffectParams"; // { parameters: EffectParameters }
    public const string SetLightingMap = "setLightingMap"; // { map: LightingMap }
    public const string SetEventEffects = "setEventEffects"; // { events: EventEffectOptions }
    public const string NotifyLighting = "notifyLighting"; // { kind: "Notification" }
    public const string SaveLightingScene = "saveLightingScene"; // { name: existing scene name }
    public const string StartSpeedTest = "startSpeedTest";

    // --- Added 2026-10-29 (protocol 1, additive) ---
    public const string SetLedColors = "setLedColors";   // args: { "device": "<id>", "colors": ["#RRGGBB", ... one per LED], "layer": "custom|overlay" }
                                                         //   custom  = the device shows exactly these colours (effect "Custom")
                                                         //   overlay = colours with alpha "#RRGGBBAA" painted over the running effect; "#00000000" = untouched
    public const string ClearLedColors = "clearLedColors"; // args: { "device": "<id>" }  removes custom/overlay colours for that device
    public const string IdentifyZone = "identifyZone";
    public const string SetAllLighting = "setAllLighting";
    public const string ResizeZone = "resizeZone";       // args: { "device": "<id>", "zone": 0, "size": 30 }  ARGB headers start at 0 LEDs
    public const string DiscoverLamps = "discoverLamps"; // returns [{ ip, deviceId, alreadyConfigured }]
    public const string RemoveLamp = "removeLamp"; // { lamp: id }
    public const string SetLampToken = "setLampToken";   // args: { "deviceId"?: decimal uint or "0x...", "name"?: string, "host"?: string, "token": "<32 hex>" }  stored DPAPI-encrypted by the service, never echoed back
    public const string SetRouterPassword = "setRouterPassword"; // args: { "password": "..." }  stored DPAPI-encrypted, never echoed back
    public const string SetServiceOptions = "setServiceOptions"; // args: { "openRgbPath": "...", "launchOpenRgb": true, "routerEnabled": true, "routerHost": "192.168.0.1" }
}

public sealed record Request(int Id, string Command, JsonElement? Args = null);
public sealed record Response(int Id, bool Ok, string? Error = null, JsonElement? Data = null, string Type = "response");

/// <summary>Everything the UI and widgets display. Missing readings are null, never 0.</summary>
public sealed record StateMessage(
    DateTimeOffset Timestamp,
    SystemState System,
    IReadOnlyList<FanState> Fans,
    FanMode FanMode,
    LightingState Lighting,
    LampState? Lamp,
    RouterState? Router,
    ServiceHealth Health,
    string Type = "state", IReadOnlyList<LampState>? Lamps = null,
    IReadOnlyList<DeviceState>? Devices = null, IReadOnlyList<DeviceState>? NewDevices = null,
    IReadOnlyList<ApplianceState>? Appliances = null, DiscoveryOptions? Discovery = null);

public sealed record SystemState(
    double? CpuLoad, IReadOnlyList<double?> CpuCoreLoads, double? CpuClockMhz, double? CpuTemp, double? CpuPowerW,
    double? GpuLoad, double? GpuTemp, double? GpuHotspot, double? GpuClockMhz, double? GpuPowerW, double? GpuFanRpm,
    double? VramUsedGb, double? VramTotalGb,
    double? RamUsedGb, double? RamTotalGb, IReadOnlyList<double?> DimmTemps,
    double? BoardTemp,
    IReadOnlyList<DiskState> Disks,
    double? NetDownMbps, double? NetUpMbps, double? GpuMemoryJunction = null);

public sealed record DiskState(string Name, double? TempC, double? ReadMBps, double? WriteMBps, double? UsedPercent, double? ActivityPercent = null);
public sealed record FanState(int Header, string Name, double? Rpm, double? Percent, bool Controllable, bool IsPump, string Curve);
public sealed record LightingState(LightingEffect Effect, string Color, double Brightness, double Speed, IReadOnlyList<RgbDeviceState> Devices,
    EffectParameters? Parameters = null, LightingMap? Map = null, EventEffectOptions? Events = null, ReactiveInputState? Inputs = null, IReadOnlyDictionary<string, SensorBinding>? Bindings = null, IReadOnlyDictionary<string, SensorBindingReading>? BindingReadings = null, IReadOnlyList<ZoneIdentification>? IdentifyingZones = null);
public sealed record ZoneIdentification(string Device, int Zone, int Size, int LedIndex);
public sealed record RgbDeviceState(string Id, string Name, string Kind, bool Connected, bool Synced, int LedCount,
    IReadOnlyList<RgbZoneState>? Zones = null,
    IReadOnlyList<string>? CustomColors = null,     // "#RRGGBB" per LED when the device is in Custom, else null
    IReadOnlyList<string>? OverlayColors = null, bool CustomEnabled = false);   // "#RRGGBBAA" per LED painted over the effect, else null

/// <summary>One OpenRGB zone. LED indexes are device-wide: this zone covers [FirstLed, FirstLed + LedCount).</summary>
public sealed record RgbZoneState(string Name, string Type, int FirstLed, int LedCount, int MinLeds, int MaxLeds,
    IReadOnlyList<string>? LedNames = null,
    int MatrixWidth = 0, int MatrixHeight = 0, IReadOnlyList<int>? Matrix = null); // Matrix: LED index per cell, -1 = empty
public sealed record LampState(string Model, bool Connected, bool On, int Brightness, string Color, string Follow, string Transport, string? Id = null, string? Name = null, uint? DeviceId = null, string? Host = null, string? Error = null);
public sealed record RouterState(
    bool Connected, string? Network, string? Band, int? Score, int? Bars, string? Verdict, string? Sentence,
    double? Rsrp5g, double? Sinr5g, double? Rsrq5g, double? Rsrp4g, double? Sinr4g, double? Rsrq4g,
    double? PingMs, double? PingLossPercent, string? Error);

/// <summary>Conflicts and warnings the UI should surface (e.g. "AsusFanControlService is running").</summary>
public sealed record ServiceHealth(bool Mock, bool Elevated, IReadOnlyList<string> Warnings, bool FanFailsafeActive);

// Additive protocol-1 spatial lighting types. Coordinates use top-left origin.
public sealed record MapPoint(double X, double Y);
public sealed record DevicePlacement(string Device, double X, double Y, double Length = 0.4,
    double Rotation = 90, bool Flip = false, int? Zone = null);
public sealed record LightingMap(IReadOnlyList<DevicePlacement> Devices, MapPoint? Lamp = null, IReadOnlyDictionary<string, MapPoint>? Lamps = null);
public sealed record PaletteStop(double Position, string Color);
public sealed record EffectParameters
{
    public string Palette { get; init; } = "Aurora";
    public IReadOnlyList<PaletteStop>? Stops { get; init; }
    public double Angle { get; init; } = 90;
    public double Scale { get; init; } = 1;
    public double Intensity { get; init; } = 1;
    public int CometCount { get; init; } = 1;
    public double TailLength { get; init; } = 0.25;
    public bool HeartbeatCpu { get; init; }
    public IReadOnlyDictionary<string, bool>? Devices { get; init; }
}
public enum LightingEventKind { PingSpike, PoorSignal, FanFailsafe, SceneChange, Notification }
public sealed record EventEffectOptions(bool PingSpike = true, bool PoorSignal = true,
    bool FanFailsafe = true, bool SceneChange = true, bool Notification = true)
{
    public bool Enabled(LightingEventKind kind) => kind switch {
        LightingEventKind.PingSpike => PingSpike, LightingEventKind.PoorSignal => PoorSignal,
        LightingEventKind.FanFailsafe => FanFailsafe, LightingEventKind.SceneChange => SceneChange,
        _ => Notification };
}

// Optional capture telemetry for the app preview; never contains captured pixels or audio samples.
public sealed record ReactiveInputState(IReadOnlyList<double> AudioBands, double Beat,
    string ScreenLeft, string ScreenCentre, string ScreenRight);

public enum SensorStyle { ColourGradient, FillBar, BreathingSpeed, Brightness, Threshold }
public sealed record SensorAlert(double Threshold);
public sealed record SensorBinding(string Source, double Min, double Max,
    SensorStyle Style = SensorStyle.ColourGradient, IReadOnlyList<PaletteStop>? Palette = null,
    double Smoothing = 0.5, SensorAlert? Alert = null, bool AutoLearn = false, double Hysteresis = 2);

public sealed record SensorBindingReading(double? Value, double Min, double Max, double Normalized, bool Alert, string? Reason);
