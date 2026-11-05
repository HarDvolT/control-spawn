using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace ControlSpawn.Lighting;

public sealed record MiioDevice(string Ip, uint DeviceId, uint Stamp);
public interface IMiioDiscoveryTransport
{
    Task<IReadOnlyList<(string Ip, byte[] Packet)>> ScanAsync(byte[] hello, IReadOnlyList<string> targets, TimeSpan duration, CancellationToken token);
}
public sealed class UdpMiioDiscoveryTransport : IMiioDiscoveryTransport
{
    public async Task<IReadOnlyList<(string Ip, byte[] Packet)>> ScanAsync(byte[] hello, IReadOnlyList<string> targets, TimeSpan duration, CancellationToken token)
    {
        using var socket = new UdpClient { EnableBroadcast = true };
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(duration);
        var replies = new List<(string, byte[])>();
        try
        {
            foreach (var target in targets) await socket.SendAsync(hello, target, 54321, deadline.Token);
            while (true)
            {
                var reply = await socket.ReceiveAsync(deadline.Token);
                if (reply.RemoteEndPoint.Port == 54321) replies.Add((reply.RemoteEndPoint.Address.ToString(), reply.Buffer));
            }
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested) { }
        return replies;
    }
}
/// <summary>Discovery has no token dependency; construction never opens a socket.</summary>
public sealed class MiioDiscovery(IMiioDiscoveryTransport transport, Func<IEnumerable<string>>? addresses = null)
{
    public static IReadOnlyList<string> BroadcastTargets(IEnumerable<string> addresses) => new[] { "255.255.255.255" }
        .Concat(addresses.Select(a => IPAddress.TryParse(a, out var ip) && ip.AddressFamily == AddressFamily.InterNetwork ? ip.GetAddressBytes() : null)
            .Where(b => b is not null && b[0] != 127).Select(b => $"{b![0]}.{b[1]}.{b[2]}.255")).Distinct().ToArray();
    public async Task<IReadOnlyList<MiioDevice>> DiscoverAsync(CancellationToken token = default, IEnumerable<string>? legacyHosts = null)
    {
        token.ThrowIfCancellationRequested();
        var targets = BroadcastTargets((addresses ?? (() => NetworkInterface.GetAllNetworkInterfaces()
            .Where(n => n.OperationalStatus == OperationalStatus.Up).SelectMany(n => n.GetIPProperties().UnicastAddresses).Select(a => a.Address.ToString())))());
        var replies = await transport.ScanAsync(MiioProtocol.Hello(), targets.Concat(legacyHosts ?? []).Distinct().ToArray(), TimeSpan.FromMilliseconds(1500), token);
        var found = new Dictionary<uint, MiioDevice>();
        foreach (var reply in replies)
        {
            if (reply.Packet.Length != 32 || !IPAddress.TryParse(reply.Ip, out _)) continue;
            try { var h = MiioProtocol.ReadHeader(reply.Packet); if (h.DeviceId is not (0 or uint.MaxValue)) found[h.DeviceId] = new(reply.Ip, h.DeviceId, h.Stamp); }
            catch (InvalidDataException) { }
        }
        return found.Values.OrderBy(d => d.DeviceId).ToArray();
    }
}
