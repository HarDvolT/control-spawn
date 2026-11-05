using ControlSpawn.Core;

namespace ControlSpawn.Hardware;

public interface IFanControlPort
{
    IReadOnlyList<int> Headers { get; }
    void SetSoftware(int header, float percent);
    void SetDefault(int header);
}
public sealed class FanSafety
{
    public int ConsecutiveFailures { get; private set; }
    /// <summary>Why the last check sent the fans to full speed, in words; null when all is well.</summary>
    public string? Reason { get; private set; }
    public bool Observe(HardwareSnapshot snapshot, bool requiredSensorsPresent = true)
    {
        ConsecutiveFailures = snapshot.Success && requiredSensorsPresent ? 0 : Math.Min(3, ConsecutiveFailures + 1);
        var hot = snapshot.Sensors.FirstOrDefault(s =>
            (s.Category is SensorCategory.Cpu or SensorCategory.Gpu) && s.Kind == "Temperature" && s.Value >= LimitFor(s));
        Reason = hot is not null ? $"{hot.Name} reached {hot.Value:0} °C" : ConsecutiveFailures >= 3 ? "Temperature sensors stopped answering" : null;
        return Reason is not null;
    }

    // GPU hotspot and memory junction normally run past 90 °C under gaming load on an RX 9070 XT,
    // so they get their own limit instead of forcing every fan to 100% during games.
    private static double LimitFor(SensorReading s) =>
        s.Category == SensorCategory.Gpu &&
        (s.Name.Contains("Hot Spot", StringComparison.OrdinalIgnoreCase) ||
         s.Name.Contains("Hotspot", StringComparison.OrdinalIgnoreCase) ||
         s.Name.Contains("Junction", StringComparison.OrdinalIgnoreCase))
            ? 105 : 90;
}
public sealed class FanController : IFanController
{
    private readonly IFanControlPort port;
    private readonly object gate = new();
    private readonly FanSafety safety = new();
    private readonly Dictionary<int, FanCurveEngine> engines = new();
    private readonly Dictionary<int, FanSettings> settings = new();
    private readonly Dictionary<int, double> overrides = new();
    private bool disposed;
    public FanController(IFanControlPort port)
    {
        this.port = port;
        AppDomain.CurrentDomain.ProcessExit += OnExit;
        AppDomain.CurrentDomain.UnhandledException += OnUnhandled;
    }
    public void Configure(int header, FanSettings value)
    {
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (header == 7) throw new InvalidOperationException("The AIO pump is fixed at 100%.");
            if (!port.Headers.Contains(header)) throw new ArgumentOutOfRangeException(nameof(header));
            settings[header] = value;
        }
    }
    public void Apply(HardwareSnapshot snapshot, TimeSpan elapsed)
    {
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            var headers = port.Headers.ToArray();
            bool valid = snapshot.Temperature(SensorCategory.Cpu).HasValue &&
                settings.Values.All(s => FanCurveEngine.SelectTemperature(snapshot, s.Source).HasValue);
            bool emergency = safety.Observe(snapshot, valid);
            try
            {
                foreach (int header in headers)
                {
                    if (!engines.TryGetValue(header, out var engine)) engines[header] = engine = new();
                    var config = settings.GetValueOrDefault(header) ?? new(FanCurveEngine.Preset(FanMode.Standard));
                    double? temp = FanCurveEngine.SelectTemperature(snapshot, config.Source);
                    double percent = header == 7 || emergency || temp is null || !snapshot.Success
                        ? engine.ForceFull() : engine.Evaluate(temp.Value, config, elapsed);
                    // Measuring holds one fan at a fixed duty; overheating still wins (emergency is checked above).
                    if (!emergency && header != 7 && overrides.TryGetValue(header, out var held)) percent = held;
                    port.SetSoftware(header, (float)percent);
                }
            }
            catch { RestoreDefaults(); throw; }
        }
    }
    /// <summary>The curve's target and the averaged temperature it read on the last tick.</summary>
    public (double? Target, double? Following) Status(int header)
    {
        lock (gate) return engines.TryGetValue(header, out var engine) ? (engine.Target, engine.Following) : (null, null);
    }
    /// <summary>The lowest duty a hold may set: Measure's last step. Holds skip the curve minimum, so nothing may hold a fan stopped.</summary>
    public const double MinimumHold = 15;
    /// <summary>Holds a fan at a fixed duty (null releases it). The pump can't be held and safety full speed still applies.</summary>
    public void Hold(int header, double? percent)
    {
        lock (gate)
        {
            if (header == 7) throw new InvalidOperationException("The AIO pump is fixed at 100%.");
            if (percent is { } p && !double.IsFinite(p)) throw new ArgumentOutOfRangeException(nameof(percent));
            if (percent is { } held) overrides[header] = Math.Clamp(held, MinimumHold, 100); else overrides.Remove(header);
        }
    }
    public void RestoreDefaults()
    {
        lock (gate)
        {
            // Attempt every control even when one fails. Cleanup must never hide the original exception.
            foreach (int header in port.Headers.ToArray())
                try { port.SetDefault(header); } catch (Exception ex) { ControlSpawnLog.Failure("fans", ex); }
        }
    }
    private void OnExit(object? sender, EventArgs e) => RestoreDefaults();
    private void OnUnhandled(object sender, UnhandledExceptionEventArgs e) => RestoreDefaults();
    public void Dispose()
    {
        lock (gate)
        {
            if (disposed) return;
            RestoreDefaults(); disposed = true;
            AppDomain.CurrentDomain.ProcessExit -= OnExit;
            AppDomain.CurrentDomain.UnhandledException -= OnUnhandled;
        }
    }
}
