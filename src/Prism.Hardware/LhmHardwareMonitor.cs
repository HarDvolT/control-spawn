using LibreHardwareMonitor.Hardware;
using Prism.Core;

namespace Prism.Hardware;

public interface ISensorSource : IDisposable
{
    IReadOnlyList<SensorReading> Read();
}

/// <summary>Opening LHM is explicit and occurs only on the first Read, never in a constructor.</summary>
public sealed class LhmSensorSource : ISensorSource, IFanControlPort
{
    /// <summary>
    /// GPU sensor reads share the card's I2C/ADL bus with OpenRGB's lighting writes. Set PRISM_NO_GPU_SENSORS=1
    /// (the service's --no-gpu-sensors flag) to skip them, e.g. to check whether reads disturb the GPU LEDs.
    /// </summary>
    public static bool GpuSensorsEnabled => Environment.GetEnvironmentVariable("PRISM_NO_GPU_SENSORS") != "1";
    private readonly object gate = new();
    private Computer? computer;
    private readonly Dictionary<int, IControl> controls = new();
    private readonly Dictionary<IHardware, DateTime> dimmReads = new();
    private bool disposed;
    /// <summary>
    /// Each DIMM temperature read holds the SMBus and the RAM lighting waits ~200 ms for it, which showed as a step in
    /// fast RAM effects (live, 2026-11-02). DIMM temperatures change slowly, so they are read this often at most.
    /// </summary>
    public static readonly TimeSpan DimmReadEvery = TimeSpan.FromSeconds(10);
    public IReadOnlyList<int> Headers { get { lock (gate) return controls.Keys.Order().ToArray(); } }
    public IReadOnlyList<SensorReading> Read()
    {
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (computer is null)
            {
                computer = new Computer { IsCpuEnabled = true, IsGpuEnabled = GpuSensorsEnabled, IsMemoryEnabled = true,
                    IsMotherboardEnabled = true, IsStorageEnabled = true, IsNetworkEnabled = true };
                try { computer.Open(); }
                catch
                {
                    try { computer.Close(); } finally { computer = null; }
                    throw;
                }
            }
            var readings = new List<SensorReading>();
            foreach (var hardware in computer.Hardware) Visit(hardware, readings);
            return SensorProjection.WithMemoryTotal(readings);
        }
    }
    private void Visit(IHardware hardware, List<SensorReading> readings)
    {
        var type = hardware.HardwareType.ToString();
        if (type is "GpuAmd" or "GpuNvidia" or "GpuIntel") BusTurns.Gpu.Read(hardware.Update);
        else if (type == "Memory") ReadMemory(hardware);
        else hardware.Update();
        var category = hardware.HardwareType.ToString() switch
        {
            "Cpu" => SensorCategory.Cpu,
            "GpuAmd" or "GpuNvidia" or "GpuIntel" => SensorCategory.Gpu,
            "Memory" => SensorCategory.Memory,
            "Motherboard" or "SuperIO" => SensorCategory.Motherboard,
            "Storage" => SensorCategory.Storage,
            "Network" => SensorCategory.Network,
            _ => SensorCategory.Other
        };
        foreach (var sensor in hardware.Sensors)
        {
            // Only the verified motherboard chip can be controlled; never expose GPU controls.
            if (hardware.HardwareType.ToString() == "SuperIO" &&
                hardware.Name.Contains("NCT6701D", StringComparison.OrdinalIgnoreCase) &&
                sensor.SensorType == SensorType.Control && sensor.Control is not null && sensor.Index is >= 0 and < 7)
                controls[sensor.Index + 1] = sensor.Control;
            readings.Add(new(sensor.Identifier.ToString(), $"{hardware.Name}: {sensor.Name}", category,
                sensor.SensorType.ToString(), Unit(sensor.SensorType.ToString()),
                sensor.Value is float value && float.IsFinite(value) ? value : null));
        }
        foreach (var child in hardware.SubHardware) Visit(child, readings);
    }
    // Generic memory (load, used GiB) comes from Windows and updates every snapshot; DIMMs with SPD temperature sensors
    // share the SMBus with the RAM lighting, so they take a bus turn and only every DimmReadEvery.
    private void ReadMemory(IHardware hardware)
    {
        if (!hardware.Identifier.ToString().Contains("/dimm/", StringComparison.OrdinalIgnoreCase)) { hardware.Update(); return; }
        var now = DateTime.UtcNow;
        if (dimmReads.TryGetValue(hardware, out var last) && now - last < DimmReadEvery) return;
        dimmReads[hardware] = now;
        BusTurns.Smbus.Read(hardware.Update);
    }
    private static string Unit(string kind) => kind switch
    {
        "Temperature" => "°C", "Load" or "Control" or "Level" => "%", "Clock" => "MHz",
        "Power" => "W", "Fan" => "rpm", "Data" => "GiB", "SmallData" => "MiB",
        "Throughput" => "B/s", "Voltage" => "V", _ => ""
    };
    public void SetSoftware(int header, float percent)
    {
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (!float.IsFinite(percent)) throw new ArgumentOutOfRangeException(nameof(percent));
            controls[header].SetSoftware(header == 7 ? 100 : Math.Clamp(percent, 0, 100));
        }
    }
    public void SetDefault(int header) { lock (gate) controls[header].SetDefault(); }
    public void Dispose()
    {
        lock (gate)
        {
            if (disposed) return;
            foreach (var control in controls.Values)
                try { control.SetDefault(); } catch (Exception ex) { PrismLog.Failure("fans", ex); }
            try { computer?.Close(); } finally { disposed = true; controls.Clear(); }
        }
    }
}

