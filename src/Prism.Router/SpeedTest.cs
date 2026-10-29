using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Sockets;

namespace Prism.Router;

/// <summary>A speed test failure the UI can show as plain words.</summary>
public sealed class SpeedTestError : Exception
{
    public SpeedTestError(string message) : base(message)
    {
    }

    public SpeedTestError(string message, Exception inner) : base(message, inner)
    {
    }
}

/// <summary>One speed test result; milliseconds for ping/jitter, Mbps for down/up.</summary>
public sealed record SpeedTestResult(
    double? Ping,
    double? Jitter,
    double? Down,
    double? Up,
    double At);

/// <summary>One HTTP connection to the speed test server, abstracted so tests never use the network.</summary>
public interface ISpeedTestConnection : IDisposable
{
    /// <summary>GET and return the response body stream (the caller disposes it).</summary>
    Stream Get(string path);

    /// <summary>POST a body of <paramref name="byteCount"/> bytes, reporting each chunk sent.</summary>
    void Post(string path, long byteCount, Action<int> onSent, CancellationToken cancel);
}

/// <summary>Creates speed test connections; the default one talks to speed.cloudflare.com.</summary>
public interface ISpeedTestTransport
{
    ISpeedTestConnection Connect();
}

/// <summary>
/// Internet speed test: ping, download and upload against Cloudflare's public speed test servers
/// (speed.cloudflare.com). Has nothing to do with the router's API. Uses normal TLS.
/// </summary>
public static class SpeedTest
{
    public const string Host = "speed.cloudflare.com";
    public const int PingCount = 10;
    public const double DownSeconds = 8;   // seconds of downloading
    public const double UpSeconds = 6;     // seconds of uploading
    public const int DownStreams = 4;      // parallel connections, like browser speed tests
    public const int UpStreams = 3;
    public const long DownChunk = 25_000_000;
    public const long UpChunk = 2_000_000;
    public const long DownCap = 150_000_000; // stop early on fast lines, so a test uses at most ~200 MB
    public const long UpCap = 50_000_000;
    public const double WarmupSeconds = 1.0; // ignore the slow start of each phase
    public const int TimeoutSeconds = 10;

    /// <summary>Median round trip and jitter (mean change between consecutive samples), in ms.</summary>
    public static (double? Median, double? Jitter) PingStats(IReadOnlyList<double> samplesMs)
    {
        if (samplesMs.Count == 0)
        {
            return (null, null);
        }

        double jitter = 0.0;
        if (samplesMs.Count > 1)
        {
            var total = 0.0;
            for (var i = 1; i < samplesMs.Count; i++)
            {
                total += Math.Abs(samplesMs[i] - samplesMs[i - 1]);
            }

            jitter = total / (samplesMs.Count - 1);
        }

        var sorted = samplesMs.OrderBy(value => value).ToList();
        var middle = sorted.Count / 2;
        var median = sorted.Count % 2 == 1
            ? sorted[middle]
            : (sorted[middle - 1] + sorted[middle]) / 2.0;

        return (median, jitter);
    }

    public static double Mbps(long bytes, double seconds) =>
        seconds > 0 ? bytes * 8 / 1e6 / seconds : 0.0;

    /// <summary>
    /// Runs the blocking test. progress(phase, value, fraction) with phase in ping / download /
    /// upload; value is ms or Mbps. Returns the result, or null when cancelled.
    /// </summary>
    public static SpeedTestResult? Run(
        Action<string, double?, double> progress,
        CancellationToken cancel,
        ISpeedTestTransport? transport = null)
    {
        if (progress is null)
        {
            throw new ArgumentNullException(nameof(progress));
        }

        var speedTestTransport = transport ?? new CloudflareSpeedTestTransport();

        var (ping, jitter) = MeasurePing(progress, cancel, speedTestTransport);
        if (cancel.IsCancellationRequested)
        {
            return null;
        }

        var down = TimedPhase(
            (meter, stop, errors) => DownloadWorker(speedTestTransport, meter, stop, errors),
            DownStreams,
            DownSeconds,
            DownCap,
            "download",
            progress,
            cancel);
        if (down is null)
        {
            return null;
        }

        var up = TimedPhase(
            (meter, stop, errors) => UploadWorker(speedTestTransport, meter, stop, errors),
            UpStreams,
            UpSeconds,
            UpCap,
            "upload",
            progress,
            cancel);
        if (up is null)
        {
            return null;
        }

        return new SpeedTestResult(ping, jitter, down, up, Now());
    }

