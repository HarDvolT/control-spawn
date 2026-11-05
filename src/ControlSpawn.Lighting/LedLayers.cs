using System.Buffers.Binary;
using System.Globalization;
using ControlSpawn.Core;
using ControlSpawn.Core.Ipc;

namespace ControlSpawn.Lighting;

public static class LedLayers
{
    public static bool Valid(string? color, bool alpha) => color is not null && color.Length == (alpha ? 9 : 7) &&
        color[0] == '#' && uint.TryParse(color.AsSpan(1), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out _);

    public static string[] Resize(IReadOnlyList<string>? colors, int count, bool alpha) =>
        Enumerable.Range(0, count).Select(i => colors is not null && i < colors.Count ? colors[i] : alpha ? "#00000000" : "#000000").ToArray();

    public static IReadOnlyList<RgbColor> Composite(IReadOnlyList<RgbColor> effect, IReadOnlyList<string>? custom,
        IReadOnlyList<string>? overlay, bool exact)
    {
        var result = new RgbColor[effect.Count];
        for (int i = 0; i < result.Length; i++)
        {
            var c = exact ? custom is not null && i < custom.Count ? Parse(custom[i]) : RgbColor.Black : effect[i];
            if (overlay is not null && i < overlay.Count)
                c = RgbColor.Lerp(c, Parse(overlay[i]), byte.Parse(overlay[i].AsSpan(7, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture) / 255d);
            result[i] = c;
        }
        return result;
    }

    private static RgbColor Parse(string text)
    {
        var n = uint.Parse(text.AsSpan(1, 6), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        return new((byte)(n >> 16), (byte)(n >> 8), (byte)n);
    }

    public static RgbDeviceState Project(string id, OpenRgbController controller)
    {
        int first = 0;
        var zones = controller.Zones.Select(z =>
        {
            int start = first; first += checked((int)z.Count);
            int width = 0, height = 0; int[]? matrix = null;
            if (z.Matrix.Length >= 8)
            {
                height = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(z.Matrix));
                width = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(z.Matrix.AsSpan(4)));
                matrix = Enumerable.Range(0, width * height).Select(i =>
                {
                    uint index = BinaryPrimitives.ReadUInt32LittleEndian(z.Matrix.AsSpan(8 + i * 4));
                    return index == uint.MaxValue || index >= z.Count ? -1 : start + (int)index;
                }).ToArray();
            }
            return new RgbZoneState(z.Name, z.Type switch { 0 => "Single", 1 => "Linear", 2 => "Matrix", _ => "Unknown" },
                start, (int)z.Count, (int)z.Minimum, (int)z.Maximum,
                controller.Leds.Skip(start).Take((int)z.Count).Select(l => l.Name).ToArray(), width, height, matrix);
        }).ToArray();
        return new(id, controller.Name, OpenRgbClient.KindName(controller.Type), true, true, controller.Colors.Count, zones);
    }
}
