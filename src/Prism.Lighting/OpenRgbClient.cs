using System.Buffers.Binary;
using System.Text;
using System.Threading.Channels;
using Prism.Core;

namespace Prism.Lighting;

public sealed class OpenRgbClient(IConnectionFactory connections) : IRgbBackend
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private Stream? stream;
    private CancellationTokenSource? readStop;
    private Task? readTask;
    private Channel<OpenRgbPacket>? incoming;
    private int deviceGeneration;
    private volatile bool invalidated;
    public uint ProtocolVersion { get; private set; }
    public IReadOnlyList<OpenRgbController> Controllers { get; private set; } = Array.Empty<OpenRgbController>();
    public bool DeviceListInvalidated { get => invalidated; private set => invalidated = value; }

    public async Task ConnectAsync(string host = "127.0.0.1", int port = 6742, CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (stream is not null) throw new InvalidOperationException("Already connected.");
            stream = await connections.ConnectAsync(host, port, cancellationToken).ConfigureAwait(false);
            StartReader();
            await SendAsync(0, 40, OpenRgbProtocol.UInt32(OpenRgbProtocol.MaximumVersion), cancellationToken);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(2));
            try { ProtocolVersion = Math.Min(OpenRgbProtocol.MaximumVersion, ReadUInt32((await ReceiveAsync(0, 40, timeout.Token)).Data)); }
            catch (OperationCanceledException) when (timeout.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
            {
                // Reconnect to discard a partially received header before falling back to legacy protocol.
                await StopReaderAsync();
                await stream.DisposeAsync(); stream = await connections.ConnectAsync(host, port, cancellationToken);
                StartReader();
                ProtocolVersion = 0;
            }
            await SendAsync(0, 50, Encoding.UTF8.GetBytes("Control Spawn\0"), cancellationToken);
            await LoadControllersAsync(cancellationToken);
        }
        catch { await StopReaderAsync(); if (stream is not null) await stream.DisposeAsync(); stream = null; throw; }
        finally { gate.Release(); }
    }
    public async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken);
        try { await LoadControllersAsync(cancellationToken); }
        finally { gate.Release(); }
    }
    private async Task LoadControllersAsync(CancellationToken token)
    {
        int generation = Volatile.Read(ref deviceGeneration);
        Controllers = Array.Empty<OpenRgbController>(); DeviceListInvalidated = true;
        await SendAsync(0, 0, Array.Empty<byte>(), token);
        uint count = ReadUInt32((await ReceiveAsync(0, 0, token)).Data);
        if (count > 4096) throw new InvalidDataException("Unreasonable controller count.");
        var list = new List<OpenRgbController>();
        for (uint i = 0; i < count; i++)
        {
            await SendAsync(i, 1, ProtocolVersion == 0 ? Array.Empty<byte>() : OpenRgbProtocol.UInt32(ProtocolVersion), token);
            list.Add(OpenRgbProtocol.ParseController((await ReceiveAsync(i, 1, token)).Data, ProtocolVersion));
        }
        Controllers = list.AsReadOnly(); DeviceListInvalidated = false;
        if (generation != Volatile.Read(ref deviceGeneration))
        {
            DeviceListInvalidated = true; Controllers = Array.Empty<OpenRgbController>();
            throw new InvalidOperationException("Device list changed during enumeration. Retry refresh.");
        }
    }
    public async Task SetDirectModeAsync(uint controller, CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken);
        try { ValidateController(controller); await SendAsync(controller, 1100, Array.Empty<byte>(), cancellationToken); }
        finally { gate.Release(); }
    }
    public async Task UpdateModeAsync(uint controller, int modeIndex, OpenRgbMode mode, CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            ValidateController(controller);
            if (modeIndex < 0 || modeIndex >= Controllers[(int)controller].Modes.Count) throw new ArgumentOutOfRangeException(nameof(modeIndex));
            await SendAsync(controller, 1101, OpenRgbProtocol.UpdateMode(modeIndex, mode, ProtocolVersion), cancellationToken);
        }
        finally { gate.Release(); }
    }
    public async Task SetColorsAsync(uint controller, IReadOnlyList<RgbColor> colors, CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            ValidateController(controller);
            if (colors.Count != Controllers[(int)controller].Colors.Count) throw new ArgumentException("LED color count mismatch.", nameof(colors));
            await SendAsync(controller, 1050, OpenRgbProtocol.UpdateLeds(colors), cancellationToken);
        }
        finally { gate.Release(); }
    }
    /// <summary>Sets one zone's colours. OpenRGB then writes only that zone to the device, not every zone.</summary>
    public async Task SetZoneColorsAsync(uint controller, int zone, IReadOnlyList<RgbColor> colors, CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            ValidateController(controller);
            var zones = Controllers[(int)controller].Zones;
            if (zone < 0 || zone >= zones.Count) throw new ArgumentOutOfRangeException(nameof(zone));
            await SendAsync(controller, 1051, OpenRgbProtocol.UpdateZoneLeds(zone, colors), cancellationToken);
        }
        finally { gate.Release(); }
    }
    /// <summary>
    /// Sets the LED count of a resizable zone (ARGB headers start at 0 LEDs in OpenRGB, so a strip stays dark
    /// until it is sized). RGBCONTROLLER_RESIZEZONE = 1000, payload int32 zone index + int32 new size.
    /// The controller's LED list changes afterwards: call <see cref="RefreshAsync"/> before sending colours.
    /// </summary>
    public async Task ResizeZoneAsync(uint controller, int zone, int size, CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            ValidateController(controller);
            var z = Controllers[(int)controller].Zones;
            if (zone < 0 || zone >= z.Count) throw new ArgumentOutOfRangeException(nameof(zone));
            if (size < z[zone].Minimum || size > z[zone].Maximum) throw new ArgumentOutOfRangeException(nameof(size), $"Zone accepts {z[zone].Minimum}..{z[zone].Maximum} LEDs.");
            var data = new byte[8];
            System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(data, zone);
            System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(4), size);
            await SendAsync(controller, 1000, data, cancellationToken);
        }
        finally { gate.Release(); }
    }
    /// <summary>OpenRGB device_type values as names (0 Motherboard, 1 DRAM, 2 GPU, ...).</summary>
    public static string KindName(int type) => type switch
    {
        0 => "Motherboard", 1 => "RAM", 2 => "GPU", 3 => "Cooler", 4 => "LED strip", 5 => "Keyboard", 6 => "Mouse",
        7 => "Mousemat", 8 => "Headset", 9 => "Headset stand", 10 => "Gamepad", 11 => "Light", 12 => "Speaker",
        13 => "Virtual", 14 => "Storage", 15 => "Case", 16 => "Microphone", 17 => "Accessory", 18 => "Keypad",
        _ => "Other"
    };
    private void ValidateController(uint controller)
    {
        if (DeviceListInvalidated) throw new InvalidOperationException("Device list changed. Refresh controllers first.");
        if (controller >= Controllers.Count) throw new ArgumentOutOfRangeException(nameof(controller));
    }
    private async Task SendAsync(uint device, uint id, byte[] data, CancellationToken token)
    {
        if (stream is null) throw new InvalidOperationException("Not connected.");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token); deadline.CancelAfter(TimeSpan.FromSeconds(5));
        await stream.WriteAsync(OpenRgbProtocol.Encode(new(device, id, data)), deadline.Token).ConfigureAwait(false);
    }
    private async Task<OpenRgbPacket> ReceiveAsync(uint device, uint id, CancellationToken token)
    {
        if (stream is null) throw new InvalidOperationException("Not connected.");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token); deadline.CancelAfter(TimeSpan.FromSeconds(5));
        var packet = await incoming!.Reader.ReadAsync(deadline.Token);
        if (packet.Device == device && packet.Id == id) return packet;
        throw new InvalidDataException($"Unexpected packet {packet.Id} for device {packet.Device}.");
    }
    private void StartReader()
    {
        readStop = new();
        incoming = Channel.CreateBounded<OpenRgbPacket>(new BoundedChannelOptions(16) { SingleWriter = true, SingleReader = true });
        readTask = ReadLoopAsync(stream!, incoming, readStop.Token);
    }
    private async Task ReadLoopAsync(Stream connection, Channel<OpenRgbPacket> channel, CancellationToken token)
    {
        try
        {
            while (true)
            {
                byte[] header = new byte[16]; await connection.ReadExactlyAsync(header, token);
                var h = OpenRgbProtocol.DecodeHeader(header); var data = new byte[h.Size]; await connection.ReadExactlyAsync(data, token);
                if (h.Id == 100)
                {
                    Interlocked.Increment(ref deviceGeneration);
                    DeviceListInvalidated = true; Controllers = Array.Empty<OpenRgbController>();
                }
                else await channel.Writer.WriteAsync(new(h.Device, h.Id, data), token);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { channel.Writer.TryComplete(); }
        catch (Exception ex) { DeviceListInvalidated = true; channel.Writer.TryComplete(ex); }
    }
    private async Task StopReaderAsync()
    {
        if (readStop is null) return;
        readStop.Cancel();
        if (readTask is not null) await readTask;
        readStop.Dispose(); readStop = null; readTask = null;
    }
    private static uint ReadUInt32(byte[] data) => data.Length == 4 ? BinaryPrimitives.ReadUInt32LittleEndian(data) : throw new InvalidDataException("Expected uint32.");
    public async ValueTask DisposeAsync()
    {
        await gate.WaitAsync();
        try { await StopReaderAsync(); if (stream is not null) await stream.DisposeAsync(); stream = null; Controllers = Array.Empty<OpenRgbController>(); }
        finally { gate.Release(); }
    }
}
