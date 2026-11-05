using System.Buffers.Binary;
using System.Text;
using ControlSpawn.Core;
using Xunit;

namespace ControlSpawn.Lighting.Tests;

public sealed class OpenRgbTests
{
    // Hand-authored protocol-0 controller: name A, one LED, one RGB(1,2,3) color.
    private static readonly byte[] LegacyController = Convert.FromHexString(
        "32000000" + "00000000" + "02004100" + "010000010000010000010000" +
        "000000000000" + "0000" + "0100" + "04004C45440000000000" + "010001020300");

    [Fact]
    public void HeaderAndLedsMatchFixedWireFixture()
    {
        var encoded = OpenRgbProtocol.Encode(new(2, 1050, OpenRgbProtocol.UpdateLeds(new[] { new RgbColor(0x11, 0x22, 0x33) })));
        byte[] fixture = Convert.FromHexString("4F524742020000001A0400000A0000000A000000010011223300");
        Assert.Equal(fixture, encoded);
        var decoded = OpenRgbProtocol.Decode(fixture);
        Assert.Equal(2u, decoded.Device); Assert.Equal(1050u, decoded.Id);
        Assert.Equal(fixture[16..], decoded.Data);
    }
    [Fact]
    public void ParsesIndependentLegacyControllerFixture()
    {
        var controller = OpenRgbProtocol.ParseController(LegacyController, 0);
        Assert.Equal("A", controller.Name); Assert.Empty(controller.Vendor);
        Assert.Equal("LED", Assert.Single(controller.Leds).Name);
        Assert.Equal(new RgbColor(1, 2, 3), Assert.Single(controller.Colors));
    }
    [Theory]
    [InlineData(1u)] [InlineData(2u)] [InlineData(3u)]
    public void ParsesVersionedModesZonesAndMatrices(uint version)
    {
        var controller = OpenRgbProtocol.ParseController(ControllerFixture(version), version);
        Assert.Equal("Vendor", controller.Vendor);
        var mode = Assert.Single(controller.Modes);
        Assert.Equal(77, mode.Value); Assert.Equal(2u, mode.Speed);
        Assert.Equal(version >= 3 ? 80u : 0u, mode.Brightness);
        Assert.Equal(new RgbColor(10, 20, 30), Assert.Single(mode.Colors));
        var zone = Assert.Single(controller.Zones);
        Assert.Equal(12, zone.Matrix.Length); Assert.Equal(1u, zone.Count);
    }
    [Fact]
    public void UpdateModePreservesInternalValueAndWritesCorrectDataSize()
    {
        var mode = new OpenRgbMode("D", 77, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, Array.Empty<RgbColor>());
        var data = OpenRgbProtocol.UpdateMode(2, mode, 0);
        byte[] fixture = Convert.FromHexString("3200000002000000020044004D000000" + new string('0', 68));
        Assert.Equal(fixture, data);
        Assert.Equal((uint)data.Length, BinaryPrimitives.ReadUInt32LittleEndian(data));
    }
    [Fact]
    public void MalformedPacketsAndControllersAreRejected()
    {
        Assert.Throws<InvalidDataException>(() => OpenRgbProtocol.Decode(new byte[16]));
        byte[] huge = Convert.FromHexString("4F5247420000000000000000FFFFFFFF");
        Assert.Throws<InvalidDataException>(() => OpenRgbProtocol.Decode(huge));
        Assert.Throws<InvalidDataException>(() => OpenRgbProtocol.ParseController(LegacyController[..^1], 0));
        var badString = (byte[])LegacyController.Clone(); badString[11] = 1;
        Assert.Throws<InvalidDataException>(() => OpenRgbProtocol.ParseController(badString, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => OpenRgbProtocol.ParseController(LegacyController, 4));
    }
    [Fact]
    public async Task ClientNegotiatesEnumeratesAndWritesUsingOnlyFragmentedFixtureStream()
    {
        var wire = new DuplexFixtureStream(Reply(40, OpenRgbProtocol.UInt32(5))
            .Concat(Reply(0, OpenRgbProtocol.UInt32(1))).Concat(Reply(1, ControllerFixture(3))).ToArray());
        var factory = new FakeConnectionFactory(); factory.Streams.Enqueue(wire);
        await using var client = new OpenRgbClient(factory);
        await client.ConnectAsync();
        Assert.Equal(3u, client.ProtocolVersion); Assert.Single(client.Controllers);
        await client.SetDirectModeAsync(0);
        await client.SetColorsAsync(0, new[] { new RgbColor(1, 2, 3) });
        using var packets = new MemoryStream(wire.Output.ToArray());
        var ids = new List<uint>();
        while (packets.Position < packets.Length)
        {
            var header = new byte[16]; packets.ReadExactly(header);
            var h = OpenRgbProtocol.DecodeHeader(header); ids.Add(h.Id); packets.Position += h.Size;
        }
        Assert.Equal(new uint[] { 40, 50, 0, 1, 1100, 1050 }, ids);
    }
    [Fact]
    public async Task IdleDeviceListNotificationInvalidatesIndicesBeforeFurtherWrites()
    {
        var wire = new DuplexFixtureStream(Reply(40, OpenRgbProtocol.UInt32(3))
            .Concat(Reply(0, OpenRgbProtocol.UInt32(1))).Concat(Reply(1, ControllerFixture(3))).ToArray());
        var factory = new FakeConnectionFactory(); factory.Streams.Enqueue(wire);
        await using var client = new OpenRgbClient(factory); await client.ConnectAsync();
        wire.Feed(Reply(100, Array.Empty<byte>()));
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        while (!client.DeviceListInvalidated) await Task.Delay(1, deadline.Token);
        await Assert.ThrowsAsync<InvalidOperationException>(() => client.SetDirectModeAsync(0));
        Assert.Empty(client.Controllers);
    }
    private static byte[] Reply(uint id, byte[] data) => OpenRgbProtocol.Encode(new(0, id, data));
    [Fact]
    public async Task ResizeRefreshAndDirectModeUseOnlyFixtureBytes()
    {
        static byte[] Controller(int count)
        {
            using var data = new MemoryStream(); using var w = new BinaryWriter(data, Encoding.UTF8, true);
            void Text(string text) { var bytes = Encoding.UTF8.GetBytes(text + "\0"); w.Write((ushort)bytes.Length); w.Write(bytes); }
            w.Write(0u); w.Write(0); foreach (var text in new[] { "Board", "ASUS", "", "", "serial", "USB" }) Text(text);
            w.Write((ushort)0); w.Write(0); // modes, active
            w.Write((ushort)1); Text("ARGB"); w.Write(1); w.Write(0u); w.Write(120u); w.Write((uint)count); w.Write((ushort)0);
            w.Write((ushort)count); for (int i = 0; i < count; i++) { Text("LED " + i); w.Write((uint)i); }
            w.Write((ushort)count); for (int i = 0; i < count; i++) w.Write(0u);
            uint size = (uint)data.Length; data.Position = 0; w.Write(size); return data.ToArray();
        }
        var wire = new DuplexFixtureStream(Reply(40, OpenRgbProtocol.UInt32(3))
            .Concat(Reply(0, OpenRgbProtocol.UInt32(1))).Concat(Reply(1, Controller(0))).ToArray());
        var factory = new FakeConnectionFactory(); factory.Streams.Enqueue(wire);
        await using var client = new OpenRgbClient(factory); await client.ConnectAsync();
        Assert.Empty(client.Controllers[0].Colors);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => client.ResizeZoneAsync(0, 0, 121));
        await client.ResizeZoneAsync(0, 0, 30);
        wire.Feed(Reply(0, OpenRgbProtocol.UInt32(1)).Concat(Reply(1, Controller(30))).ToArray());
        await client.RefreshAsync(); await client.SetDirectModeAsync(0);
        await client.SetColorsAsync(0, Enumerable.Repeat(new RgbColor(1, 2, 3), 30).ToArray());
        var projected = LedLayers.Project("board", client.Controllers[0]);
        Assert.Equal(30, projected.LedCount); Assert.Equal(120, projected.Zones![0].MaxLeds);
        using var output = new MemoryStream(wire.Output.ToArray()); var packets = new List<OpenRgbPacket>();
        while (output.Position < output.Length)
        {
            var header = new byte[16]; output.ReadExactly(header); var h = OpenRgbProtocol.DecodeHeader(header);
            var payload = new byte[h.Size]; output.ReadExactly(payload); packets.Add(new(h.Device, h.Id, payload));
        }
        Assert.Equal(new uint[] { 40, 50, 0, 1, 1000, 0, 1, 1100, 1050 }, packets.Select(p => p.Id));
        Assert.Equal(Convert.FromHexString("000000001E000000"), packets.Single(p => p.Id == 1000).Data);
    }

