namespace Prism.Router;

/// <summary>One point of the router's signal history used as spike evidence.</summary>
public readonly record struct SignalPoint(
    double Time,
    double? Sinr,
    double? Rsrp,
    string Network,
    string Pci);

/// <summary>Per-process internet activity from one sampler tick (an estimate, see Spikes).</summary>
public sealed class ProcActivity
{
    public string Exe { get; init; } = "";

    public IReadOnlyList<string> Services { get; init; } = Array.Empty<string>();

    /// <summary>Socket I/O operations since the previous sample, summed for this process.</summary>
    public long Ops { get; init; }

    public int Conns { get; init; }

    public int New { get; init; }
}

/// <summary>A finished ping spike.</summary>
public sealed class SpikeEvent
{
    public double Start { get; set; }

    public double End { get; set; }

    /// <summary>Highest reply in ms; null when every sample was lost.</summary>
    public double? Peak { get; set; }

    public int Lost { get; set; }

    public int Count { get; set; }

    public double? Baseline { get; set; }
}

/// <summary>One ranked explanation for a spike. Kind: app / signal / other_device / isp.</summary>
public sealed class SpikeCause
{
    public string Kind { get; init; } = "";

    public string Name { get; init; } = "";

    public string Detail { get; init; } = "";

    public string Tip { get; init; } = "";

    public string Confidence { get; init; } = "";

    public double? Share { get; init; }

    public double? Mbps { get; init; }

    public int New { get; init; }
}

/// <summary>What <see cref="Spikes.Classify"/> found for one spike.</summary>
public sealed class ClassifyResult
{
    public IReadOnlyList<SpikeCause> Causes { get; init; } = Array.Empty<SpikeCause>();

    public double PcDown { get; init; }

    public double PcUp { get; init; }

    public double? RouterTotal { get; init; }

    public bool HasPcData { get; init; }

    public bool HasProcData { get; init; }
}

/// <summary>Feed it ping samples (t, ms or None); it returns finished spike events.</summary>
public sealed class SpikeDetector
{
    private readonly Queue<(double Time, double Milliseconds)> _normal = new();
    private SpikeEvent? _open;
    private int _calm;

    /// <summary>Median of the replies that were not part of a spike, once there are enough.</summary>
    public double? Baseline()
    {
        if (_normal.Count < Spikes.MinBaseline)
        {
            return null;
        }

        var values = _normal.Select(sample => sample.Milliseconds).OrderBy(ms => ms).ToList();
        var middle = values.Count / 2;
        return values.Count % 2 == 1 ? values[middle] : (values[middle - 1] + values[middle]) / 2.0;
    }

    public double? Threshold()
    {
        var baseline = Baseline();
        return baseline is null
            ? null
            : Math.Max(Spikes.SpikeFactor * baseline.Value, baseline.Value + Spikes.SpikeExtraMs);
    }

    public SpikeEvent? Update(double time, double? milliseconds)
    {
        while (_normal.Count > 0 && _normal.Peek().Time < time - Spikes.BaselineSeconds)
        {
            _normal.Dequeue();
        }

        var limit = Threshold();
        var spiking = limit is not null && (milliseconds is null || milliseconds > limit);

        if (spiking)
        {
            if (_open is null)
            {
                _open = new SpikeEvent
                {
                    Start = time,
                    End = time,
                    Baseline = Baseline(),
                };
            }

            var open = _open;
            open.End = time;
            open.Count++;
            if (milliseconds is null)
            {
                open.Lost++;
            }
            else
            {
                open.Peak = open.Peak is null ? milliseconds : Math.Max(open.Peak.Value, milliseconds.Value);
            }

            _calm = 0;
            return null;
        }

        if (milliseconds is not null)
        {
            _normal.Enqueue((time, milliseconds.Value));
        }

        if (_open is not null)
        {
            _calm++;
            if (_calm >= Spikes.EndAfter)
            {
                var finished = _open;
                _open = null;
                return finished;
            }
        }

        return null;
    }
}

