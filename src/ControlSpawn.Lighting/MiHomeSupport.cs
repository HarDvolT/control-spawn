using ControlSpawn.Core.Ipc;

namespace ControlSpawn.Lighting;

/// <summary>What Control Spawn can drive among the devices of a Mi Home account, by model id.</summary>
public static class MiHomeSupport
{
    /// <summary>Lamp or Appliance for a model Control Spawn controls, null for one it can only list.</summary>
    public static DeviceKind? Kind(string model) =>
        model.StartsWith("yeelink.light.", StringComparison.Ordinal) ? DeviceKind.Lamp : MiotSpec.Load(model) is not null ? DeviceKind.Appliance : null;

    /// <summary>A name for the model a person recognises: "Yeelight bulb" rather than yeelink.light.color5.</summary>
    public static string Describe(string model) => model switch
    {
        _ when model.StartsWith("yeelink.light.", StringComparison.Ordinal) => "Yeelight bulb",
        _ when model.Contains(".fryer.", StringComparison.Ordinal) => (model.Split('.')[0] is { Length: > 0 } brand ? char.ToUpperInvariant(brand[0]) + brand[1..] + " " : "") + "air fryer",
        _ => model.Length > 0 ? model : "Mi Home device"
    };
}
