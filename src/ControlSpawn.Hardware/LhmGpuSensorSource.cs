using ControlSpawn.Core;
using LibreHardwareMonitor.Hardware;

namespace ControlSpawn.Hardware;

/// <summary>Owns the GPU driver calls made by the service's sensor child.</summary>
public sealed class LhmGpuSensorSource : ISensorSource
{
    private Computer? computer;

    public IReadOnlyList<SensorReading> Read()
    {
        if (computer is null)
        {
            var gpu = new Computer { IsGpuEnabled = true };
            try { gpu.Open(); }
            catch { gpu.Close(); throw; }
            computer = gpu;
        }

        var readings = new List<SensorReading>();
        foreach (var hardware in computer.Hardware) Visit(hardware, readings);
        return readings;
    }

    private static void Visit(IHardware hardware, List<SensorReading> readings)
    {
        hardware.Update();
        foreach (var sensor in hardware.Sensors)
            readings.Add(SensorProjection.FromLhm(hardware, sensor, SensorCategory.Gpu));
        foreach (var subHardware in hardware.SubHardware) Visit(subHardware, readings);
    }

    public void Dispose()
    {
        computer?.Close();
        computer = null;
    }
}
