using Prism.Core;

namespace Prism.Service;

/// <summary>
/// One device's frame failing must not stop the others. Unexpected errors used to escape the lighting loop, which the
/// module then restarted with back-off up to 30 s while every device froze. A failing device now keeps its last frame
/// and is logged at most once a minute; the loop and the OpenRGB connection carry on.
/// </summary>
public sealed class FrameGuard(Action<string, Exception>? log = null)
{
    public static readonly TimeSpan LogEvery = TimeSpan.FromMinutes(1);
    private readonly Action<string, Exception> log = log ?? ((_, e) => PrismLog.Failure("rgb", e));
    private readonly Dictionary<string, TimeSpan> lastLogged = new();
    public int Failures { get; private set; }
    public bool TryRun<T>(string key, Func<T> work, TimeSpan now, out T result)
    {
        try { result = work(); return true; }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            Failures++; result = default!;
            if (!lastLogged.TryGetValue(key, out var last) || now - last >= LogEvery) { lastLogged[key] = now; log(key, e); }
            return false;
        }
    }
}
