using System.Buffers.Binary;
using System.Text;
using ControlSpawn.Core;

namespace ControlSpawn.Lighting;

public sealed record OpenRgbPacket(uint Device, uint Id, byte[] Data);
public sealed record OpenRgbMode(string Name, int Value, uint Flags, uint SpeedMin, uint SpeedMax,
    uint BrightnessMin, uint BrightnessMax, uint ColorsMin, uint ColorsMax, uint Speed,
    uint Brightness, uint Direction, uint ColorMode, IReadOnlyList<RgbColor> Colors);
public sealed record OpenRgbZone(string Name, int Type, uint Minimum, uint Maximum, uint Count, byte[] Matrix);
public sealed record OpenRgbLed(string Name, uint Value);
public sealed record OpenRgbController(int Type, string Name, string Vendor, string Description, string Version,
    string Serial, string Location, int ActiveMode, IReadOnlyList<OpenRgbMode> Modes,
    IReadOnlyList<OpenRgbZone> Zones, IReadOnlyList<OpenRgbLed> Leds, IReadOnlyList<RgbColor> Colors);

/// <summary>Little-endian SDK protocol 0..3. Negotiate at most 3 even with newer servers.</summary>
public static class OpenRgbProtocol
{
    public const uint MaximumVersion = 3;
    public const int MaximumPayload = 16 * 1024 * 1024;
    public static byte[] UInt32(uint value)
    {
        var data = new byte[4]; BinaryPrimitives.WriteUInt32LittleEndian(data, value); return data;
    }
    public static byte[] Encode(OpenRgbPacket packet)
    {
        if (packet.Data.Length > MaximumPayload) throw new InvalidDataException("Payload too large.");
        var result = new byte[16 + packet.Data.Length];
        "ORGB"u8.CopyTo(result);
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(4), packet.Device);
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(8), packet.Id);
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(12), (uint)packet.Data.Length);
        packet.Data.CopyTo(result, 16); return result;
    }
    public static (uint Device, uint Id, int Size) DecodeHeader(ReadOnlySpan<byte> header)
    {
        if (header.Length != 16 || !header[..4].SequenceEqual("ORGB"u8)) throw new InvalidDataException("Invalid ORGB header.");
        uint size = BinaryPrimitives.ReadUInt32LittleEndian(header[12..]);
        if (size > MaximumPayload) throw new InvalidDataException("Payload too large.");
        return (BinaryPrimitives.ReadUInt32LittleEndian(header[4..]), BinaryPrimitives.ReadUInt32LittleEndian(header[8..]), (int)size);
    }
    public static OpenRgbPacket Decode(byte[] bytes)
    {
        if (bytes.Length < 16) throw new InvalidDataException("Truncated packet.");
        var h = DecodeHeader(bytes.AsSpan(0, 16));
        if (bytes.Length != h.Size + 16) throw new InvalidDataException("Packet length mismatch.");
        return new(h.Device, h.Id, bytes[16..]);
    }
    public static byte[] UpdateLeds(IReadOnlyList<RgbColor> colors)
    {
        if (colors.Count > ushort.MaxValue) throw new ArgumentOutOfRangeException(nameof(colors));
        using var ms = new MemoryStream(); using var w = new BinaryWriter(ms);
        w.Write((uint)(6 + 4 * colors.Count)); w.Write((ushort)colors.Count);
        foreach (var color in colors) WriteColor(w, color);
        return ms.ToArray();
    }
    /// <summary>RGBCONTROLLER_UPDATEZONELEDS (1051): data size, zone index, colour count, colours.</summary>
    public static byte[] UpdateZoneLeds(int zone, IReadOnlyList<RgbColor> colors)
    {
        if (colors.Count > ushort.MaxValue || zone < 0) throw new ArgumentOutOfRangeException(nameof(colors));
        using var ms = new MemoryStream(); using var w = new BinaryWriter(ms);
        w.Write((uint)(10 + 4 * colors.Count)); w.Write((uint)zone); w.Write((ushort)colors.Count);
        foreach (var color in colors) WriteColor(w, color);
        return ms.ToArray();
    }
    public static byte[] UpdateMode(int index, OpenRgbMode mode, uint version)
    {
        ValidateVersion(version);
        using var ms = new MemoryStream(); using var w = new BinaryWriter(ms);
        w.Write(0u); w.Write(index); WriteString(w, mode.Name); w.Write(mode.Value); w.Write(mode.Flags);
        w.Write(mode.SpeedMin); w.Write(mode.SpeedMax);
        if (version >= 3) { w.Write(mode.BrightnessMin); w.Write(mode.BrightnessMax); }
        w.Write(mode.ColorsMin); w.Write(mode.ColorsMax); w.Write(mode.Speed);
        if (version >= 3) w.Write(mode.Brightness);
        w.Write(mode.Direction); w.Write(mode.ColorMode); w.Write(checked((ushort)mode.Colors.Count));
        foreach (var c in mode.Colors) WriteColor(w, c);
        long size = ms.Length; ms.Position = 0; w.Write((uint)size); return ms.ToArray();
    }
    public static OpenRgbController ParseController(byte[] data, uint version)
    {
        ValidateVersion(version);
        using var ms = new MemoryStream(data, false); using var r = new BinaryReader(ms, Encoding.UTF8);
        try
        {
            if (r.ReadUInt32() != data.Length) throw new InvalidDataException("Controller size mismatch.");
            int type = r.ReadInt32(); string name = ReadString(r), vendor = version >= 1 ? ReadString(r) : "";
            string description = ReadString(r), firmware = ReadString(r), serial = ReadString(r), location = ReadString(r);
            int modesCount = r.ReadUInt16(), active = r.ReadInt32();
            var modes = new List<OpenRgbMode>();
            for (int i = 0; i < modesCount; i++) modes.Add(ReadMode(r, version));
            int zoneCount = r.ReadUInt16(); var zones = new List<OpenRgbZone>();
            for (int i = 0; i < zoneCount; i++)
            {
                string zn = ReadString(r); int zt = r.ReadInt32();
                uint min = r.ReadUInt32(), max = r.ReadUInt32(), count = r.ReadUInt32();
                int length = r.ReadUInt16(); var matrix = ReadExact(r, length);
                if (length != 0 && (length < 8 || (length - 8) % 4 != 0 ||
                    (ulong)BinaryPrimitives.ReadUInt32LittleEndian(matrix) * BinaryPrimitives.ReadUInt32LittleEndian(matrix.AsSpan(4)) != (ulong)(length - 8) / 4))
                    throw new InvalidDataException("Invalid zone matrix.");
                zones.Add(new(zn, zt, min, max, count, matrix));
            }
            int ledCount = r.ReadUInt16(); var leds = new List<OpenRgbLed>();
            for (int i = 0; i < ledCount; i++) leds.Add(new(ReadString(r), r.ReadUInt32()));
            var colors = ReadColors(r);
            if (ms.Position != ms.Length) throw new InvalidDataException("Unexpected controller trailing bytes.");
            return new(type, name, vendor, description, firmware, serial, location, active, modes, zones, leds, colors);
        }
        catch (EndOfStreamException ex) { throw new InvalidDataException("Truncated controller.", ex); }
    }
    private static OpenRgbMode ReadMode(BinaryReader r, uint v)
    {
        string name = ReadString(r); int value = r.ReadInt32();
        uint flags = r.ReadUInt32(), speedMin = r.ReadUInt32(), speedMax = r.ReadUInt32();
        uint brightMin = v >= 3 ? r.ReadUInt32() : 0, brightMax = v >= 3 ? r.ReadUInt32() : 0;
        uint colorsMin = r.ReadUInt32(), colorsMax = r.ReadUInt32(), speed = r.ReadUInt32();
        uint brightness = v >= 3 ? r.ReadUInt32() : 0, direction = r.ReadUInt32(), colorMode = r.ReadUInt32();
        return new(name, value, flags, speedMin, speedMax, brightMin, brightMax, colorsMin, colorsMax, speed, brightness, direction, colorMode, ReadColors(r));
    }
    private static RgbColor[] ReadColors(BinaryReader r)
    {
        int count = r.ReadUInt16(); var colors = new RgbColor[count];
        for (int i = 0; i < count; i++) { byte red = r.ReadByte(), green = r.ReadByte(), blue = r.ReadByte(); r.ReadByte(); colors[i] = new(red, green, blue); }
        return colors;
    }
    private static void WriteColor(BinaryWriter w, RgbColor c) { w.Write(c.R); w.Write(c.G); w.Write(c.B); w.Write((byte)0); }
    private static string ReadString(BinaryReader r)
    {
        int length = r.ReadUInt16(); var bytes = ReadExact(r, length);
        if (length == 0 || bytes[^1] != 0) throw new InvalidDataException("Missing string terminator.");
        return Encoding.UTF8.GetString(bytes, 0, length - 1);
    }
    private static byte[] ReadExact(BinaryReader r, int length)
    {
        var bytes = r.ReadBytes(length);
        if (bytes.Length != length) throw new EndOfStreamException();
        return bytes;
    }
    private static void WriteString(BinaryWriter w, string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value); w.Write(checked((ushort)(bytes.Length + 1))); w.Write(bytes); w.Write((byte)0);
    }
    private static void ValidateVersion(uint version)
    {
        if (version > MaximumVersion) throw new ArgumentOutOfRangeException(nameof(version));
    }
}
