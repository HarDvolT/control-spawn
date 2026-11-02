using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using Prism.App.ViewModels;
using Prism.App.Views;
using Forms = System.Windows.Forms;
namespace Prism.App.Widgets;
public sealed class WidgetWindow : Window
{
    private readonly WidgetViewModel _vm;
    [DllImport("user32.dll", EntryPoint="GetWindowLongW")] private static extern int GetWindowLong(IntPtr hwnd, int index);
    [DllImport("user32.dll", EntryPoint="SetWindowLongW")] private static extern int SetWindowLong(IntPtr hwnd, int index, int value);
    [DllImport("user32.dll")] private static extern bool SetLayeredWindowAttributes(IntPtr hwnd, uint key, byte alpha, uint flags);
    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
    [DllImport("dwmapi.dll")] private static extern int DwmExtendFrameIntoClientArea(IntPtr hwnd, ref Margins margins);
    private struct Margins { public int Left, Right, Top, Bottom; }
    public WidgetWindow(WidgetViewModel vm)
    {
        _vm = vm; DataContext = vm;
        ShowActivated = false;
        Title = "Prism \u00b7 " + vm.Name;
        // No AllowsTransparency: that forces WPF to paint the whole window on the CPU on every change.
        // Windows draws the rounded corners and the blurred glass instead, on the graphics card.
        WindowStyle = WindowStyle.None; ShowInTaskbar = false;
        Background = Brushes.Transparent; ResizeMode = ResizeMode.NoResize;
        System.Windows.Shell.WindowChrome.SetWindowChrome(this, new System.Windows.Shell.WindowChrome { CaptionHeight = 0, ResizeBorderThickness = new Thickness(0), GlassFrameThickness = new Thickness(-1), CornerRadius = new CornerRadius(0), UseAeroCaptionButtons = false });
        SizeToContent = SizeToContent.Height;
        Content = vm.Kind is WidgetKind.SystemMonitor or WidgetKind.Fans or WidgetKind.Modem or WidgetKind.Lamp or WidgetKind.AirFryer
            ? new System.Windows.Controls.Viewbox { Stretch = Stretch.Uniform, Child = new WidgetMiniature(vm) { Width = 250 } }
            : new WidgetContent();
        SourceInitialized += (_, _) => { ApplyBackdrop(); Place(); UpdateOptions(); };
        vm.PropertyChanged += OptionsChanged;
        Closed += (_, _) => vm.PropertyChanged -= OptionsChanged;
        Width = vm.Width;
    }
    private void OptionsChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(WidgetViewModel.Size) or nameof(WidgetViewModel.AlwaysOnTop) or nameof(WidgetViewModel.ClickThrough)) UpdateOptions();
    }
    private void ApplyBackdrop()
    {
        var handle = new WindowInteropHelper(this).Handle;
        if (handle == IntPtr.Zero) return;
        try
        {
            int dark = 1, round = 2, acrylic = 3;
            DwmSetWindowAttribute(handle, 20, ref dark, sizeof(int));     // dark mode frame
            DwmSetWindowAttribute(handle, 33, ref round, sizeof(int));    // rounded corners
            DwmSetWindowAttribute(handle, 38, ref acrylic, sizeof(int));  // blurred glass behind transparent pixels
            var all = new Margins { Left = -1, Right = -1, Top = -1, Bottom = -1 };
            DwmExtendFrameIntoClientArea(handle, ref all);
            if (HwndSource.FromHwnd(handle) is { CompositionTarget: { } target }) target.BackgroundColor = Colors.Transparent;
        }
        catch (DllNotFoundException) { }
        catch (EntryPointNotFoundException) { }
    }
    private void UpdateOptions()
    {
        Width = _vm.Width; Topmost = _vm.AlwaysOnTop;
        var handle = new WindowInteropHelper(this).Handle;
        if (handle == IntPtr.Zero) return;
        var style = GetWindowLong(handle, -20);
        SetWindowLong(handle, -20, _vm.ClickThrough ? style | 0x80000 | 0x20 : style & ~0x20);
        // A layered window needs an alpha set or it stays invisible; fully opaque keeps the glass look.
        if (_vm.ClickThrough) SetLayeredWindowAttributes(handle, 0, 255, 2);
    }
    public void RefreshPlacement() { if (IsLoaded) Place(); }

    private void Place()
    {
        var screens = Forms.Screen.AllScreens;
        var screen = screens.FirstOrDefault(s => s.DeviceName == _vm.Layout.Monitor)
            ?? Forms.Screen.FromPoint(new System.Drawing.Point((int)_vm.Layout.X, (int)_vm.Layout.Y));
        var dpi = VisualTreeHelper.GetDpi(this);
        var area = screen.WorkingArea;
        var bounds = new PixelRect(area.X / dpi.DpiScaleX, area.Y / dpi.DpiScaleY, area.Width / dpi.DpiScaleX, area.Height / dpi.DpiScaleY);
        var point = WidgetPlacement.Restore(_vm.Layout, Width, Math.Max(ActualHeight, _vm.Height), bounds);
        Left = point.X; Top = point.Y;
    }
    public void DragWidget()
    {
        if (_vm.Locked || _vm.ClickThrough) return;
        DragMove();
        var dpi = VisualTreeHelper.GetDpi(this);
        var screen = Forms.Screen.FromPoint(new System.Drawing.Point((int)(Left * dpi.DpiScaleX), (int)(Top * dpi.DpiScaleY)));
        var area = screen.WorkingArea;
        var point = WidgetPlacement.Restore(new WidgetLayout { X = Left, Y = Top, SnapToGrid = _vm.SnapToGrid }, Width, ActualHeight,
            new PixelRect(area.X / dpi.DpiScaleX, area.Y / dpi.DpiScaleY, area.Width / dpi.DpiScaleX, area.Height / dpi.DpiScaleY));
        Left = point.X; Top = point.Y;
        _vm.MoveTo(point.X,point.Y,screen.DeviceName);
    }
}
