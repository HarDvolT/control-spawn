using System.Net;
using Prism.Core;

namespace Prism.Lighting;

public sealed class FakeRgbBackend : IRgbBackend
{
    public Dictionary<uint, RgbColor[]> Colors { get; } = new();
    public Task SetColorsAsync(uint controller, IReadOnlyList<RgbColor> colors, CancellationToken cancellationToken = default)
    { cancellationToken.ThrowIfCancellationRequested(); Colors[controller] = colors.ToArray(); return Task.CompletedTask; }
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
public sealed class FakeLampClient : ILampClient
{
    public bool IsMusicMode { get; set; }
    public List<RgbColor> Colors { get; } = new();
    public bool Power { get; private set; }
    public Task SetColorAsync(RgbColor color, CancellationToken cancellationToken = default)
    { cancellationToken.ThrowIfCancellationRequested(); Colors.Add(color); return Task.CompletedTask; }
    public Task SetPowerAsync(bool on, CancellationToken cancellationToken = default)
    { cancellationToken.ThrowIfCancellationRequested(); Power = on; return Task.CompletedTask; }
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
public sealed class FakeInputs : IAudioLevelInput, IScreenAverageInput, ICpuTemperatureInput
{
    public double Level { get; set; }
    public RgbColor Average { get; set; } = new(50, 100, 150);
    public double Temperature { get; set; } = 40;
}
public sealed class FakeConnectionFactory : IConnectionFactory
{
    public Queue<Stream> Streams { get; } = new();
    public Task<Stream> ConnectAsync(string host, int port, CancellationToken cancellationToken)
    { cancellationToken.ThrowIfCancellationRequested(); return Task.FromResult(Streams.Dequeue()); }
}
public sealed class FakeMusicListener : IMusicListener
{
    public int Port => 12345;
    public Stream Connection { get; init; } = new MemoryStream();
    public void Start(IPAddress localAddress) { }
    public Task<Stream> AcceptAsync(IPAddress expectedPeer, CancellationToken cancellationToken)
    { cancellationToken.ThrowIfCancellationRequested(); return Task.FromResult(Connection); }
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
public sealed class FakeDiscoveryTransport : IDiscoveryTransport
{
    public IReadOnlyList<string> Responses { get; init; } = Array.Empty<string>();
    public Task<IReadOnlyList<string>> SearchAsync(TimeSpan duration, CancellationToken cancellationToken) => Task.FromResult(Responses);
}
public static class LightingComposition
{
    public static (IRgbBackend Rgb, ILampClient Lamp, YeelightDiscovery Discovery) Create(IEnumerable<string> args, IClock clock)
    {
        if (args.Contains("--mock", StringComparer.OrdinalIgnoreCase))
            return (new FakeRgbBackend(), new FakeLampClient(), new(new FakeDiscoveryTransport()));
        var tcp = new TcpConnectionFactory();
        return (new OpenRgbClient(tcp), new YeelightClient(tcp, clock, new TcpMusicListener()), new(new UdpDiscoveryTransport()));
    }
}
