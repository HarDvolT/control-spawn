using Prism.Core;

namespace Prism.Hardware;

public interface IFanControlPort
{
    IReadOnlyList<int> Headers { get; }
    void SetSoftware(int header, float percent);
    void SetDefault(int header);
}
public sealed class FanSafety
{
    public int ConsecutiveFailures { get; private set; }
    public bool Observe(HardwareSnapshot snapshot, bool requiredSensorsPresent = true)
    {
        ConsecutiveFailures = snapshot.Success && requiredSensorsPresent ? 0 : Math.Min(3, ConsecutiveFailures + 1);
        return ConsecutiveFailures >= 3 || snapshot.Sensors.Any(s =>
            (s.Category is SensorCategory.Cpu or SensorCategory.Gpu) && s.Kind == "Temperature" && s.Value >= LimitFor(s));
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
                    port.SetSoftware(header, (float)percent);
                }
            }
            catch { RestoreDefaults(); throw; }
        }
    }
    public void RestoreDefaults()
    {
        lock (gate)
        {
            // Attempt every control even when one fails. Cleanup must never hide the original exception.
            foreach (int header in port.Headers.ToArray())
                try { port.SetDefault(header); } catch (Exception ex) { System.Diagnostics.Trace.TraceError(ex.ToString()); }
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
