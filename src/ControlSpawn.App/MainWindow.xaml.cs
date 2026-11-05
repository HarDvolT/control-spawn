using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using ControlSpawn.App.ViewModels;
namespace ControlSpawn.App;
public partial class MainWindow : Window
{
    private readonly MainViewModel _vm;
    public MainWindow(MainViewModel vm)
    {
        InitializeComponent(); DataContext = _vm = vm;
        // A minimized window draws nothing, so hide its content: every animation and timer pauses until it comes back.
        StateChanged += (_, _) => { if (Content is UIElement root) root.Visibility = WindowState == WindowState.Minimized ? Visibility.Hidden : Visibility.Visible; };
        SourceInitialized += (_, _) => Services.WindowEffects.Apply(this);
        vm.PropertyChanged += SearchChanged;
        Closed += (_, _) => vm.PropertyChanged -= SearchChanged;
    }
    private void SearchChanged(object? sender, PropertyChangedEventArgs e)
    { if (e.PropertyName == nameof(_vm.IsSearchOpen) && _vm.IsSearchOpen) Dispatcher.BeginInvoke(() => SearchBox.Focus()); }
    private void TitleDrag(object sender, MouseButtonEventArgs e)
    { if (e.ClickCount == 2) MaximizeClick(sender,e); else DragMove(); }
    private void NotificationsClick(object sender, RoutedEventArgs e)
    {
        var menu = new System.Windows.Controls.ContextMenu();
        var warnings = (_vm.LastState?.Health.Warnings ?? []).Concat(_vm.Notifications.Messages).ToArray();
        foreach (var warning in warnings is { Length: > 0 } ? warnings : new[] { _vm.StatusText })
            menu.Items.Add(new System.Windows.Controls.MenuItem { Header = warning, IsEnabled = false });
        menu.PlacementTarget = (UIElement)sender; menu.IsOpen = true;
    }
    private void MinimizeClick(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    private void MaximizeClick(object sender, RoutedEventArgs e) => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    private void CloseClick(object sender, RoutedEventArgs e) => Close();
    private void SearchActivate(object sender, MouseButtonEventArgs e) => _vm.ExecuteResult(_vm.SelectedResult);
    private void WindowKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Space && Keyboard.Modifiers == ModifierKeys.Control) { _vm.IsSearchOpen = true; e.Handled = true; }
        else if (e.Key == Key.Escape) { _vm.IsSearchOpen = false; e.Handled = true; }
        else if (_vm.IsSearchOpen && e.Key == Key.Enter) { _vm.ExecuteResult(_vm.SelectedResult); e.Handled = true; }
        else if (_vm.IsSearchOpen && e.Key == Key.Down) { SearchList.SelectedIndex = Math.Min(SearchList.SelectedIndex + 1, SearchList.Items.Count - 1); e.Handled = true; }
        else if (_vm.IsSearchOpen && e.Key == Key.Up) { SearchList.SelectedIndex = Math.Max(0, SearchList.SelectedIndex - 1); e.Handled = true; }
    }
}