    // Independent field-by-field fixture including nonempty mode, matrix, LED, and colors.
    private static byte[] ControllerFixture(uint version)
    {
        using var ms = new MemoryStream(); using var w = new BinaryWriter(ms, Encoding.UTF8, true);
        void Text(string s) { var bytes = Encoding.UTF8.GetBytes(s + "\0"); w.Write((ushort)bytes.Length); w.Write(bytes); }
        w.Write(0u); w.Write(1); Text("Board"); Text("Vendor"); Text("Description"); Text("Firmware"); Text("Serial"); Text("USB");
        w.Write((ushort)1); w.Write(0);
        Text("Direct"); w.Write(77); w.Write(1u); w.Write(1u); w.Write(10u);
        if (version >= 3) { w.Write(1u); w.Write(100u); }
        w.Write(1u); w.Write(1u); w.Write(2u);
        if (version >= 3) w.Write(80u);
        w.Write(0u); w.Write(1u); w.Write((ushort)1); w.Write(new byte[] { 10, 20, 30, 0 });
        w.Write((ushort)1); Text("Zone"); w.Write(2); w.Write(1u); w.Write(1u); w.Write(1u);
        w.Write((ushort)12); w.Write(1u); w.Write(1u); w.Write(0u);
        w.Write((ushort)1); Text("LED"); w.Write(0u); w.Write((ushort)1); w.Write(new byte[] { 1, 2, 3, 0 });
        uint length = (uint)ms.Length; ms.Position = 0; w.Write(length); return ms.ToArray();
    }
}
