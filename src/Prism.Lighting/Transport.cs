using System.Net;
using System.Net.Sockets;

namespace Prism.Lighting;

public interface IConnectionFactory
{
    Task<Stream> ConnectAsync(string host, int port, CancellationToken cancellationToken);
}
public sealed class TcpConnectionFactory : IConnectionFactory
{
    public async Task<Stream> ConnectAsync(string host, int port, CancellationToken cancellationToken)
    {
        var client = new TcpClient();
        try { await client.ConnectAsync(host, port, cancellationToken).ConfigureAwait(false); return client.GetStream(); }
        catch { client.Dispose(); throw; }
    }
}
public interface IMusicListener : IAsyncDisposable
{
    int Port { get; }
    void Start(IPAddress localAddress);
    Task<Stream> AcceptAsync(IPAddress expectedPeer, CancellationToken cancellationToken);
}
public sealed class TcpMusicListener : IMusicListener
{
    private TcpListener? listener;
    public int Port => ((IPEndPoint)(listener?.LocalEndpoint ?? throw new InvalidOperationException("Listener not started."))).Port;
    public void Start(IPAddress localAddress)
    {
        if (listener is not null) throw new InvalidOperationException("Already listening.");
        listener = new TcpListener(localAddress, 0); listener.Start();
    }
    public async Task<Stream> AcceptAsync(IPAddress expectedPeer, CancellationToken cancellationToken)
    {
        if (listener is null) throw new InvalidOperationException("Listener not started.");
        while (true)
        {
            var client = await listener.AcceptTcpClientAsync(cancellationToken).ConfigureAwait(false);
            if (((IPEndPoint)client.Client.RemoteEndPoint!).Address.Equals(expectedPeer)) return client.GetStream();
            client.Dispose();
        }
    }
    public ValueTask DisposeAsync() { listener?.Stop(); listener = null; return ValueTask.CompletedTask; }
}
