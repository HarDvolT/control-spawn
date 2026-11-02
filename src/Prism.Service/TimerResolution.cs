using System.Runtime.InteropServices;

namespace Prism.Service;

/// <summary>
/// Asks Windows for 1 ms timer ticks while held. The default 15.6 ms tick made the 30 fps lighting loop wake
/// after 31 or 47 ms in turn, so LED animations judder however fast or slow the effect runs.
/// </summary>
internal sealed class TimerResolution : IDisposable
{
    [DllImport("winmm.dll")] private static extern uint timeBeginPeriod(uint milliseconds);
    [DllImport("winmm.dll")] private static extern uint timeEndPeriod(uint milliseconds);
    private bool held;
    public TimerResolution()
    {
        try { held = timeBeginPeriod(1) == 0; }
        catch (DllNotFoundException) { }
        catch (EntryPointNotFoundException) { }
    }
    public void Dispose()
    {
        if (!held) return;
        held = false; timeEndPeriod(1);
    }
}
