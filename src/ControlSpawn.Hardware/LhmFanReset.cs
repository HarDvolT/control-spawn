using LibreHardwareMonitor.Hardware;

namespace ControlSpawn.Hardware;

/// <summary>
/// Crash-only BIOS handoff in the guard process. Motherboard controls only: Control Spawn never writes GPU fans, and GPU
/// driver calls are what crashed the service, so opening the GPU here could fail or crash the handoff itself.
/// The real hardware path is never called by tests or mock/observe modes; tests use the internal seam below.
/// </summary>
public static class LhmFanReset
{
    private static readonly TimeSpan RetryDelay = TimeSpan.FromMilliseconds(500);

    public static void RestoreAll() => RestoreAll(() => new ComputerAdapter(new Computer { IsMotherboardEnabled = true }), Thread.Sleep);

    internal static void RestoreAll(Func<IFanResetComputer> create, Action<TimeSpan> wait)
    {
        IFanResetComputer? computer = create();
        try
        {
            try { computer.Open(); }
            catch (Exception)
            {
                // One fresh attempt: a driver can refuse the first open while it is still shutting down.
                Close(computer);
                computer = null;
                wait(RetryDelay);
                computer = create();
                computer.Open(); // A second failure propagates to FanGuard, which logs it.
            }
            foreach (var hardware in computer.Hardware) Reset(hardware);
        }
        finally { if (computer is not null) Close(computer); }
    }
    private static void Close(IFanResetComputer computer)
    {
        try { computer.Close(); } catch (Exception) { /* A failed close must not hide an earlier failure or skip resets. */ }
    }
    private static void Reset(IFanResetHardware hardware)
    {
        try { hardware.Update(); } catch (Exception) { }
        foreach (var control in hardware.Controls)
            try { control.SetDefault(); } catch (Exception) { /* Try every remaining control. */ }
        foreach (var child in hardware.SubHardware) Reset(child);
    }

    internal interface IFanResetComputer
    {
        void Open();
        void Close();
        IEnumerable<IFanResetHardware> Hardware { get; }
    }
    internal interface IFanResetHardware
    {
        void Update();
        IEnumerable<IFanResetControl> Controls { get; }
        IEnumerable<IFanResetHardware> SubHardware { get; }
    }
    internal interface IFanResetControl { void SetDefault(); }

    private sealed class ComputerAdapter(Computer computer) : IFanResetComputer
    {
        public void Open() => computer.Open();
        public void Close() => computer.Close();
        public IEnumerable<IFanResetHardware> Hardware => computer.Hardware.Select(hardware => new HardwareAdapter(hardware));
    }
    private sealed class HardwareAdapter(IHardware hardware) : IFanResetHardware
    {
        public void Update() => hardware.Update();
        public IEnumerable<IFanResetControl> Controls
        {
            get
            {
                foreach (var sensor in hardware.Sensors)
                    if (sensor.Control is { } control)
                        yield return new ControlAdapter(control);
            }
        }
        public IEnumerable<IFanResetHardware> SubHardware => hardware.SubHardware.Select(child => new HardwareAdapter(child));
    }
    private sealed class ControlAdapter(IControl control) : IFanResetControl
    {
        public void SetDefault() => control.SetDefault();
    }
}