/// <summary>Ring buffers of what was happening; filled by the sampler thread and the poller.</summary>
public sealed class Evidence
{
    private readonly object _lock = new();
    private readonly List<(double Time, double Down, double Up)> _pc = new();
    private readonly List<(double Time, IReadOnlyDictionary<int, ProcActivity> Snapshot)> _procs = new();
    private readonly List<(double Time, double Down, double Up)> _router = new();
    private readonly List<SignalPoint> _signal = new();
    private (double Time, long Rx, long Tx)? _routerLast;

    /// <summary>This PC's traffic samples.</summary>
    public IReadOnlyList<(double Time, double Down, double Up)> Pc
    {
        get
        {
            lock (_lock)
            {
                return _pc.ToList();
            }
        }
    }

    /// <summary>Per-process activity samples.</summary>
    public IReadOnlyList<(double Time, IReadOnlyDictionary<int, ProcActivity> Snapshot)> Procs
    {
        get
        {
            lock (_lock)
            {
                return _procs.ToList();
            }
        }
    }

    /// <summary>The router's traffic samples, derived from its WAN byte counters.</summary>
    public IReadOnlyList<(double Time, double Down, double Up)> Router
    {
        get
        {
            lock (_lock)
            {
                return _router.ToList();
            }
        }
    }

    /// <summary>The router's signal samples.</summary>
    public IReadOnlyList<SignalPoint> Signal
    {
        get
        {
            lock (_lock)
            {
                return _signal.ToList();
            }
        }
    }

    public void AddPc(double time, double downMbps, double upMbps)
    {
        lock (_lock)
        {
            _pc.Add((time, downMbps, upMbps));
            Trim(_pc, row => row.Time, time);
        }
    }

    public void AddProcs(double time, IReadOnlyDictionary<int, ProcActivity> snapshot)
    {
        lock (_lock)
        {
            _procs.Add((time, snapshot));
            Trim(_procs, row => row.Time, time);
        }
    }

    public void AddRouter(double time, double downMbps, double upMbps)
    {
        lock (_lock)
        {
            _router.Add((time, downMbps, upMbps));
            Trim(_router, row => row.Time, time);
        }
    }

    public void AddSignal(SignalPoint point)
    {
        lock (_lock)
        {
            _signal.Add(point);
            Trim(_signal, item => item.Time, point.Time);
        }
    }

    /// <summary>Router's cumulative WAN byte counters (wan_rx_bytes / wan_tx_bytes) -> rates.</summary>
    public void AddRouterCounters(double time, long? rxBytes, long? txBytes)
    {
        if (rxBytes is null || txBytes is null)
        {
            return;
        }

        lock (_lock)
        {
            var last = _routerLast;
            _routerLast = (time, rxBytes.Value, txBytes.Value);
            if (last is null || time <= last.Value.Time
                || rxBytes < last.Value.Rx || txBytes < last.Value.Tx)
            {
                return;
            }

            var dt = time - last.Value.Time;
            _router.Add((
                time,
                (rxBytes.Value - last.Value.Rx) * 8 / 1e6 / dt,
                (txBytes.Value - last.Value.Tx) * 8 / 1e6 / dt));
            Trim(_router, row => row.Time, time);
        }
    }

    public IReadOnlyList<(double Time, double Down, double Up)> PcWindow(double start, double end)
    {
        lock (_lock)
        {
            return _pc.Where(row => row.Time >= start && row.Time <= end).ToList();
        }
    }

    public IReadOnlyList<(double Time, IReadOnlyDictionary<int, ProcActivity> Snapshot)> ProcWindow(
        double start,
        double end)
    {
        lock (_lock)
        {
            return _procs.Where(row => row.Time >= start && row.Time <= end).ToList();
        }
    }

    public IReadOnlyList<(double Time, double Down, double Up)> RouterWindow(double start, double end)
    {
        lock (_lock)
        {
            return _router.Where(row => row.Time >= start && row.Time <= end).ToList();
        }
    }

    public IReadOnlyList<SignalPoint> SignalWindow(double start, double end)
    {
        lock (_lock)
        {
            return _signal.Where(point => point.Time >= start && point.Time <= end).ToList();
        }
    }

    private static void Trim<T>(List<T> buffer, Func<T, double> timeOf, double now)
    {
        buffer.RemoveAll(item => timeOf(item) < now - Spikes.KeepSeconds);
    }
}
