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
    public WidgetWindow(WidgetViewModel vm)
    {
        _vm = vm; DataContext = vm;
        ShowActivated = false;
        Title = "Prism \u00b7 " + vm.Name;
        WindowStyle = WindowStyle.None; AllowsTransparency = true; ShowInTaskbar = false;
        Background = Brushes.Transparent; ResizeMode = ResizeMode.NoResize;
        SizeToContent = SizeToContent.Height;
        Content = new WidgetContent();
        SourceInitialized += (_, _) => { Place(); UpdateOptions(); };
        vm.PropertyChanged += OptionsChanged;
        Closed += (_, _) => vm.PropertyChanged -= OptionsChanged;
        Width = vm.Width;
    }
    private void OptionsChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(WidgetViewModel.Size) or nameof(WidgetViewModel.AlwaysOnTop) or nameof(WidgetViewModel.ClickThrough)) UpdateOptions();
    }
    private void UpdateOptions()
    {
        Width = _vm.Width; Topmost = _vm.AlwaysOnTop;
        var handle = new WindowInteropHelper(this).Handle;
        if (handle == IntPtr.Zero) return;
        var style = GetWindowLong(handle, -20);
        SetWindowLong(handle, -20, _vm.ClickThrough ? style | 0x80000 | 0x20 : style & ~0x20);
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
