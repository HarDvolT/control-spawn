using Prism.Core;
using Prism.Core.Ipc;
using Prism.Hardware;

namespace Prism.Service;

public static class StateAssembler
{
    public static StateMessage Assemble(HardwareSnapshot snapshot, ServiceSettings settings, IReadOnlyList<int> controls,
        IReadOnlyList<RgbDeviceState> devices, LampState? lamp, RouterState? router, ServiceHealth health)
    {
        var sensors = snapshot.Sensors;
        double? Pick(SensorCategory category, string kind, string name) => sensors.FirstOrDefault(s => s.Category == category && s.Kind == kind && s.Name.Contains(name, StringComparison.OrdinalIgnoreCase))?.Value;
        double? Id(string id) => sensors.FirstOrDefault(s => s.Id == id)?.Value;
        double? Sum(SensorCategory category, string kind, string name, double divisor)
        {
            var found = sensors.Where(s => s.Category == category && s.Kind == kind && s.Name.Contains(name, StringComparison.OrdinalIgnoreCase) && s.Value.HasValue).ToArray();
            return found.Length == 0 ? null : found.Sum(s => s.Value) / divisor;
        }
        var system = new SystemState(
            Pick(SensorCategory.Cpu, "Load", "Total"), sensors.Where(s => s.Category == SensorCategory.Cpu && s.Kind == "Load" && s.Name.Contains("Core", StringComparison.OrdinalIgnoreCase)).Select(s => s.Value).ToArray(),
            Pick(SensorCategory.Cpu, "Clock", "Core"), snapshot.Temperature(SensorCategory.Cpu), Pick(SensorCategory.Cpu, "Power", "Package"),
            Pick(SensorCategory.Gpu, "Load", "Core"), Pick(SensorCategory.Gpu, "Temperature", "Core"), Pick(SensorCategory.Gpu, "Temperature", "Hot"),
            Pick(SensorCategory.Gpu, "Clock", "Core"), Pick(SensorCategory.Gpu, "Power", "Package") ?? Pick(SensorCategory.Gpu, "Power", "Total"), Pick(SensorCategory.Gpu, "Fan", ""),
            Pick(SensorCategory.Gpu, "SmallData", "Memory Used") / 1024, Pick(SensorCategory.Gpu, "SmallData", "Memory Total") / 1024,
            Id("/ram/data/0"), Id("prism/ram/total"), sensors.Where(s => s.Category == SensorCategory.Memory && s.Kind == "Temperature").Select(s => s.Value).ToArray(),
            snapshot.Temperature(SensorCategory.Motherboard),
            sensors.Where(s => s.Category == SensorCategory.Storage).GroupBy(s => DeviceId(s.Id)).Select(g => new DiskState(
                g.First().Name.Split(':')[0], g.FirstOrDefault(s => s.Kind == "Temperature")?.Value,
                g.FirstOrDefault(s => s.Kind == "Throughput" && s.Name.Contains("Read"))?.Value / 1_000_000,
                g.FirstOrDefault(s => s.Kind == "Throughput" && s.Name.Contains("Write"))?.Value / 1_000_000,
                g.FirstOrDefault(s => s.Kind == "Load")?.Value)).ToArray(),
            Sum(SensorCategory.Network, "Throughput", "Download", 125000), Sum(SensorCategory.Network, "Throughput", "Upload", 125000));
        var fans = FanMap.Discover(sensors, controls).Select(f => new FanState(f.Header, f.Name,
            sensors.FirstOrDefault(s => s.Category == SensorCategory.Motherboard && s.Kind == "Fan" && s.Id.EndsWith($"/fan/{f.Header - 1}"))?.Value,
            sensors.FirstOrDefault(s => s.Category == SensorCategory.Motherboard && s.Kind == "Control" && s.Id.EndsWith($"/control/{f.Header - 1}"))?.Value,
            f.CanControl, f.IsPump, f.IsPump ? "Fixed 100%" : settings.Fans.GetValueOrDefault(f.Name)?.Mode.ToString() ?? "BIOS")).ToArray();
        return new(snapshot.Timestamp, system, fans, settings.Fans["Radiator"].Mode,
            new(settings.Lighting.Effect, settings.Lighting.Color, settings.Lighting.Brightness, settings.Lighting.Speed, devices), lamp, router, health);
    }
    private static string DeviceId(string id)
    {
        var index = id.LastIndexOf('/');
        if (index <= 0) return id;
        var kindIndex = id.LastIndexOf('/', index - 1);
        return kindIndex < 0 ? id : id[..kindIndex];
    }
}
