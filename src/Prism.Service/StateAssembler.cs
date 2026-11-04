using Prism.Core;
using Prism.Core.Ipc;
using Prism.Hardware;

namespace Prism.Service;

public static class StateAssembler
{
    public static StateMessage Assemble(HardwareSnapshot snapshot, ServiceSettings settings, IReadOnlyList<int> controls,
        IReadOnlyList<RgbDeviceState> devices, LampState? lamp, RouterState? router, ServiceHealth health)
    {
        // The 9800X3D also has an integrated Radeon GPU (512 MB): read GPU values from the discrete card only,
        // i.e. the GPU device reporting the most dedicated memory.
        var gpuDevice = snapshot.Sensors.Where(s => s.Category == SensorCategory.Gpu).GroupBy(s => DeviceId(s.Id))
            .OrderByDescending(g => g.FirstOrDefault(s => s.Kind == "SmallData" && s.Name.Contains("Memory Total", StringComparison.OrdinalIgnoreCase))?.Value ?? -1)
            .Select(g => g.Key).FirstOrDefault();
        var sensors = snapshot.Sensors.Where(s => s.Category != SensorCategory.Gpu || DeviceId(s.Id) == gpuDevice).ToArray();
        double? Pick(SensorCategory category, string kind, string name) => sensors.FirstOrDefault(s => s.Category == category && s.Kind == kind && s.Name.Contains(name, StringComparison.OrdinalIgnoreCase))?.Value;
        // Per-thread loads are "CPU Core #1 Thread #2" style; "CPU Core Max" is an aggregate, not a core.
        static bool IsCoreLoad(SensorReading s) => s.Category == SensorCategory.Cpu && s.Kind == "Load" &&
            s.Name.Contains("CPU Core #", StringComparison.OrdinalIgnoreCase);
        // DIMM sensors also report resolution and limit values as temperatures; only "DIMM #n" is a reading.
        static bool IsDimmTemp(SensorReading s) => s.Category == SensorCategory.Memory && s.Kind == "Temperature" &&
            s.Name.Contains("DIMM #", StringComparison.OrdinalIgnoreCase);
        double? Id(string id) => sensors.FirstOrDefault(s => s.Id == id)?.Value;
        double? Sum(SensorCategory category, string kind, string name, double divisor)
        {
            var found = sensors.Where(s => s.Category == category && s.Kind == kind && s.Name.Contains(name, StringComparison.OrdinalIgnoreCase) && s.Value.HasValue).ToArray();
            return found.Length == 0 ? null : found.Sum(s => s.Value) / divisor;
        }
        var system = new SystemState(
            Pick(SensorCategory.Cpu, "Load", "Total"), sensors.Where(IsCoreLoad).Select(s => s.Value).ToArray(),
            Pick(SensorCategory.Cpu, "Clock", "Core"), snapshot.Temperature(SensorCategory.Cpu), Pick(SensorCategory.Cpu, "Power", "Package"),
            Pick(SensorCategory.Gpu, "Load", "Core"), Pick(SensorCategory.Gpu, "Temperature", "Core"), Pick(SensorCategory.Gpu, "Temperature", "Hot"),
            Pick(SensorCategory.Gpu, "Clock", "Core"), Pick(SensorCategory.Gpu, "Power", "Package") ?? Pick(SensorCategory.Gpu, "Power", "Total"), Pick(SensorCategory.Gpu, "Fan", ""),
            Pick(SensorCategory.Gpu, "SmallData", "Memory Used") / 1024, Pick(SensorCategory.Gpu, "SmallData", "Memory Total") / 1024,
            Id("/ram/data/0"), Id("controlspawn/ram/total"), sensors.Where(IsDimmTemp).Select(s => s.Value).ToArray(),
            snapshot.Temperature(SensorCategory.Motherboard),
            sensors.Where(s => s.Category == SensorCategory.Storage).GroupBy(s => DeviceId(s.Id)).Select(g => new DiskState(
                g.First().Name.Split(':')[0], g.FirstOrDefault(s => s.Kind == "Temperature")?.Value,
                g.FirstOrDefault(s => s.Kind == "Throughput" && s.Name.Contains("Read"))?.Value / 1_000_000,
                g.FirstOrDefault(s => s.Kind == "Throughput" && s.Name.Contains("Write"))?.Value / 1_000_000,
                g.FirstOrDefault(s => s.Kind == "Load" && s.Name.Contains("Used Space", StringComparison.OrdinalIgnoreCase))?.Value,
                g.FirstOrDefault(s => s.Kind == "Load" && (s.Name.Contains("Activity", StringComparison.OrdinalIgnoreCase) || s.Name.Contains("Total", StringComparison.OrdinalIgnoreCase)))?.Value)).ToArray(),
            Sum(SensorCategory.Network, "Throughput", "Download", 125000), Sum(SensorCategory.Network, "Throughput", "Upload", 125000), Pick(SensorCategory.Gpu, "Temperature", "Memory"));
        var throttle = sensors.Where(s => s.Name.Contains("Thermal Throttling", StringComparison.OrdinalIgnoreCase) && s.Value.HasValue).ToArray();
        string? Name(SensorCategory category) => sensors.FirstOrDefault(s => s.Category == category && s.Name.Contains(':'))?.Name.Split(':')[0].Trim();
        system = system with { ThermalThrottling = throttle.Length == 0 ? null : throttle.Any(s => s.Value > 0),
            CpuName = Name(SensorCategory.Cpu), GpuName = Name(SensorCategory.Gpu), BoardName = Name(SensorCategory.Motherboard),
            CpuCcdTemp = Pick(SensorCategory.Cpu, "Temperature", "CCD"),
            FanChipName = sensors.FirstOrDefault(s => s.Category == SensorCategory.Motherboard && s.Kind is "Fan" or "Control" && s.Name.Contains(':'))?.Name.Split(':')[0].Trim() };
        var fans = FanMap.Discover(sensors, controls).Select(f => new FanState(f.Header, f.Name,
            sensors.FirstOrDefault(s => s.Category == SensorCategory.Motherboard && s.Kind == "Fan" && s.Id.EndsWith($"/fan/{f.Header - 1}"))?.Value,
            sensors.FirstOrDefault(s => s.Category == SensorCategory.Motherboard && s.Kind == "Control" && s.Id.EndsWith($"/control/{f.Header - 1}"))?.Value,
            f.CanControl, f.IsPump, f.IsPump ? "Fixed 100%" : (settings.HeaderFans.GetValueOrDefault(f.Header) ?? settings.Fans.GetValueOrDefault(f.Name))?.Mode.ToString() ?? "BIOS")).ToArray();
        fans = fans.Select(f =>
        {
            var configured = settings.HeaderFans.GetValueOrDefault(f.Header) ?? settings.Fans.GetValueOrDefault(f.Name);
            if (f.IsPump || configured is null) return f;
            var tuning = configured.ToSettings(settings.FanProfiles);
            return f with { Configuration = new(tuning.Curve.Points, tuning.Source, tuning.Minimum, tuning.Hysteresis, tuning.SpinUpPerSecond, tuning.SpinDownPerSecond, configured.Response, tuning.Averaging,
                configured.Mode != FanMode.Custom && configured.EditedProfile(settings.FanProfiles) is not null) };
        }).ToArray();
        return new(snapshot.Timestamp, system, fans, settings.Fans["Radiator"].Mode,
            new(settings.Lighting.Effect, settings.Lighting.Color, settings.Lighting.Brightness, settings.Lighting.Speed, devices, settings.Lighting.GetParameters(), settings.Map, settings.Events, Bindings: settings.Bindings), lamp, router, health)
            { Scenes = Scenes(settings), FanProfiles = settings.FanProfiles.ToDictionary(p => p.Key, p => (IReadOnlyList<FanPoint>)p.Value) };
    }
    public static IReadOnlyList<SceneSummary> Scenes(ServiceSettings settings) => settings.Scenes.Select(s => new SceneSummary(s.Key, s.Value.Fans, s.Value.Lighting.Effect, s.Value.Lighting.Color, s.Value.Lighting.Brightness,
        !s.Value.Lamp.On ? "off" : s.Value.Lamp.Follow is "pc" or "average" ? "pc" : s.Value.Lamp.Kelvin is >= 5000 ? "cool" : "warm")).ToArray();
    private static string DeviceId(string id)
    {
        var index = id.LastIndexOf('/');
        if (index <= 0) return id;
        var kindIndex = id.LastIndexOf('/', index - 1);
        return kindIndex < 0 ? id : id[..kindIndex];
    }
}
