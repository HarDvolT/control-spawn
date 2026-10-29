using System.Globalization;
using System.Net.NetworkInformation;
using System.Text.RegularExpressions;

namespace Prism.Router;

/// <summary>One ping reply: when it was taken and the round trip in ms (null = lost).</summary>
public readonly record struct PingSample(double Time, double? Milliseconds);

/// <summary>Window statistics over ping samples.</summary>
public sealed record PingStats(
    int Count,
    double? Loss,
    double? Avg,
    double? Min,
    double? Max,
    double? Jitter);

/// <summary>
/// Live ping monitor: pings an internet host (default 1.1.1.1) once a second with
/// <see cref="System.Net.NetworkInformation.Ping"/>. Never talks to the router's API.
/// </summary>
public sealed class PingMonitor : IDisposable
{
    public const string DefaultTarget = "1.1.1.1";

    public static readonly IReadOnlyList<string> Targets = new[] { "1.1.1.1", "8.8.8.8" };

    public const double IntervalSeconds = 1.0;

    public const double KeepSeconds = 5 * 60;

    // Reply lines carry "TTL=" in every Windows language; the time is "time=12ms", "Zeit=12ms",
    // "temps=12 ms", "tiempo=12ms", or "time<1ms".
    private static readonly Regex TimePattern = new(
        @"[=<]\s*(\d+(?:[.,]\d+)?)\s*ms\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private readonly Func<string, double?> _pinger;
    private readonly List<PingSample> _samples = new();
    private readonly object _lock = new();
    private readonly ManualResetEventSlim _paused = new(false);
    private readonly ManualResetEventSlim _stopped = new(false);
    private Thread? _thread;

    public PingMonitor(string target = DefaultTarget, Func<string, double?>? pinger = null)
    {
        Target = target;
        _pinger = pinger ?? (t => PingOnce(t));
    }

    /// <summary>The host currently pinged. One ping monitor per widget, like the Python one.</summary>
    public string Target { get; private set; }

    /// <summary>Round trip in ms from ping.exe output, or null for a timeout / unreachable.</summary>
    public static double? ParsePing(string? text)
    {
        foreach (var line in (text ?? "").Split('\n'))
        {
            if (!line.ToLowerInvariant().Contains("ttl=", StringComparison.Ordinal))
            {
                continue;
            }

            var match = TimePattern.Match(line);
            if (!match.Success)
            {
                continue;
            }

            var raw = match.Groups[1].Value.Replace(',', '.');
            if (!double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var ms))
            {
                continue;
            }

            return line[match.Index] == '<' ? 0.5 : ms;
        }

        return null;
    }

    /// <summary>One ping via the .NET ICMP API (no ping.exe, no console window).</summary>
    public static double? PingOnce(string target, int timeoutMs = 1000)
    {
        try
        {
            using var ping = new Ping();
            var reply = ping.Send(target, timeoutMs);
            return reply is { Status: IPStatus.Success } ? reply.RoundtripTime : null;
        }
        catch (Exception e) when (e is PingException or NetworkInformationException
                                      or ArgumentException or InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>
    /// Samples are (time, ms or None). Returns count, loss %, and avg/min/max/jitter of replies.
    /// </summary>
    public static PingStats Stats(IEnumerable<PingSample> samples, double now, double windowSeconds)
    {
        var window = samples.Where(sample => now - sample.Time <= windowSeconds).ToList();
        var replies = window.Where(sample => sample.Milliseconds is not null)
            .Select(sample => sample.Milliseconds!.Value)
            .ToList();

        double? jitter = null;
        if (replies.Count > 1)
        {
            var total = 0.0;
            for (var i = 1; i < replies.Count; i++)
            {
                total += Math.Abs(replies[i] - replies[i - 1]);
            }

            jitter = total / (replies.Count - 1);
        }

        return new PingStats(
            Count: window.Count,
            Loss: window.Count > 0 ? 100.0 * (window.Count - replies.Count) / window.Count : null,
            Avg: replies.Count > 0 ? replies.Average() : null,
            Min: replies.Count > 0 ? replies.Min() : null,
            Max: replies.Count > 0 ? replies.Max() : null,
            Jitter: jitter);
    }

    /// <summary>Excellent below 30 ms, Good below 60, Fair below 100, else Poor.</summary>
    public static string? PingRating(double? ms)
    {
        if (ms is null)
        {
            return null;
        }

        if (ms < 30)
        {
            return Ratings.Excellent;
        }

        if (ms < 60)
        {
            return Ratings.Good;
        }

        return ms < 100 ? Ratings.Fair : Ratings.Poor;
    }

    public static readonly IReadOnlyDictionary<string, string> PingPlain = new Dictionary<string, string>
    {
        [Ratings.Excellent] = "Great for gaming and calls",
        [Ratings.Good] = "Fine for calls and most games",
        [Ratings.Fair] = "OK for browsing; games may feel laggy",
        [Ratings.Poor] = "Laggy: calls and games will suffer",
    };

    public const string NoRepliesPlain = "No replies";

    public static string PingPlainText(string? rating)
    {
        if (rating is not null && PingPlain.TryGetValue(rating, out var text))
        {
            return text;
        }

        return NoRepliesPlain;
    }

    public void Start()
    {
        if (_thread is not null)
        {
            return;
        }

        _thread = new Thread(Loop)
        {
            IsBackground = true,
            Name = "Prism ping monitor",
        };
        _thread.Start();
    }

    private void Loop()
    {
        while (!_stopped.IsSet)
        {
            var started = Now();
            if (!_paused.IsSet)
            {
                var ms = _pinger(Target);
                lock (_lock)
                {
                    _samples.Add(new PingSample(started, ms));
                    _samples.RemoveAll(sample => sample.Time < started - KeepSeconds);
                }
            }

            var wait = Math.Max(0.05, IntervalSeconds - (Now() - started));
            _stopped.Wait(TimeSpan.FromSeconds(wait));
        }
    }

    public IReadOnlyList<PingSample> Snapshot()
    {
        lock (_lock)
        {
            return _samples.ToList();
        }
    }

    /// <summary>Records a sample (the live loop does this too; tests seed the history directly).</summary>
    public void AddSample(double time, double? milliseconds)
    {
        lock (_lock)
        {
            _samples.Add(new PingSample(time, milliseconds));
        }
    }

    public void SetTarget(string target)
    {
        Target = target;
        lock (_lock)
        {
            _samples.Clear();
        }
    }

    public void Pause() => _paused.Set();

    public void Resume() => _paused.Reset();

    public bool IsPaused => _paused.IsSet;

    public void Stop() => _stopped.Set();

    public void Dispose()
    {
        Stop();
        _stopped.Dispose();
        _paused.Dispose();
    }

    private static double Now() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0;
}