    private static (double? Ping, double? Jitter) MeasurePing(
        Action<string, double?, double> progress,
        CancellationToken cancel,
        ISpeedTestTransport transport)
    {
        var samples = new List<double>();
        using var connection = transport.Connect();
        try
        {
            using (var warmup = connection.Get("/__down?bytes=0"))
            {
                warmup.CopyTo(Stream.Null); // first request also opens TLS; not counted
            }

            for (var i = 0; i < PingCount; i++)
            {
                if (cancel.IsCancellationRequested)
                {
                    return (null, null);
                }

                var started = Stopwatch.GetTimestamp();
                using (var response = connection.Get("/__down?bytes=0"))
                {
                    response.CopyTo(Stream.Null);
                }

                var ms = (Stopwatch.GetTimestamp() - started) * 1000.0 / Stopwatch.Frequency;
                samples.Add(ms);
                progress("ping", ms, (i + 1) / (double)PingCount);
            }
        }
        catch (Exception e) when (e is IOException or HttpRequestException
                                      or OperationCanceledException or SocketException)
        {
            throw new SpeedTestError($"Can't reach the speed test server ({e.GetType().Name})", e);
        }

        return PingStats(samples);
    }

    private delegate void PhaseWorker(Meter meter, CancellationToken stop, ConcurrentBag<Exception> errors);

    private static double? TimedPhase(
        PhaseWorker worker,
        int streams,
        double seconds,
        long cap,
        string phase,
        Action<string, double?, double> progress,
        CancellationToken cancel)
    {
        var meter = new Meter();
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancel);
        var errors = new ConcurrentBag<Exception>();

        var tasks = new Task[streams];
        for (var i = 0; i < streams; i++)
        {
            tasks[i] = Task.Run(() =>
            {
                try
                {
                    worker(meter, stop.Token, errors);
                }
                catch (Exception e)
                {
                    if (!stop.IsCancellationRequested)
                    {
                        errors.Add(e);
                    }
                }
            });
        }

        var end = Now() + seconds;
        while (Now() < end && !cancel.IsCancellationRequested)
        {
            if (errors.Count == streams || meter.Total >= cap)
            {
                break;
            }

            Thread.Sleep(150);
            progress(phase, meter.Sample(), 1 - (end - Now()) / seconds);
        }

        stop.Cancel();

        // Give the workers a moment to unwind; the Python version lets its daemon threads die.
        try
        {
            Task.WaitAll(tasks, TimeSpan.FromSeconds(2));
        }
        catch (AggregateException)
        {
            // Already recorded in errors (or a cancellation).
        }

        if (cancel.IsCancellationRequested)
        {
            return null;
        }

        if (meter.Total == 0)
        {
            var reason = errors.TryPeek(out var first) ? first.GetType().Name : "no data";
            throw new SpeedTestError($"{phase} failed: {reason}");
        }

        return meter.Result();
    }

    private static void DownloadWorker(
        ISpeedTestTransport transport,
        Meter meter,
        CancellationToken stop,
        ConcurrentBag<Exception> errors)
    {
        try
        {
            using var connection = transport.Connect();
            var buffer = new byte[65536];
            while (!stop.IsCancellationRequested)
            {
                using var body = connection.Get($"/__down?bytes={DownChunk}");
                while (!stop.IsCancellationRequested)
                {
                    var read = body.Read(buffer, 0, buffer.Length);
                    if (read <= 0)
                    {
                        break;
                    }

                    meter.Add(read);
                }
            }
        }
        catch (Exception e) when (!stop.IsCancellationRequested)
        {
            errors.Add(e);
        }
    }

    private static void UploadWorker(
        ISpeedTestTransport transport,
        Meter meter,
        CancellationToken stop,
        ConcurrentBag<Exception> errors)
    {
        try
        {
            using var connection = transport.Connect();
            while (!stop.IsCancellationRequested)
            {
                connection.Post("/__up", UpChunk, meter.Add, stop);
            }
        }
        catch (Exception e) when (!stop.IsCancellationRequested)
        {
            errors.Add(e);
        }
    }

    // ---------- plain words

    public static string SpeedVerdict(double? down)
    {
        if (down is null)
        {
            return "";
        }

        if (down >= 100)
        {
            return "Great: 4K on several screens and fast downloads";
        }

        if (down >= 25)
        {
            return "Good for 4K or HD streaming and video calls";
        }

        return down >= 8
            ? "OK for browsing and HD video on one screen"
            : "Slow: pages and video will lag";
    }

    public static string PingVerdict(double? ping)
    {
        if (ping is null)
        {
            return "";
        }

        if (ping < 30)
        {
            return "great for gaming";
        }

        if (ping < 60)
        {
            return "fine for gaming and calls";
        }

        return ping < 100 ? "OK, games may feel laggy" : "high: calls and games will lag";
    }

    internal static double Now() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0;

    /// <summary>
    /// Counts bytes from several threads; gives the live rate and the rate after warm-up.
    /// </summary>
    private sealed class Meter
    {
        private readonly object _lock = new();
        private readonly List<(double Time, long Total)> _marks = new();
        private long _total;
        private readonly double _t0 = Now();
        private (double Time, long Total)? _warm;

        public long Total
        {
            get
            {
                lock (_lock)
                {
                    return _total;
                }
            }
        }

        public void Add(int bytes)
        {
            lock (_lock)
            {
                _total += bytes;
            }
        }

        public double Sample()
        {
            var now = Now();
            var total = Total;
            _marks.Add((now, total));
            if (_warm is null && now - _t0 >= WarmupSeconds)
            {
                _warm = (now, total);
            }

            // live rate over the last ~1 s
            var oldest = _marks[0];
            foreach (var mark in _marks)
            {
                if (now - mark.Time <= 1.0)
                {
                    oldest = mark;
                    break;
                }
            }

            return Mbps(total - oldest.Total, now - oldest.Time);
        }

        public double Result()
        {
            var now = Now();
            var total = Total;
            var (startTime, startTotal) = _warm ?? (_t0, 0L);
            return Mbps(total - startTotal, now - startTime);
        }
    }
}

