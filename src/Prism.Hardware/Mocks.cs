using Prism.Core;

namespace Prism.Hardware;

public sealed class FakeSensorSource : ISensorSource
{
    public IReadOnlyList<SensorReading> Sensors { get; set; } = new[] {
        new SensorReading("mock/cpu/temperature/0", "CPU Tctl", SensorCategory.Cpu, "Temperature", "°C", 45),
        new SensorReading("mock/gpu/temperature/0", "GPU", SensorCategory.Gpu, "Temperature", "°C", 40) };
    public IReadOnlyList<SensorReading> Read() => Sensors;
    public void Dispose() { }
}
public sealed class FakeFanControlPort : IFanControlPort
{
    public IReadOnlyList<int> Headers { get; init; } = Enumerable.Range(1, 7).ToArray();
    public Dictionary<int, float> Speeds { get; } = new();
    public List<int> Restored { get; } = new();
    public void SetSoftware(int header, float percent) => Speeds[header] = percent;
    public void SetDefault(int header) => Restored.Add(header);
}
public sealed class FakeServiceQuery : IServiceQuery
{
    public IReadOnlyList<ServiceState> States { get; init; } = Array.Empty<ServiceState>();
    public IReadOnlyList<ServiceState> Query() => States;
}
public static class HardwareComposition
{
    // Libraries have no executable entry point. The later service host passes its command-line args here.
    public static (ISensorSource Sensors, IFanControlPort Controls, IServiceQuery Services) Create(IEnumerable<string> args)
    {
        if (args.Contains("--mock", StringComparer.OrdinalIgnoreCase))
            return (new FakeSensorSource(), new FakeFanControlPort(), new FakeServiceQuery());
        var lhm = new LhmSensorSource();
        return (lhm, lhm, new WindowsServiceQuery());
    }
}
