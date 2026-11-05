using ControlSpawn.Core;

namespace ControlSpawn.Lighting;

/// <summary>
/// Colour bulbs mix an RGB white from their red, green and blue LEDs, which gives a fraction of the light of their own
/// white LEDs. Mi Home picks white mode (colour temperature) for whites, so the same "warm white" set from Control Spawn looked
/// much dimmer than from the phone (live, 2026-11-03). A near-white colour is therefore sent as a colour temperature.
/// </summary>
public static class LampWhite
{
    /// <summary>Most saturation still treated as white: #FFE6C7 (a warm white) is 0.22; a pale pink like #FFC0CB is 0.25.</summary>
    public const double MaxSaturation = 0.24;

    // Black-body white points (Tanner Helland's fit), warmest to coolest, within the bulbs' 1700-6500 K range.
    private static readonly (int Kelvin, double R, double G, double B)[] WhitePoints =
    [
        (1700, 255, 121, 0), (2000, 255, 137, 14), (2500, 255, 159, 70), (2700, 255, 167, 87), (3000, 255, 177, 110),
        (3500, 255, 196, 137), (4000, 255, 209, 163), (4500, 255, 219, 186), (5000, 255, 228, 206), (5500, 255, 236, 224),
        (6000, 255, 243, 239), (6500, 255, 249, 253),
    ];

    /// <summary>The colour temperature that looks closest to <paramref name="color"/>, or null when it isn't a white.</summary>
    public static int? Kelvin(RgbColor color)
    {
        int max = Math.Max(color.R, Math.Max(color.G, color.B)), min = Math.Min(color.R, Math.Min(color.G, color.B));
        // Dark colours aren't whites to light a room with, and a strong tint must stay a colour.
        if (max < 128 || (max - min) / (double)max > MaxSaturation) return null;
        // Compare hue only: scale so the brightest channel is 255, as the white points are.
        double scale = 255.0 / max, r = color.R * scale, g = color.G * scale, b = color.B * scale;
        return WhitePoints.MinBy(p => Math.Pow(p.R - r, 2) + Math.Pow(p.G - g, 2) + Math.Pow(p.B - b, 2)).Kelvin;
    }

    /// <summary>The colour a colour temperature looks like on screen, blended between the white points.</summary>
    public static string Hex(int kelvin)
    {
        kelvin = Math.Clamp(kelvin, WhitePoints[0].Kelvin, WhitePoints[^1].Kelvin);
        int upper = Array.FindIndex(WhitePoints, p => p.Kelvin >= kelvin), lower = Math.Max(0, upper - 1);
        var (a, b) = (WhitePoints[lower], WhitePoints[upper]);
        double t = b.Kelvin == a.Kelvin ? 0 : (kelvin - a.Kelvin) / (double)(b.Kelvin - a.Kelvin);
        int Mix(double x, double y) => (int)Math.Round(x + (y - x) * t);
        return $"#{Mix(a.R, b.R):X2}{Mix(a.G, b.G):X2}{Mix(a.B, b.B):X2}";
    }
}
