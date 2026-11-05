using System.Runtime.InteropServices;

namespace ControlSpawn.Service;

/// <summary>
/// Asks Windows for 1 ms timer ticks while held. The default 15.6 ms tick made the 30 fps lighting loop wake
/// after 31 or 47 ms in turn, so LED animations judder however fast or slow the effect runs. Released while the lights
/// are still, because the 1 ms tick also keeps the CPU out of deep idle.
/// </summary>
internal sealed class TimerResolution : IDisposable
{
    [DllImport("winmm.dll")] private static extern uint timeBeginPeriod(uint milliseconds);
    [DllImport("winmm.dll")] private static extern uint timeEndPeriod(uint milliseconds);
    private bool held;
    public TimerResolution() => Hold(true);
    public void Hold(bool on)
    {
        if (on == held) return;
        try { if (on) held = timeBeginPeriod(1) == 0; else { held = false; timeEndPeriod(1); } }
        catch (DllNotFoundException) { }
        catch (EntryPointNotFoundException) { }
    }
    public void Dispose() => Hold(false);
}
