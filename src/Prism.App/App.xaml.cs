using System.Windows;
using Prism.App.Services;
using Prism.App.ViewModels;
using Prism.App.Widgets;
namespace Prism.App;
public partial class App : Application
{
    private SingleInstance? _instance;
    private IPrismClient? _client;
    private TrayIcon? _tray;
    private DesktopWidgetHost? _widgets;
    private bool _quitting;
    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        CrashLog.Install(this);
        var options = LaunchOptions.Parse(e.Args);
        _instance = new SingleInstance(options.Mock ? "Prism.App.Mock" : "Prism.App", () => Dispatcher.BeginInvoke(OpenMain));
        if (!_instance.IsPrimary) { Shutdown(); return; }
        var store = new SettingsStore();
        var settings = store.Load();
        _client = options.Mock ? new MockPrismClient() : new PipePrismClient();
        _widgets = new DesktopWidgetHost();
        // Mock mode cannot write to the Windows startup registry.
        IStartupRegistry startup = options.Mock ? new InMemoryStartupRegistry() : new RegistryStartup();
        var vm = new MainViewModel(_client, settings, store, startup, new WidgetLayoutStore(), _widgets);
        _widgets.ViewModel = vm.Widgets;
        var window = new MainWindow(vm);
        MainWindow = window;
        window.Closing += (_, args) =>
        {
            if (!_quitting && vm.Settings.KeepRunningWhenClosed) { args.Cancel = true; window.Hide(); }
            else if (!_quitting) { args.Cancel = true; Dispatcher.BeginInvoke(Quit); }
        };
        _tray = new TrayIcon(vm, _widgets, OpenMain, Quit);
        WidgetCard.OpenPage = page => { vm.Navigate(page); OpenMain(); };
        if (!options.Tray && !settings.StartMinimizedToTray) window.Show();
        if (settings.ShowWidgetsAtStartup) _widgets.ShowAll();
        await _client.StartAsync();
        // The service runs outside your desktop session, so the app tells it when the PC locks (for the "PC locked" scene).
        Microsoft.Win32.SystemEvents.SessionSwitch += SessionSwitched;
    }
    private void OpenMain()
    {
        if (MainWindow is null) return;
        MainWindow.Show();
        if (MainWindow.WindowState == WindowState.Minimized) MainWindow.WindowState = WindowState.Normal;
        MainWindow.Activate();
    }
    private void Quit()
    {
        if (_quitting) return;
        _quitting = true;
        _tray?.Dispose(); _tray = null;
        _widgets?.Dispose(); _widgets = null;
        Shutdown();
    }
    private void SessionSwitched(object? sender, Microsoft.Win32.SessionSwitchEventArgs e)
    {
        bool? locked = e.Reason switch { Microsoft.Win32.SessionSwitchReason.SessionLock => true, Microsoft.Win32.SessionSwitchReason.SessionUnlock => false, _ => null };
        if (locked is { } value && _client is { } client) _ = client.SendAsync(Prism.Core.Ipc.Commands.SetSessionLocked, new { locked = value });
    }

    protected override void OnExit(ExitEventArgs e)
    {
        Microsoft.Win32.SystemEvents.SessionSwitch -= SessionSwitched;
        _tray?.Dispose(); _widgets?.Dispose();
        if (_client is not null) _client.DisposeAsync().AsTask().GetAwaiter().GetResult();
        _instance?.Dispose();
        base.OnExit(e);
    }
}
