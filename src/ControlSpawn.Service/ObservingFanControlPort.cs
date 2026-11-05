using ControlSpawn.Hardware;

namespace ControlSpawn.Service;

/// <summary>
/// Read-only stand-in for the real fan port used by <c>--observe</c>: it exposes the real headers so the
/// curves and state are computed as usual, but never writes, so every fan stays on its BIOS curve.
/// </summary>
public sealed class ObservingFanControlPort(IFanControlPort inner) : IFanControlPort
{
    private readonly Dictionary<int, float> wouldSet = new();
    public IReadOnlyList<int> Headers => inner.Headers;
    public IReadOnlyDictionary<int, float> WouldSet { get { lock (wouldSet) return new Dictionary<int, float>(wouldSet); } }
    public void SetSoftware(int header, float percent) { lock (wouldSet) wouldSet[header] = percent; }
    public void SetDefault(int header) { lock (wouldSet) wouldSet.Remove(header); }
}