/// <summary>The real speed test transport: normal TLS to speed.cloudflare.com.</summary>
public sealed class CloudflareSpeedTestTransport : ISpeedTestTransport
{
    private static readonly HttpClient Http = new()
    {
        Timeout = TimeSpan.FromSeconds(SpeedTest.TimeoutSeconds),
    };

    public ISpeedTestConnection Connect() => new CloudflareSpeedTestConnection();

    private sealed class CloudflareSpeedTestConnection : ISpeedTestConnection
    {
        private readonly string _base = $"https://{SpeedTest.Host}";

        public Stream Get(string path)
        {
            var request = new HttpRequestMessage(HttpMethod.Get, _base + path);
            var response = Http.Send(request, HttpCompletionOption.ResponseHeadersRead);
            if (!response.IsSuccessStatusCode)
            {
                var code = (int)response.StatusCode;
                response.Dispose();
                throw new HttpRequestException(
                    $"speed test server replied HTTP {code}",
                    null,
                    (HttpStatusCode)code);
            }

            return new ResponseStream(response, response.Content.ReadAsStream());
        }

        public void Post(string path, long byteCount, Action<int> onSent, CancellationToken cancel)
        {
            using var content = new RandomBlockContent(byteCount, onSent, cancel);
            using var response = Http.PostAsync(_base + path, content, cancel).GetAwaiter().GetResult();
            response.EnsureSuccessStatusCode();
            using var body = response.Content.ReadAsStream();
            body.CopyTo(Stream.Null);
        }

        public void Dispose()
        {
        }
    }

    /// <summary>A response body stream that also disposes its HTTP response.</summary>
    private sealed class ResponseStream : Stream
    {
        private readonly HttpResponseMessage _response;
        private readonly Stream _inner;

        public ResponseStream(HttpResponseMessage response, Stream inner)
        {
            _response = response;
            _inner = inner;
        }

        public override bool CanRead => _inner.CanRead;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush() => _inner.Flush();

        public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, count);

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _inner.Dispose();
                _response.Dispose();
            }

            base.Dispose(disposing);
        }
    }

    /// <summary>Random bytes so nothing on the way can compress the upload.</summary>
    private sealed class RandomBlockContent : HttpContent
    {
        private readonly long _length;
        private readonly Action<int> _onSent;
        private readonly CancellationToken _cancel;

        public RandomBlockContent(long length, Action<int> onSent, CancellationToken cancel)
        {
            _length = length;
            _onSent = onSent;
            _cancel = cancel;
            Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        }

        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context)
        {
            var block = new byte[65536];
            Random.Shared.NextBytes(block);

            long sent = 0;
            while (sent < _length)
            {
                _cancel.ThrowIfCancellationRequested();
                var count = (int)Math.Min(block.Length, _length - sent);
                stream.Write(block, 0, count);
                sent += count;
                _onSent(count);
            }

            return Task.CompletedTask;
        }

        protected override bool TryComputeLength(out long length)
        {
            length = _length;
            return true;
        }
    }
}
