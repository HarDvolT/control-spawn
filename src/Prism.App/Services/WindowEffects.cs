using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
namespace Prism.App.Services;
internal static class WindowEffects
{
    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
    public static void Apply(Window window)
    {
        try
        {
            var handle = new WindowInteropHelper(window).Handle;
            int dark = 1, backdrop = 2;
            DwmSetWindowAttribute(handle, 20, ref dark, sizeof(int));
            DwmSetWindowAttribute(handle, 38, ref backdrop, sizeof(int));
        }
        catch (DllNotFoundException) { }
        catch (EntryPointNotFoundException) { }
        HwndSource.FromHwnd(new WindowInteropHelper(window).Handle)?.AddHook(KeepTaskbar);
    }

    [StructLayout(LayoutKind.Sequential)] private struct Rect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct MonitorInfo { public int Size; public Rect Monitor, Work; public int Flags; }
    [StructLayout(LayoutKind.Sequential)] private struct MinMaxInfo { public int ReservedX, ReservedY, MaxSizeX, MaxSizeY, MaxPositionX, MaxPositionY, MinTrackX, MinTrackY, MaxTrackX, MaxTrackY; }
    [DllImport("user32.dll")] private static extern IntPtr MonitorFromWindow(IntPtr hwnd, int flags);
    [DllImport("user32.dll")] private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);

    // A borderless window (WindowStyle None) maximizes over the whole monitor and hides the taskbar; cap it to the work area.
    private static IntPtr KeepTaskbar(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        const int WM_GETMINMAXINFO = 0x0024, MONITOR_DEFAULTTONEAREST = 2;
        if (msg != WM_GETMINMAXINFO) return IntPtr.Zero;
        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        if (!GetMonitorInfo(MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST), ref info)) return IntPtr.Zero;
        var limits = Marshal.PtrToStructure<MinMaxInfo>(lParam);
        limits.MaxPositionX = info.Work.Left - info.Monitor.Left; limits.MaxPositionY = info.Work.Top - info.Monitor.Top;
        limits.MaxSizeX = info.Work.Right - info.Work.Left; limits.MaxSizeY = info.Work.Bottom - info.Work.Top;
        Marshal.StructureToPtr(limits, lParam, false);
        return IntPtr.Zero;   // WPF still applies its own MinWidth/MinHeight on top
    }
}
