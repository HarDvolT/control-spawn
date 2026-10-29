using System.Net;
using System.Net.Sockets;
using System.Text;

namespace Prism.Lighting;

public interface IDiscoveryTransport
{
    Task<IReadOnlyList<string>> SearchAsync(TimeSpan duration, CancellationToken cancellationToken);
}
public sealed record YeelightDevice(string Id, string Model, Uri Location, IReadOnlyList<string> SupportedMethods);
public sealed class YeelightDiscovery(IDiscoveryTransport transport)
{
    public const string SearchMessage = "M-SEARCH * HTTP/1.1\r\nHOST: 239.255.255.250:1982\r\nMAN: \"ssdp:discover\"\r\nST: wifi_bulb\r\n\r\n";
    public static YeelightDevice Parse(string response)
    {
        var lines = response.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        if (lines.Length == 0 || lines[0] != "HTTP/1.1 200 OK") throw new InvalidDataException("Invalid discovery response.");
        var fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (string line in lines.Skip(1))
        {
            int colon = line.IndexOf(':');
            if (colon > 0) fields[line[..colon].Trim()] = line[(colon + 1)..].Trim();
        }
        if (!fields.TryGetValue("Location", out var location) || !Uri.TryCreate(location, UriKind.Absolute, out var uri) ||
            uri.Scheme != "yeelight" || !IPAddress.TryParse(uri.Host, out _) || uri.Port <= 0 || uri.Port > 65535 ||
            !fields.TryGetValue("id", out var id)) throw new InvalidDataException("Invalid bulb endpoint or ID.");
        return new(id, fields.GetValueOrDefault("model", ""), uri,
            fields.GetValueOrDefault("support", "").Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }
    public async Task<IReadOnlyList<YeelightDevice>> DiscoverAsync(TimeSpan duration, CancellationToken cancellationToken = default)
    {
        var devices = new Dictionary<string, YeelightDevice>();
        foreach (string response in await transport.SearchAsync(duration, cancellationToken))
            try { var device = Parse(response); devices[device.Id] = device; } catch (InvalidDataException) { }
        return devices.Values.ToArray();
    }
}
public sealed class UdpDiscoveryTransport : IDiscoveryTransport
{
    public async Task<IReadOnlyList<string>> SearchAsync(TimeSpan duration, CancellationToken cancellationToken)
    {
        if (duration <= TimeSpan.Zero || duration > TimeSpan.FromMinutes(1)) throw new ArgumentOutOfRangeException(nameof(duration));
        using var udp = new UdpClient(AddressFamily.InterNetwork);
        byte[] request = Encoding.ASCII.GetBytes(YeelightDiscovery.SearchMessage);
        await udp.SendAsync(request, new IPEndPoint(IPAddress.Parse("239.255.255.250"), 1982), cancellationToken);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken); deadline.CancelAfter(duration);
        var responses = new List<string>();
        try
        {
            while (responses.Count < 256)
                responses.Add(Encoding.UTF8.GetString((await udp.ReceiveAsync(deadline.Token)).Buffer));
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { }
        return responses;
    }
}
