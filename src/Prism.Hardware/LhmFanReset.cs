using LibreHardwareMonitor.Hardware;

namespace Prism.Hardware;

/// <summary>Crash-only BIOS handoff in the guard process. Never called by tests or mock/observe modes.</summary>
public static class LhmFanReset
{
    public static void RestoreAll()
    {
        var computer = new Computer { IsMotherboardEnabled = true, IsGpuEnabled = true };
        try
        {
            computer.Open();
            foreach (var hardware in computer.Hardware) Reset(hardware);
        }
        finally { computer.Close(); }
    }
    private static void Reset(IHardware hardware)
    {
        try { hardware.Update(); } catch (Exception) { }
        foreach (var sensor in hardware.Sensors)
            if (sensor.Control is { } control)
                try { control.SetDefault(); } catch (Exception) { /* Try every remaining control. */ }
        foreach (var child in hardware.SubHardware) Reset(child);
    }
}
