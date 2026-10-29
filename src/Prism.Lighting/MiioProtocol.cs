using System.Buffers.Binary;
using System.Security.Cryptography;

namespace Prism.Lighting;

public sealed record MiioHeader(uint DeviceId, uint Stamp);

/// <summary>Pure miIO codec. No discovery, sockets or token extraction.</summary>
public static class MiioProtocol
{
    public static byte[] Hello()
    {
        var packet = Enumerable.Repeat((byte)255, 32).ToArray();
        packet[0] = 0x21; packet[1] = 0x31; packet[2] = 0; packet[3] = 32;
        return packet;
    }

    public static MiioHeader ReadHeader(ReadOnlySpan<byte> packet)
    {
        if (packet.Length < 32 || BinaryPrimitives.ReadUInt16BigEndian(packet) != 0x2131 ||
            BinaryPrimitives.ReadUInt16BigEndian(packet[2..]) != packet.Length)
            throw new InvalidDataException("Invalid miIO header.");
        return new(BinaryPrimitives.ReadUInt32BigEndian(packet[8..]), BinaryPrimitives.ReadUInt32BigEndian(packet[12..]));
    }

    private static Aes Cipher(byte[] token)
    {
        if (token.Length != 16) throw new ArgumentException("miIO requires a 16-byte token.", nameof(token));
        var aes = Aes.Create();
        aes.Key = MD5.HashData(token);
        aes.IV = MD5.HashData(aes.Key.Concat(token).ToArray());
        aes.Mode = CipherMode.CBC; aes.Padding = PaddingMode.PKCS7;
        return aes;
    }

    public static byte[] Encode(uint deviceId, uint stamp, byte[] token, ReadOnlySpan<byte> plaintext)
    {
        using var aes = Cipher(token);
        var ciphertext = aes.EncryptCbc(plaintext, aes.IV, PaddingMode.PKCS7);
        if (ciphertext.Length + 32 > ushort.MaxValue) throw new ArgumentOutOfRangeException(nameof(plaintext));
        var packet = new byte[32 + ciphertext.Length];
        BinaryPrimitives.WriteUInt16BigEndian(packet, 0x2131);
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(2), (ushort)packet.Length);
        BinaryPrimitives.WriteUInt32BigEndian(packet.AsSpan(8), deviceId);
        BinaryPrimitives.WriteUInt32BigEndian(packet.AsSpan(12), stamp);
        token.CopyTo(packet, 16); ciphertext.CopyTo(packet, 32);
        MD5.HashData(packet).CopyTo(packet, 16);
        return packet;
    }

    public static byte[] Decode(byte[] packet, byte[] token)
    {
        ReadHeader(packet);
        using var aes = Cipher(token);
        if (packet.Length <= 32 || (packet.Length - 32) % 16 != 0) throw new InvalidDataException("Invalid ciphertext size.");
        var signed = packet.ToArray(); token.CopyTo(signed, 16);
        if (!CryptographicOperations.FixedTimeEquals(packet.AsSpan(16, 16), MD5.HashData(signed)))
            throw new InvalidDataException("miIO checksum mismatch.");
        return aes.DecryptCbc(packet.AsSpan(32), aes.IV, PaddingMode.PKCS7);
    }
}
