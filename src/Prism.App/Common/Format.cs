using System.Globalization;

namespace Prism.App.Common;

/// <summary>
/// One place for every number the UI shows. A missing reading is always "—", never 0.
/// Formatting is invariant so tests and screenshots are stable.
/// </summary>
public static class Format
{
    public const string Dash = "—";
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    public static string Number(double? value, string format = "0") =>
        value is { } v && double.IsFinite(v) ? v.ToString(format, Inv) : Dash;

    public static string Percent(double? value, int decimals = 0) =>
        value is { } v && double.IsFinite(v) ? v.ToString("F" + decimals, Inv) + "%" : Dash;

    public static string Rpm(double? value) =>
        value is { } v && double.IsFinite(v) ? v.ToString("N0", Inv) + " rpm" : Dash;

    public static string RpmShort(double? value) =>
        value is { } v && double.IsFinite(v) ? v.ToString("N0", Inv) : Dash;

    public static string Temp(double? value, int decimals = 1) =>
        value is { } v && double.IsFinite(v) ? v.ToString("F" + decimals, Inv) + "°" : Dash;

    public static string TempUnit(double? value, int decimals = 1) =>
        value is { } v && double.IsFinite(v) ? v.ToString("F" + decimals, Inv) + " °C" : Dash;

    public static string Gb(double? value, int decimals = 1) =>
        value is { } v && double.IsFinite(v) ? v.ToString("F" + decimals, Inv) + " GB" : Dash;

    public static string GbPair(double? used, double? total, int decimals = 1) =>
        used is { } u && total is { } t && double.IsFinite(u) && double.IsFinite(t)
            ? $"{u.ToString("F" + decimals, Inv)} / {t.ToString("F0", Inv)} GB"
            : Dash;

    public static string Mbps(double? value, int decimals = 0) =>
        value is { } v && double.IsFinite(v) ? v.ToString("F" + decimals, Inv) + " Mb/s" : Dash;

    public static string Mbit(double? value) =>
        value is { } v && double.IsFinite(v) ? v.ToString("F0", Inv) + " Mbit/s" : Dash;

    public static string Watts(double? value) =>
        value is { } v && double.IsFinite(v) ? v.ToString("F0", Inv) + " W" : Dash;

    public static string Mhz(double? value) =>
        value is { } v && double.IsFinite(v) ? v.ToString("F0", Inv) + " MHz" : Dash;

    public static string Ghz(double? mhz) =>
        mhz is { } v && double.IsFinite(v) ? (v / 1000.0).ToString("F2", Inv) + " GHz" : Dash;

    public static string Db(double? value, int decimals = 1) =>
        value is { } v && double.IsFinite(v) ? v.ToString("F" + decimals, Inv) + " dB" : Dash;

    public static string Dbm(double? value, int decimals = 0) =>
        value is { } v && double.IsFinite(v) ? v.ToString("F" + decimals, Inv) + " dBm" : Dash;

    public static string Ms(double? value, int decimals = 0) =>
        value is { } v && double.IsFinite(v) ? v.ToString("F" + decimals, Inv) + " ms" : Dash;

    public static string MsValue(double? value, int decimals = 0) =>
        value is { } v && double.IsFinite(v) ? v.ToString("F" + decimals, Inv) : Dash;

    public static string Text(string? value) =>
        string.IsNullOrWhiteSpace(value) ? Dash : value;

    public static string Bytes(long? value)
    {
        if (value is not { } v || v < 0) return Dash;
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double size = v;
        var unit = 0;
        while (size >= 1024 && unit < units.Length - 1) { size /= 1024; unit++; }
        return size.ToString(unit == 0 ? "F0" : "F1", Inv) + " " + units[unit];
    }

    /// <summary>Clamps a possibly-null reading into a 0..100 bar value.</summary>
    public static double Bar(double? value, double max = 100)
    {
        if (value is not { } v || !double.IsFinite(v) || max <= 0) return 0;
        return Math.Clamp(v / max * 100, 0, 100);
    }

    public static string FanModeName(Core.FanMode mode) => mode switch
    {
        Core.FanMode.Silent => "Silent",
        Core.FanMode.Standard => "Standard",
        Core.FanMode.Turbo => "Turbo",
        Core.FanMode.Full => "Full speed",
        Core.FanMode.Custom => "Custom",
        _ => mode.ToString()
    };

    public static string EffectName(Core.LightingEffect effect) => effect switch
    {
        Core.LightingEffect.Static => "Static",
        Core.LightingEffect.Breathing => "Breathing",
        Core.LightingEffect.Strobing => "Strobing",
        Core.LightingEffect.ColorCycle => "Color cycle",
        Core.LightingEffect.RainbowWave => "Rainbow",
        Core.LightingEffect.StarryNight => "Starry night",
        Core.LightingEffect.Smart => "Smart",
        Core.LightingEffect.Music => "Music",
        Core.LightingEffect.Adaptive => "Adaptive",
        Core.LightingEffect.Custom => "Custom",
        Core.LightingEffect.Off => "Off",
        _ => effect.ToString()
    };

    public static string EffectSubtitle(Core.LightingEffect effect) => effect switch
    {
        Core.LightingEffect.Static => "One colour",
        Core.LightingEffect.Breathing => "Fade in/out",
        Core.LightingEffect.Strobing => "Flash",
        Core.LightingEffect.ColorCycle => "All colours",
        Core.LightingEffect.RainbowWave => "Moving wave",
        Core.LightingEffect.StarryNight => "Twinkles",
        Core.LightingEffect.Music => "Follows audio",
        Core.LightingEffect.Smart => "CPU temperature",
        Core.LightingEffect.Adaptive => "Screen colours",
        Core.LightingEffect.Custom => "Per-LED colours",
        Core.LightingEffect.Off => "Lights off",
        _ => string.Empty
    };
}
