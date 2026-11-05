using ControlSpawn.Router;

namespace ControlSpawn.Router.Tests;

/// <summary>Small helpers mirroring the Python dict literals used by the reference tests.</summary>
internal static class D
{
    public static Dictionary<string, object?> Map(params (string Key, object? Value)[] items)
    {
        var map = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var (key, value) in items)
        {
            map[key] = value;
        }

        return map;
    }

    public static Dictionary<string, object?> Copy(Dictionary<string, object?> source)
    {
        return new Dictionary<string, object?>(source, StringComparer.Ordinal);
    }

    public static Dictionary<string, object?> With(
        this Dictionary<string, object?> source,
        params (string Key, object? Value)[] items)
    {
        var copy = Copy(source);
        foreach (var (key, value) in items)
        {
            copy[key] = value;
        }

        return copy;
    }

    /// <summary>The reference test's EXAMPLE signal reply.</summary>
    public static Dictionary<string, object?> Example() => Map(
        ("network_type_str", "5G(NSA)"),
        ("network_operator", "Ooredoo TN"),
        ("signal_lvl", "3"),
        ("RSRP", "-92"),
        ("RSRQ", "-14.0"),
        ("SINR", "0.0"),
        ("RSSI", "-77"),
        ("CQI", "9"),
        ("PCI", "272+272"),
        ("currentband", "1+3"),
        ("bandwidth", "20+20"),
        ("RSRP_5G", "-97"),
        ("RSRQ_5G", "-11"),
        ("SINR_5G", "8"),
        ("RSSI_5G", "-86"),
        ("CQI_5G", "7"),
        ("PCI_5G", "715"),
        ("currentband_5g", "78"),
        ("bandwidth_5g", "100"));

    public static Dictionary<string, object?> ExampleWithSuccess() =>
        Example().With(("success", true));
}
