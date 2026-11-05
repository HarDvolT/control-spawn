using System.Security.Cryptography;
using System.Text.Json;
using ControlSpawn.Core;

namespace ControlSpawn.Lighting;

public interface ILampSecretProtector
{
    byte[] Protect(byte[] value);
    byte[] Unprotect(byte[] value);
}
public sealed class LampDpapiProtector : ILampSecretProtector
{
    private readonly SecretStore store = new();
    public byte[] Protect(byte[] value) => store.ProtectPayload(value);
    public byte[] Unprotect(byte[] value) => store.UnprotectPayload(value);
}

/// <summary>Service machine store by default; explicit paths/protectors support legacy fixtures.</summary>
public sealed class LampTokenStore(string? path = null, ILampSecretProtector? protector = null, SecretStore? secrets = null)
{
    private readonly SecretStore? secrets = secrets ?? (protector is null ? new SecretStore() : null);
    private readonly string path = path ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), ControlSpawn.Core.ProductNames.DataFolder, "lamp.json");
    private readonly ILampSecretProtector protector = protector ?? new LampDpapiProtector();
    private string DevicePath(uint deviceId) => path + "." + deviceId.ToString(System.Globalization.CultureInfo.InvariantCulture);
    public void Save(uint deviceId, byte[] token) { if (secrets is not null) { if (token.Length != 16) throw new ArgumentException("Token must contain 16 bytes."); secrets.Save("lamp." + deviceId, token); } else new LampTokenStore(DevicePath(deviceId), protector).Save(token); }
    public byte[]? Load(uint deviceId) => secrets is not null ? LoadSecret("lamp." + deviceId, DevicePath(deviceId)) : new LampTokenStore(DevicePath(deviceId), protector).Load();
    public void Remove(uint deviceId) { if (secrets is not null) { secrets.Remove("lamp." + deviceId); return; } if (File.Exists(DevicePath(deviceId))) File.Delete(DevicePath(deviceId)); }
    public void DeleteLegacy() { if (secrets is not null) { secrets.Remove("lamp"); return; } if (File.Exists(path)) File.Delete(path); }
    public void Save(byte[] token)
    {
        if (token.Length != 16) throw new ArgumentException("Token must contain 16 bytes.", nameof(token));
        if (secrets is not null) { secrets.Save("lamp", token); return; }
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        File.WriteAllText(path, JsonSerializer.Serialize(new { dpapi = Convert.ToBase64String(protector.Protect(token)) }));
    }
    private byte[]? LoadSecret(string key, string legacy)
    {
        try { var token = secrets!.Load(key, legacy); if (token is null || token.Length == 16) return token; CryptographicOperations.ZeroMemory(token); return null; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or CryptographicException or FormatException or InvalidOperationException or KeyNotFoundException) { return null; }
    }
    public byte[]? Load()
    {
        if (secrets is not null) return LoadSecret("lamp", path);
        try
        {
            using var json = JsonDocument.Parse(File.ReadAllText(path));
            var token = protector.Unprotect(Convert.FromBase64String(json.RootElement.GetProperty("dpapi").GetString()!));
            if (token.Length == 16) return token;
            CryptographicOperations.ZeroMemory(token); return null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or FormatException or CryptographicException or InvalidOperationException or KeyNotFoundException)
        { return null; }
    }
}

public sealed record LampConnection(ILampClient? Client, string Transport, string? Host);
public sealed class LampFactory(IConnectionFactory connections, YeelightDiscovery discovery, IClock clock,
    Func<string, IMiioTransport>? miio = null)
{
    public async Task<LampConnection> CreateAsync(string? host, byte[]? token, CancellationToken cancellationToken = default)
    {
        var candidates = new List<(string Host, int Port)>();
        if (!string.IsNullOrWhiteSpace(host)) candidates.Add((host, 55443));
        try
        {
            var devices = await discovery.DiscoverAsync(TimeSpan.FromSeconds(1), cancellationToken);
            candidates.AddRange(devices.Where(d => host is null || d.Location.Host == host).Select(d => (d.Location.Host, d.Location.Port)));
        }
        catch (Exception e) when (e is IOException or System.Net.Sockets.SocketException) { }
        foreach (var candidate in candidates.Distinct())
        {
            var client = new YeelightClient(connections, clock, new TcpMusicListener());
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(2));
            try
            {
                await client.ConnectAsync(candidate.Host, candidate.Port, timeout.Token);
                return new(client, "yeelight", candidate.Host);
            }
            catch (Exception e) when (!cancellationToken.IsCancellationRequested && e is IOException or System.Net.Sockets.SocketException or OperationCanceledException)
            { await client.DisposeAsync(); }
        }
        if (!string.IsNullOrWhiteSpace(host) && token is { Length: 16 })
            return new(new MiioLampClient((miio ?? (h => new UdpMiioTransport(h)))(host), token, clock), "miio", host);
        return new(null, "not configured", host);
    }
}
