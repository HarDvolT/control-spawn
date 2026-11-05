namespace ControlSpawn.Core;

public interface IHardwareMonitor : IAsyncDisposable
{
    event EventHandler<HardwareSnapshot>? Snapshot;
    Task RunAsync(CancellationToken cancellationToken);
}
public interface IFanController : IDisposable
{
    void Apply(HardwareSnapshot snapshot, TimeSpan elapsed);
    void RestoreDefaults();
}
public interface IRgbBackend : IAsyncDisposable
{
    Task SetColorsAsync(uint controller, IReadOnlyList<RgbColor> colors, CancellationToken cancellationToken = default);
}
public interface ILampClient : IAsyncDisposable
{
    bool IsMusicMode { get; }
    Task SetColorAsync(RgbColor color, CancellationToken cancellationToken = default);
    Task SetPowerAsync(bool on, CancellationToken cancellationToken = default);
}
public interface IClock { TimeSpan Elapsed { get; } Task DelayAsync(TimeSpan duration, CancellationToken cancellationToken); }
public sealed class SystemClock : IClock
{
    private readonly System.Diagnostics.Stopwatch watch = System.Diagnostics.Stopwatch.StartNew();
    public TimeSpan Elapsed => watch.Elapsed;
    public Task DelayAsync(TimeSpan duration, CancellationToken cancellationToken) => Task.Delay(duration, cancellationToken);
}
