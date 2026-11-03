namespace Prism.App.Common;
/// <summary>At most one finite reading per snapshot time, retained for one hour.</summary>
/// <remarks>Samples arrive in time order, so adding one is constant time; an hour of samples is about 3600 of them.</remarks>
public sealed class MetricHistory
{
    private readonly List<(DateTimeOffset Time, double Value)> _samples = new();
    /// <summary>Time of the newest sample, so callers can skip history they already gave.</summary>
    public DateTimeOffset? Latest => _samples.Count == 0 ? null : _samples[^1].Time;
    public void Add(DateTimeOffset timestamp, double? value)
    {
        if (value is not { } reading || !double.IsFinite(reading)) return;
        if (_samples.Count == 0 || timestamp > _samples[^1].Time) _samples.Add((timestamp, reading));
        else
        {
            int index = IndexAtOrAfter(timestamp);
            if (index < _samples.Count && _samples[index].Time == timestamp) _samples[index] = (timestamp, reading);
            else _samples.Insert(index, (timestamp, reading));
        }
        int stale = IndexAtOrAfter(_samples[^1].Time - TimeSpan.FromHours(1));
        stale = Math.Max(stale, _samples.Count - 3601);
        if (stale > 0) _samples.RemoveRange(0, stale);
    }
    public IReadOnlyList<double> Read(DateTimeOffset now, TimeSpan range)
    {
        var values = new List<double>();
        for (int i = IndexAtOrAfter(now - range); i < _samples.Count && _samples[i].Time <= now; i++) values.Add(_samples[i].Value);
        return values;
    }
    private int IndexAtOrAfter(DateTimeOffset time)
    {
        int low = 0, high = _samples.Count;
        while (low < high) { int mid = (low + high) / 2; if (_samples[mid].Time < time) low = mid + 1; else high = mid; }
        return low;
    }
}
