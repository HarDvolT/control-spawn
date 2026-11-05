using System.Text.Json;

namespace ControlSpawn.Core.Ipc;

public enum DeviceKind { Lamp, RgbController, FanHeader, Router, NetworkDevice, Appliance }
public enum DeviceStatus { Ready, NeedsToken, Unsupported, Offline }
public sealed record DiscoveryOptions(bool AutoJoinNewRgb = true, bool NotifyNewDevices = true, bool CookingLightFlash = false);
public sealed record DeviceState(string Id, DeviceKind Kind, string Name, string Source,
    DateTimeOffset FirstSeen, DateTimeOffset LastSeen, DeviceStatus Status, IReadOnlyList<string> Capabilities,
    string? DetectedName = null, string? Model = null, string? Firmware = null,
    uint? DeviceId = null, string? Ip = null, string? Mac = null, string? Vendor = null,
    bool Dismissed = false, bool Configured = false, string? UserName = null,
    IReadOnlyList<string>? Sources = null, DeviceStatus? OnlineStatus = null);
public sealed record ApplianceProperty(int Siid, int Piid, string Name, string Format, JsonElement? Value,
    bool Writable = false, double[]? Range = null, IReadOnlyDictionary<int, string>? Values = null, string? Unit = null);
public sealed record ApplianceActionState(int Siid, int Aiid, string Name, bool NeedsConfirmation);
public sealed record ApplianceState(string Id, string Name, string Model, bool Connected, string Status,
    IReadOnlyList<ApplianceProperty> Properties, IReadOnlyList<ApplianceActionState> Actions,
    double? Temperature = null, double? TargetTemperature = null, double? TargetMinutes = null,
    double? RemainingMinutes = null, bool TurnBasket = false, string? Warning = null, IReadOnlyList<CookRecord>? History = null);
public sealed record CookRecord(DateTimeOffset ObservedSince, DateTimeOffset EndedAt,
    double? TargetTemperature, double? TargetMinutes, string Outcome);
