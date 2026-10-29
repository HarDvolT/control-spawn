using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Prism.Core;

namespace Prism.Lighting;

public interface IMiioTransport : IAsyncDisposable
{
    Task<byte[]> ExchangeAsync(byte[] packet, CancellationToken cancellationToken);
}

/// <summary>Connected UDP socket restricts replies to the configured bulb. Created lazily.</summary>
public sealed class UdpMiioTransport(string host) : IMiioTransport
{
    private UdpClient? socket;
    public async Task<byte[]> ExchangeAsync(byte[] packet, CancellationToken cancellationToken)
    {
        if (socket is null) { socket = new UdpClient(); socket.Connect(host, 54321); }
        await socket.SendAsync(packet, cancellationToken);
        return (await socket.ReceiveAsync(cancellationToken)).Buffer;
    }
    public ValueTask DisposeAsync() { socket?.Dispose(); return ValueTask.CompletedTask; }
}

public sealed class MiioLampClient : ILampClient
{
    private readonly IMiioTransport transport;
    private readonly byte[] token;
    private readonly IClock clock;
    private readonly SemaphoreSlim gate = new(1, 1);
    private MiioHeader? header;
    private TimeSpan stampAt, lastSent;
    private bool sent;
    private int id;
    private bool disposed;
    private readonly uint? expectedDeviceId;
    public bool IsMusicMode => false;
    public MiioLampClient(IMiioTransport transport, byte[] token, IClock clock, uint? expectedDeviceId = null)
    {
        if (token.Length != 16) throw new ArgumentException("Token must contain 16 bytes.", nameof(token));
        this.expectedDeviceId = expectedDeviceId;
        this.transport = transport; this.token = token.ToArray(); this.clock = clock;
    }
    public Task SetPowerAsync(bool on, CancellationToken cancellationToken = default) =>
        SendAsync("set_power", [on ? "on" : "off", "smooth", 500], cancellationToken);
    public Task SetColorAsync(RgbColor color, CancellationToken cancellationToken = default) =>
        SendAsync("set_rgb", [color.Packed, "smooth", 500], cancellationToken);
    public Task SetBrightnessAsync(int brightness, CancellationToken cancellationToken = default) =>
        SendAsync("set_bright", [brightness is >= 1 and <= 100 ? brightness : throw new ArgumentOutOfRangeException(nameof(brightness)), "smooth", 500], cancellationToken);
    public Task SetColorTemperatureAsync(int kelvin, CancellationToken cancellationToken = default) =>
        SendAsync("set_ct_abx", [kelvin is >= 1700 and <= 6500 ? kelvin : throw new ArgumentOutOfRangeException(nameof(kelvin)), "smooth", 500], cancellationToken);
    public Task<JsonElement> GetPropertiesAsync(CancellationToken cancellationToken = default) =>
        SendAsync("get_prop", ["power", "bright", "rgb", "ct", "color_mode"], cancellationToken);

    private async Task<byte[]> ExchangeAsync(byte[] packet, CancellationToken token)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(2));
        return await transport.ExchangeAsync(packet, deadline.Token);
    }

    private async Task<JsonElement> SendAsync(string method, object[] parameters, CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            var requestId = ++id;
            // Retry with the same id: setters are idempotent. Refresh hello after a timeout.
            for (var attempt = 0; ; attempt++)
            {
                try
                {
                    if (header is null || clock.Elapsed - stampAt > TimeSpan.FromMinutes(1))
                    {
                        var hello = await ExchangeAsync(MiioProtocol.Hello(), cancellationToken);
                        if (hello.Length != 32) throw new InvalidDataException("Expected miIO hello reply.");
                        header = MiioProtocol.ReadHeader(hello);
                        if (expectedDeviceId is { } expected && header.DeviceId != expected) throw new InvalidOperationException("Unexpected miIO device.");
                        stampAt = clock.Elapsed;
                    }
                    var wait = TimeSpan.FromMilliseconds(500) - (clock.Elapsed - lastSent);
                    if (sent && wait > TimeSpan.Zero) await clock.DelayAsync(wait, cancellationToken);
                    uint stamp = unchecked(header.Stamp + (uint)Math.Max(0, (clock.Elapsed - stampAt).TotalSeconds));
                    var payload = JsonSerializer.SerializeToUtf8Bytes(new { id = requestId, method, @params = parameters }).Append((byte)0).ToArray();
                    sent = true; lastSent = clock.Elapsed;
                    var reply = await ExchangeAsync(MiioProtocol.Encode(header.DeviceId, stamp, token, payload), cancellationToken);
                    var replyHeader = MiioProtocol.ReadHeader(reply);
                    if (replyHeader.DeviceId != header.DeviceId) throw new InvalidDataException("Unexpected miIO device.");
                    using var json = JsonDocument.Parse(Encoding.UTF8.GetString(MiioProtocol.Decode(reply, token)).TrimEnd('\0'));
                    if (!json.RootElement.TryGetProperty("id", out var responseId) || responseId.GetInt32() != requestId)
                        throw new InvalidDataException("Unexpected miIO response id.");
                    // Don't discard the fractional second on every sub-second update.
                    // A newer device clock may advance our anchor; equal/older stamps cannot rewind it.
                    var estimatedStamp = unchecked(header.Stamp + (uint)Math.Max(0, (clock.Elapsed - stampAt).TotalSeconds));
                    if (unchecked((int)(replyHeader.Stamp - estimatedStamp)) > 0) { header = replyHeader; stampAt = clock.Elapsed; }
                    if (json.RootElement.TryGetProperty("error", out _)) throw new InvalidOperationException("Lamp rejected the miIO command.");
                    return json.RootElement.GetProperty("result").Clone();
                }
                catch (Exception ex) when (attempt < 2 && !cancellationToken.IsCancellationRequested &&
                    ex is OperationCanceledException or SocketException or IOException)
                { header = null; }
            }
        }
        finally { gate.Release(); }
    }
    public async ValueTask DisposeAsync()
    {
        await gate.WaitAsync();
        try
        {
            if (disposed) return;
            disposed = true;
            try { await transport.DisposeAsync(); }
            finally { System.Security.Cryptography.CryptographicOperations.ZeroMemory(token); }
        }
        finally { gate.Release(); }
    }
}