public static class SensorProjection
{
    public static IReadOnlyList<SensorReading> WithMemoryTotal(IReadOnlyList<SensorReading> readings)
    {
        // LHM 0.9.6 TotalMemory exposes used + available, rather than a total sensor.
        double? used = readings.FirstOrDefault(s => s.Id == "/ram/data/0")?.Value;
        double? available = readings.FirstOrDefault(s => s.Id == "/ram/data/1")?.Value;
        var result = readings.ToList();
        result.Add(new("controlspawn/ram/total", "Total Memory: Memory Total", SensorCategory.Memory, "Data", "GiB",
            used is not null && available is not null && double.IsFinite(used.Value) && double.IsFinite(available.Value) ? used + available : null));
        return result.AsReadOnly();
    }
}

public sealed class LhmHardwareMonitor(ISensorSource source) : IHardwareMonitor
{
    private readonly CancellationTokenSource stop = new();
    private Task? running;
    public event EventHandler<HardwareSnapshot>? Snapshot;
    public Task RunAsync(CancellationToken cancellationToken)
    {
        if (running is not null) throw new InvalidOperationException("Monitor already started.");
        return running = PollAsync(cancellationToken);
    }
    private async Task PollAsync(CancellationToken token)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, stop.Token);
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        try
        {
            do
            {
                linked.Token.ThrowIfCancellationRequested();
                HardwareSnapshot snapshot;
                try { snapshot = new(DateTimeOffset.UtcNow, source.Read()); }
                catch (Exception ex) { snapshot = new(DateTimeOffset.UtcNow, Array.Empty<SensorReading>(), false, ex.Message); }
                Snapshot?.Invoke(this, snapshot);
            } while (await timer.WaitForNextTickAsync(linked.Token).ConfigureAwait(false));
        }
        catch (OperationCanceledException) when (linked.IsCancellationRequested) { }
        finally { source.Dispose(); }
    }
    public async ValueTask DisposeAsync()
    {
        stop.Cancel();
        if (running is not null) await running.ConfigureAwait(false);
        else source.Dispose();
        stop.Dispose();
    }
}

public static class FanMap
{
    public static IReadOnlyList<FanGroup> Discover(IReadOnlyList<SensorReading> readings, IReadOnlyList<int> controls)
    {
        var groups = new List<FanGroup>();
        for (int n = 1; n <= 7; n++)
        {
            // LHM identifiers carry zero-based sensor indices; sensor names can be user-edited.
            double rpm = readings.Where(s => s.Category == SensorCategory.Motherboard && s.Kind == "Fan" &&
                s.Id.EndsWith($"/fan/{n - 1}", StringComparison.Ordinal)).Select(s => s.Value ?? 0).DefaultIfEmpty().Max();
            if (n is 2 or 6 or 7 || rpm > 0)
                groups.Add(new(n, n switch { 2 => "Radiator", 6 => "Case fans", 7 => "AIO pump", _ => $"Fan header {n}" },
                    rpm, n != 7 && controls.Contains(n), n == 7));
        }
        return groups.AsReadOnly();
    }
}
