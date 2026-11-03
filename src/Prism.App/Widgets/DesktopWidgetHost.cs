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
            // Show only hidden widgets: calling Show() on a visible window pulls it in front of every other app.
            if (!window.IsVisible) { window.RefreshPlacement(); window.Show(); }
        }
        // Sizes are known only after layout, so check for overlaps once the windows have drawn.
        System.Windows.Application.Current?.Dispatcher.BeginInvoke(ArrangeIfOverlapping, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
    }

    /// <summary>
    /// Widgets that would sit on top of each other are lined up down the right edge of their screen, 16 px apart,
    /// and the new positions are saved. Widgets that don't overlap stay exactly where the user put them.
    /// </summary>
    public void LineUp() => Arrange(force: true);
    private void ArrangeIfOverlapping() => Arrange(force: false);
    private void Arrange(bool force)
    {
        if (ViewModel is null) return;
        foreach (var window in _windows.Values.Where(w => w.IsVisible)) window.UpdateLayout();
        var shown = ViewModel.Widgets.Where(w => w.Enabled && _windows.TryGetValue(w.Id, out var win) && win.IsVisible)
            .Select(w => (Model: w, Window: _windows[w.Id])).ToList();
        bool Overlap(System.Windows.Rect a, System.Windows.Rect b) { a.Inflate(-4, -4); return a.IntersectsWith(b); }
        var rects = shown.Select(s => new System.Windows.Rect(s.Window.Left, s.Window.Top, s.Window.ActualWidth, s.Window.ActualHeight)).ToList();
        bool any = rects.Where((r, i) => rects.Skip(i + 1).Any(o => Overlap(r, o))).Any();
        if (!any && !force) return;
        var area = System.Windows.SystemParameters.WorkArea;
        double x = area.Right - 16, y = area.Top + 16, columnWidth = 0;
        foreach (var (model, window) in shown.OrderBy(s => s.Window.Top))
        {
            if (y + window.ActualHeight > area.Bottom - 16) { y = area.Top + 16; x -= columnWidth + 16; columnWidth = 0; }
            columnWidth = Math.Max(columnWidth, window.ActualWidth);
            window.Left = x - window.ActualWidth; window.Top = y;
            model.MoveTo(window.Left, window.Top, null);
            y += window.ActualHeight + 16;
        }
    }
    public void Dispose() { foreach (var window in _windows.Values) window.Close(); _windows.Clear(); }
}
