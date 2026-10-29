using Prism.Core;
using System.Threading.Channels;

namespace Prism.Lighting.Tests;

internal sealed class ManualClock : IClock
{
    public TimeSpan Elapsed { get; set; }
    public Task DelayAsync(TimeSpan duration, CancellationToken cancellationToken)
    { cancellationToken.ThrowIfCancellationRequested(); Elapsed += duration; return Task.CompletedTask; }
}
/// <summary>Preloaded incoming bytes and separate recorded outgoing bytes. Never opens a socket.</summary>
internal sealed class DuplexFixtureStream : Stream
{
    private readonly Channel<byte> input = Channel.CreateUnbounded<byte>();
    public DuplexFixtureStream(byte[] incoming) => Feed(incoming);
    public void Feed(byte[] bytes) { foreach (byte b in bytes) input.Writer.TryWrite(b); }
    public MemoryStream Output { get; } = new();
    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => true;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override int Read(byte[] buffer, int offset, int count) => ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();
    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        // Fragment network reads deliberately. At the end, remain open until canceled.
        if (buffer.Length == 0) return 0;
        byte first = await input.Reader.ReadAsync(cancellationToken);
        buffer.Span[0] = first;
        int read = 1;
        while (read < Math.Min(buffer.Length, 3) && input.Reader.TryRead(out byte b)) buffer.Span[read++] = b;
        return read;
    }
    public override void Write(byte[] buffer, int offset, int count) => Output.Write(buffer, offset, count);
    public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) => Output.WriteAsync(buffer, cancellationToken);
    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
}
