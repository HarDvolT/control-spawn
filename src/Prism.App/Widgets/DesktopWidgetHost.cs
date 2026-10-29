using Prism.App.ViewModels;
namespace Prism.App.Widgets;
public sealed class DesktopWidgetHost : IWidgetHost, IDisposable
{
    private readonly Dictionary<string, WidgetWindow> _windows = new();
    public WidgetsViewModel? ViewModel { get; set; }
    public event Action? VisibilityChanged;
    public bool IsVisible { get; private set; }
    public void ShowAll() { IsVisible = true; Sync(); VisibilityChanged?.Invoke(); }
    public void HideAll() { IsVisible = false; foreach (var window in _windows.Values) window.Hide(); VisibilityChanged?.Invoke(); }
    public void Toggle() { if (IsVisible) HideAll(); else ShowAll(); }
    public void Sync()
    {
        if (ViewModel is null) return;
        var ids = ViewModel.Widgets.Select(w => w.Id).ToHashSet();
        foreach (var id in _windows.Keys.Where(id => !ids.Contains(id)).ToArray()) { _windows[id].Close(); _windows.Remove(id); }
        if (!IsVisible) return;
        foreach (var vm in ViewModel.Widgets)
        {
            if (!vm.Enabled) { if (_windows.TryGetValue(vm.Id, out var hidden)) hidden.Hide(); continue; }
            if (!_windows.TryGetValue(vm.Id, out var window)) { window = new WidgetWindow(vm); _windows.Add(vm.Id, window); }
            window.RefreshPlacement();
            window.Show();
        }
    }
    public void Dispose() { foreach (var window in _windows.Values) window.Close(); _windows.Clear(); }
}
