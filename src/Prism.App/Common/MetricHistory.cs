namespace Prism.App.Common;
/// <summary>At most one finite reading per snapshot time, retained for one hour.</summary>
public sealed class MetricHistory
{
    private readonly SortedDictionary<DateTimeOffset,double> _samples = new();
    public void Add(DateTimeOffset timestamp, double? value)
    {
        if (value is not { } reading || !double.IsFinite(reading)) return;
        _samples[timestamp] = reading;
        var cutoff = _samples.Keys.Last() - TimeSpan.FromHours(1);
        foreach (var key in _samples.Keys.TakeWhile(t => t < cutoff).ToArray()) _samples.Remove(key);
        while (_samples.Count > 3601) _samples.Remove(_samples.Keys.First());
    }
    public IReadOnlyList<double> Read(DateTimeOffset now, TimeSpan range) =>
        _samples.Where(s => s.Key >= now - range && s.Key <= now).Select(s => s.Value).ToArray();
}
