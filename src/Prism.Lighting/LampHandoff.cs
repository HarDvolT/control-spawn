using System.Globalization;
using System.Text.Json;

namespace Prism.Lighting;

/// <summary>What a bulb reports it is showing: get_prop power, bright, rgb, ct, color_mode.</summary>
public sealed record LampReading(bool On, int Brightness, int Mode, int Kelvin, int Rgb)
{
    public const int ColorMode = 1, WhiteMode = 2, HsvMode = 3;
    public bool IsWhite => Mode == WhiteMode;
    /// <summary>The colour to draw for it; a white shows as its colour temperature.</summary>
    public string Hex => IsWhite ? LampWhite.Hex(Kelvin) : $"#{Rgb & 0xFFFFFF:X6}";

    /// <summary>Bulbs answer with strings ("on", "100") or numbers depending on firmware; anything else is not a reading.</summary>
    public static LampReading? Parse(JsonElement result)
    {
        if (result.ValueKind != JsonValueKind.Array || result.GetArrayLength() < 5) return null;
        string? Text(int i) => result[i].ValueKind == JsonValueKind.String ? result[i].GetString() : result[i].ValueKind == JsonValueKind.Number ? result[i].GetRawText() : null;
        int? Number(int i) => int.TryParse(Text(i), NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : null;
        if (Text(0) is not ("on" or "off") || Number(1) is not { } bright || Number(2) is not { } rgb || Number(3) is not { } ct || Number(4) is not { } mode) return null;
        return new(Text(0) == "on", bright, mode, ct, rgb);
    }
}

/// <summary>
/// Notices a bulb set from somewhere else (Mi Home, a voice assistant, the wall switch) and lets it be. Starting the
/// service used to repaint a white chosen on the phone with the saved colour (live, 2026-11-03). The bulb is handed
/// off until the user changes it in Control Spawn again.
/// </summary>
public sealed class LampHandoff(LampReading? left = null)
{
    /// <summary>What Control Spawn last left the bulb showing; null before it has ever driven it.</summary>
    public LampReading? Left { get; private set; } = left;
    /// <summary>The latest reading, which is what the bulb really shows while handed off.</summary>
    public LampReading? Current { get; private set; }
    public bool HandedOff { get; private set; }

    /// <summary>Read right after Control Spawn wrote to the bulb: whatever it shows now is ours.</summary>
    public void Wrote(LampReading now) { Left = Current = now; HandedOff = false; }

    /// <summary>
    /// A routine read. <paramref name="moving"/> is true while Control Spawn streams colours (following the PC or a
    /// sensor), so the bulb's colour changing is expected. Returns true when this read hands the bulb off.
    /// </summary>
    public bool Observe(LampReading now, bool moving)
    {
        Current = now;
        if (HandedOff) return false;
        if (Left is not null && ChangedElsewhere(Left, now, moving)) return HandedOff = true;
        Left = now; return false;
    }

    public void TakeOver() => HandedOff = false;

    public static bool ChangedElsewhere(LampReading left, LampReading now, bool moving)
    {
        if (left.On != now.On) return true;
        if (!now.On) return false;   // an off bulb keeps its last colour; nothing to compare
        if (Math.Abs(left.Brightness - now.Brightness) > 1 || left.Mode != now.Mode) return true;
        return now.Mode switch
        {
            LampReading.WhiteMode => Math.Abs(left.Kelvin - now.Kelvin) > 50,
            LampReading.ColorMode => !moving && (left.Rgb & 0xFFFFFF) != (now.Rgb & 0xFFFFFF),
            _ => false   // HSV: rgb isn't reported, and Control Spawn never sets it, so the mode change already counted
        };
    }
}
