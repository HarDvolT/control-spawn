using System.Collections.Concurrent;
using System.Globalization;
using System.Windows.Media;

namespace Prism.App.Common;

public static class ColorUtil
{
    private static readonly ConcurrentDictionary<uint, SolidColorBrush> BrushCache = new();

    public static readonly Color Accent = Color.FromRgb(0xFF, 0x4D, 0x1A);
    public static readonly Color Good = Color.FromRgb(0x3D, 0xDC, 0x84);
    public static readonly Color Fair = Color.FromRgb(0xF5, 0xA5, 0x24);
    public static readonly Color Poor = Color.FromRgb(0xFF, 0x4D, 0x4F);
    public static readonly Color Excellent = Color.FromRgb(0x36, 0xD6, 0xFF);

    /// <summary>Signal verdict colour: excellent cyan, good green, fair amber, poor red, anything else muted.</summary>
    public static Color ForVerdict(string? verdict) => verdict switch
    {
        "Excellent" => Excellent, "Good" => Good, "Fair" => Fair, "Poor" or "Bad" => Poor, _ => TextTertiary
    };
    public static readonly Color TextPrimary = Color.FromRgb(0xF4, 0xF4, 0xF5);
    public static readonly Color TextSecondary = Color.FromRgb(0xA1, 0xA1, 0xAA);
    public static readonly Color TextTertiary = Color.FromRgb(0x8A, 0x8A, 0x93);
    public static readonly Color Track = Color.FromRgb(0x23, 0x23, 0x27);

    public static Color Parse(string? hex, Color fallback)
    {
        if (string.IsNullOrWhiteSpace(hex)) return fallback;
        var text = hex.Trim();
        if (text.StartsWith('#')) text = text[1..];
        if (text.Length == 3)
            text = string.Concat(text[0], text[0], text[1], text[1], text[2], text[2]);
        if (text.Length != 6 && text.Length != 8) return fallback;
        if (!uint.TryParse(text, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var packed))
            return fallback;
        return text.Length == 6
            ? Color.FromRgb((byte)(packed >> 16), (byte)(packed >> 8), (byte)packed)
            : Color.FromArgb((byte)(packed >> 24), (byte)(packed >> 16), (byte)(packed >> 8), (byte)packed);
    }

    public static string ToHex(Color color) => $"#{color.R:X2}{color.G:X2}{color.B:X2}";

    public static Color Mix(Color a, Color b, double t)
    {
        t = double.IsFinite(t) ? Math.Clamp(t, 0, 1) : 0;
        return Color.FromRgb(
            (byte)Math.Round(a.R + (b.R - a.R) * t),
            (byte)Math.Round(a.G + (b.G - a.G) * t),
            (byte)Math.Round(a.B + (b.B - a.B) * t));
    }

    public static Color WithAlpha(Color color, double alpha)
    {
        alpha = double.IsFinite(alpha) ? Math.Clamp(alpha, 0, 1) : 1;
        return Color.FromArgb((byte)Math.Round(alpha * 255), color.R, color.G, color.B);
    }

    /// <summary>Green below 55 °C, amber below 75 °C, red above (same thresholds as the mockups).</summary>
    public static Color Heat(double? temp) =>
        temp is not { } t || !double.IsFinite(t) ? TextTertiary : t < 55 ? Good : t < 75 ? Fair : Poor;

    /// <summary>0..1 temperature ramp used by the Smart lighting effect.</summary>
    public static double HeatAmount(double? temp) =>
        temp is not { } t || !double.IsFinite(t) ? 0 : Math.Clamp((t - 40) / 30, 0, 1);

    public static Color Hsl(double hue, double saturation, double lightness)
    {
        hue = ((hue % 360) + 360) % 360;
        saturation = Math.Clamp(saturation, 0, 1);
        lightness = Math.Clamp(lightness, 0, 1);
        var c = (1 - Math.Abs(2 * lightness - 1)) * saturation;
        var x = c * (1 - Math.Abs(hue / 60 % 2 - 1));
        var m = lightness - c / 2;
        var (r, g, b) = hue switch
        {
            < 60 => (c, x, 0.0),
            < 120 => (x, c, 0.0),
            < 180 => (0.0, c, x),
            < 240 => (0.0, x, c),
            < 300 => (x, 0.0, c),
            _ => (c, 0.0, x)
        };
        return Color.FromRgb((byte)Math.Round((r + m) * 255), (byte)Math.Round((g + m) * 255), (byte)Math.Round((b + m) * 255));
    }

    public static SolidColorBrush Brush(Color color)
    {
        var packed = ((uint)color.A << 24) | ((uint)color.R << 16) | ((uint)color.G << 8) | color.B;
        return BrushCache.GetOrAdd(packed, _ =>
        {
            var brush = new SolidColorBrush(color);
            brush.Freeze();
            return brush;
        });
    }

    public static SolidColorBrush Brush(string? hex, Color fallback) => Brush(Parse(hex, fallback));
}
