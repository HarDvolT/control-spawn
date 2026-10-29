using System.Net.NetworkInformation;

namespace Prism.Router;

/// <summary>This PC's network adapter byte counters. Injectable so tests never read real adapters.</summary>
public interface ILocalTrafficSource
{
    /// <summary>Total bytes received/sent since boot, or null when counters are unavailable.</summary>
    (long Received, long Sent)? Read();
}

/// <summary>
/// Per-application internet activity. Windows cannot report per-app bytes without admin rights,
/// so an implementation reports socket I/O operations (and connection counts) per PID; the
/// detective turns them into shares of this PC's exact total.
/// </summary>
public interface IAppActivitySampler
{
    /// <summary>False when this platform / build has no per-app source yet.</summary>
    bool Available { get; }

    /// <summary>One snapshot keyed by process id; empty when unavailable.</summary>
    IReadOnlyDictionary<int, ProcActivity> Sample();
}

/// <summary>Adapter byte counters via the .NET network interfaces API (no admin, no WMI).</summary>
public sealed class WindowsTrafficSource : ILocalTrafficSource
{
    public (long Received, long Sent)? Read()
    {
        try
        {
            long received = 0;
            long sent = 0;
            var any = false;
            foreach (var adapter in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (adapter.OperationalStatus != OperationalStatus.Up
                    || adapter.NetworkInterfaceType == NetworkInterfaceType.Loopback)
                {
                    continue;
                }

                var stats = adapter.GetIPStatistics();
                received += stats.BytesReceived;
                sent += stats.BytesSent;
                any = true;
            }

            return any ? (received, sent) : null;
        }
        catch (Exception e) when (e is NetworkInformationException or PlatformNotSupportedException)
        {
            return null;
        }
    }
}

/// <summary>
/// Placeholder for the per-app sampler. The Python reference used psutil; the native version
/// needs GetExtendedTcpTable / performance counters (see docs/STATUS-opencode.md), so for now
/// attribution reports "not available" instead of guessing.
/// </summary>
public sealed class UnavailableAppActivitySampler : IAppActivitySampler
{
    public bool Available => false;

    public IReadOnlyDictionary<int, ProcActivity> Sample() =>
        new Dictionary<int, ProcActivity>();
}

/// <summary>
/// Samples this PC's traffic every second and (when an app sampler exists) per-app socket
/// activity every 2 s. Only processes that own internet connections are looked at.
/// </summary>
public sealed class SpikeSampler : IDisposable
{
    private const double IntervalSeconds = 1.0;

    private readonly Evidence _evidence;
    private readonly ILocalTrafficSource _traffic;
    private readonly IAppActivitySampler _apps;
    private readonly ManualResetEventSlim _stopped = new(false);
    private readonly ManualResetEventSlim _paused = new(false);
    private Thread? _thread;

    public SpikeSampler(
        Evidence evidence,
        ILocalTrafficSource? traffic = null,
        IAppActivitySampler? apps = null)
    {
        _evidence = evidence;
        _traffic = traffic ?? new WindowsTrafficSource();
        _apps = apps ?? new UnavailableAppActivitySampler();
    }

    public bool Available => _apps.Available;

    public void Start()
    {
        if (_thread is not null)
        {
            return;
        }

        _thread = new Thread(Loop)
        {
            IsBackground = true,
            Name = "Prism spike sampler",
        };
        _thread.Start();
    }

    private void Loop()
    {
        (double Time, long Received, long Sent)? last = null;
        var tick = 0;
        while (!_stopped.IsSet)
        {
            var started = Now();
            if (!_paused.IsSet)
            {
                try
                {
                    var counters = _traffic.Read();
                    if (counters is not null)
                    {
                        var (received, sent) = counters.Value;
                        if (last is not null && started > last.Value.Time)
                        {
                            var dt = started - last.Value.Time;
                            _evidence.AddPc(
                                started,
                                (received - last.Value.Received) * 8 / 1e6 / dt,
                                (sent - last.Value.Sent) * 8 / 1e6 / dt);
                        }

                        last = (started, received, sent);
                    }

                    if (tick % 2 == 0 && _apps.Available)
                    {
                        _evidence.AddProcs(started, _apps.Sample());
                    }
                }
                catch (Exception)
                {
                    // Sampling is best effort; the spike detector keeps working without it.
                }
            }

            tick++;
            var wait = Math.Max(0.05, IntervalSeconds - (Now() - started));
            _stopped.Wait(TimeSpan.FromSeconds(wait));
        }
    }

    public void Pause() => _paused.Set();

    public void Resume() => _paused.Reset();

    public void Stop() => _stopped.Set();

    public void Dispose()
    {
        Stop();
        _stopped.Dispose();
        _paused.Dispose();
    }

    private static double Now() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0;
}
