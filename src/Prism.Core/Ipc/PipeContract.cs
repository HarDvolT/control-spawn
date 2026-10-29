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
    public const string Hello = "hello";                 // args: { "version": 1, "client": "app|widget" }
    public const string Subscribe = "subscribe";         // start receiving state pushes
    public const string GetState = "getState";           // one StateMessage in Response.Data
    public const string SetFanMode = "setFanMode";       // args: { "group": "Radiator|Case fans", "mode": "Silent|Standard|Turbo|Full|Custom" }
    public const string SetFanCurve = "setFanCurve";     // args: { "group": "...", "points": [[t,%] x8], "source": "Cpu|Motherboard|Gpu|Maximum" }
    public const string SetLighting = "setLighting";     // args: { "effect": "Static|...", "color": "#RRGGBB", "brightness": 0..1, "speed": 0..2 }
    public const string SetDeviceSync = "setDeviceSync"; // args: { "device": "<id>", "enabled": true }
    public const string SetLamp = "setLamp";             // args: { "on": true, "brightness": 1..100, "follow": "pc|average|own", "color": "#RRGGBB" }
    public const string ApplyScene = "applyScene";       // args: { "name": "Gaming" }
    public const string StartSpeedTest = "startSpeedTest";
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
    string Type = "state");

public sealed record SystemState(
    double? CpuLoad, IReadOnlyList<double?> CpuCoreLoads, double? CpuClockMhz, double? CpuTemp, double? CpuPowerW,
    double? GpuLoad, double? GpuTemp, double? GpuHotspot, double? GpuClockMhz, double? GpuPowerW, double? GpuFanRpm,
    double? VramUsedGb, double? VramTotalGb,
    double? RamUsedGb, double? RamTotalGb, IReadOnlyList<double?> DimmTemps,
    double? BoardTemp,
    IReadOnlyList<DiskState> Disks,
    double? NetDownMbps, double? NetUpMbps);

public sealed record DiskState(string Name, double? TempC, double? ReadMBps, double? WriteMBps, double? UsedPercent);
public sealed record FanState(int Header, string Name, double? Rpm, double? Percent, bool Controllable, bool IsPump, string Curve);
public sealed record LightingState(LightingEffect Effect, string Color, double Brightness, double Speed, IReadOnlyList<RgbDeviceState> Devices);
public sealed record RgbDeviceState(string Id, string Name, string Kind, bool Connected, bool Synced, int LedCount);
public sealed record LampState(string Model, bool Connected, bool On, int Brightness, string Color, string Follow, string Transport);
public sealed record RouterState(
    bool Connected, string? Network, string? Band, int? Score, int? Bars, string? Verdict, string? Sentence,
    double? Rsrp5g, double? Sinr5g, double? Rsrq5g, double? Rsrp4g, double? Sinr4g, double? Rsrq4g,
    double? PingMs, double? PingLossPercent, string? Error);

/// <summary>Conflicts and warnings the UI should surface (e.g. "AsusFanControlService is running").</summary>
public sealed record ServiceHealth(bool Mock, bool Elevated, IReadOnlyList<string> Warnings, bool FanFailsafeActive);
