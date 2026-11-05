using System.Diagnostics;
using ControlSpawn.App.Common;
using ControlSpawn.App.Services;
using ControlSpawn.Core.Ipc;

namespace ControlSpawn.App.ViewModels;

/// <summary>
/// Preferences. Every toggle writes straight through to <see cref="AppSettings"/>; the startup
/// registry is only ever touched through <see cref="IStartupRegistry"/> (tests use an in-memory double).
/// </summary>
public sealed class SettingsViewModel : PageViewModel
{
    private readonly SettingsStore _store;
    private readonly IStartupRegistry _startup;
    private readonly AppSettings _settings;
    private readonly Action? _changed;
    private string _lastSavedText = "Saved automatically";
    private DiscoveryOptions discoveryOptions = new();

    public SettingsViewModel(IControlSpawnClient client, SettingsStore store, IStartupRegistry startup,
        AppSettings settings, Action? changed = null) : base(client)
    {
        PageTitle = "Settings";
        _store = store;
        _startup = startup;
        _settings = settings;
        _changed = changed;
        Motion.ReduceAnimations = settings.ReduceAnimations;
        DisplayPreferences.Apply(settings);
        SetAccentCommand = new RelayCommand<string>(color => { if (color is not null && AccentColors.Contains(color)) { _settings.AccentColor = color; DisplayPreferences.Apply(_settings); Save(nameof(AccentColor)); } });
        OpenDataFolderCommand = new RelayCommand(OpenDataFolder);
        SaveServiceOptionsCommand = new AsyncRelayCommand(() => RunAsync(() => Client.SendAsync(Commands.SetServiceOptions,
            new { openRgbPath = OpenRgbPath, launchOpenRgb = LaunchOpenRgb, routerEnabled = RouterEnabled, routerHost = RouterHost })));
        ResetCommand = new RelayCommand(Reset);
        Refresh();
    }

    public IReadOnlyList<string> TemperatureUnits { get; } = ["\u00b0C", "\u00b0F"];
    public IReadOnlyList<string> AccentColors { get; } = ["#FF4D1A", "#F5A524", "#3DDC84", "#4D6BFF", "#B26BFF"];
    public RelayCommand<string> SetAccentCommand { get; }
    public string AccentColor => _settings.AccentColor;
    public string TemperatureUnit
    {
        get => _settings.TemperatureUnit;
        set { if (!TemperatureUnits.Contains(value) || value == TemperatureUnit) return; _settings.TemperatureUnit = value; DisplayPreferences.Apply(_settings); Save(nameof(TemperatureUnit)); }
    }
    public bool NotifyRunningHot { get => _settings.NotifyRunningHot; set { _settings.NotifyRunningHot = value; Save(nameof(NotifyRunningHot)); } }
    public bool NotifyCookingFinished { get => _settings.NotifyCookingFinished; set { _settings.NotifyCookingFinished = value; Save(nameof(NotifyCookingFinished)); } }
    public bool NotifyDeviceOffline { get => _settings.NotifyDeviceOffline; set { _settings.NotifyDeviceOffline = value; Save(nameof(NotifyDeviceOffline)); } }
    public string HealthUpdated => State is null ? "waiting" : "just now";
    public string SectionSubtitle => SelectedSection switch { "General" => "Units, look and notifications", "Devices & access" => "Integrations, credentials and startup", "Safety" => "Protection for your hardware", "Backup" => "Save and restore your preferences", _ => "Version, logs and diagnostics" };
    public IReadOnlyList<string> Sections { get; } = ["General", "Devices & access", "Safety", "Backup", "About"];
    private string selectedSection = "General";
    public string SelectedSection { get => selectedSection; set { if (Sections.Contains(value) && Set(ref selectedSection, value)) NotifyAll(); } }
    public bool IsBackup => SelectedSection == "Backup";
    public IReadOnlyList<StatRow> HealthRows => [
        new("Control Spawn service", State is not null && (Client.IsConnected || IsMock) ? "running" : "offline", State is not null && (Client.IsConnected || IsMock) ? "#3DDC84" : "#F5A524"),
        new("OpenRGB", State is { } rgb ? $"{rgb.Lighting.Devices.Count(d => d.Connected)} devices" : Format.Dash, State?.Lighting.Devices.Any(d => d.Connected) == true ? "#3DDC84" : "#7C808A"),
        new("Fan chip", State?.System.FanChipName ?? "Unavailable", State?.System.FanChipName is null ? "#7C808A" : "#3DDC84"),
        new("Lamps", State?.Lamps is { } lamps ? $"{lamps.Count(l => l.Connected)} online" : Format.Dash, State?.Lamps?.Any(l => l.Connected) == true ? "#3DDC84" : "#7C808A"),
        new("Router", State?.Router?.Connected == true ? "signed in" : "offline", State?.Router?.Connected == true ? "#3DDC84" : "#F5A524")];
    public bool IsGeneral => SelectedSection == "General";
    public bool IsAccess => SelectedSection == "Devices & access";
    public bool IsStartup => SelectedSection == "Startup & tray";
    public bool IsSafety => SelectedSection == "Safety";
    public bool IsAbout => SelectedSection == "About";
    public string HealthSummary => State is { } state ? $"{state.Lighting.Devices.Count(d => d.Connected)} RGB devices · {state.Lamps?.Count(l => l.Connected) ?? 0} lamps · Router {(state.Router?.Connected == true ? "connected" : "offline")}" : "Waiting for the service";
    public RelayCommand OpenDataFolderCommand { get; }
    public AsyncRelayCommand SaveServiceOptionsCommand { get; }
    public bool LaunchOpenRgb { get => _settings.LaunchOpenRgb; set { _settings.LaunchOpenRgb = value; Save(nameof(LaunchOpenRgb)); } }
    public string RouterHost { get => _settings.RouterHost; set { _settings.RouterHost = value; Save(nameof(RouterHost)); } }
    public RelayCommand ResetCommand { get; }
    public bool NotifyNewDevices { get => discoveryOptions.NotifyNewDevices; set => _ = DiscoveryAsync(discoveryOptions with { NotifyNewDevices = value }); }
    public bool AutoJoinNewRgb { get => discoveryOptions.AutoJoinNewRgb; set => _ = DiscoveryAsync(discoveryOptions with { AutoJoinNewRgb = value }); }
    public bool CookingLightFlash { get => discoveryOptions.CookingLightFlash; set => _ = DiscoveryAsync(discoveryOptions with { CookingLightFlash = value }); }
    private Task DiscoveryAsync(DiscoveryOptions options) { discoveryOptions = options; NotifyAll(); return RunAsync(() => Client.SendAsync(Commands.SetDiscoveryOptions, new { options })); }

    public AppSettings Settings => _settings;

    public bool StartWithWindows
    {
        get => _settings.StartWithWindows;
        set
        {
            if (_settings.StartWithWindows == value) return;
            if (!TryStartup(value, StartMinimizedToTray)) return;
            _settings.StartWithWindows = value;
            Save(nameof(StartWithWindows));
        }
    }

    public bool StartMinimizedToTray
    {
        get => _settings.StartMinimizedToTray;
        set
        {
            if (_settings.StartMinimizedToTray == value) return;
            if (StartWithWindows && !TryStartup(true, value)) return;
            _settings.StartMinimizedToTray = value;
            Save(nameof(StartMinimizedToTray));
        }
    }

    public bool KeepRunningWhenClosed
    {
        get => _settings.KeepRunningWhenClosed;
        set
        {
            if (_settings.KeepRunningWhenClosed == value) return;
            _settings.KeepRunningWhenClosed = value;
            Save(nameof(KeepRunningWhenClosed));
        }
    }

    public bool ShowWidgetsAtStartup
    {
        get => _settings.ShowWidgetsAtStartup;
        set
        {
            if (_settings.ShowWidgetsAtStartup == value) return;
            _settings.ShowWidgetsAtStartup = value;
            Save(nameof(ShowWidgetsAtStartup));
        }
    }

    public bool ReduceAnimations
    {
        get => _settings.ReduceAnimations;
        set
        {
            if (_settings.ReduceAnimations == value) return;
            _settings.ReduceAnimations = value;
            Motion.ReduceAnimations = value;
            Save(nameof(ReduceAnimations));
        }
    }

    public bool CheckForUpdates
    {
        get => _settings.CheckForUpdates;
        set
        {
            if (_settings.CheckForUpdates == value) return;
            _settings.CheckForUpdates = value;
            Save(nameof(CheckForUpdates));
        }
    }

    public bool SceneAutoGame
    {
        get => _settings.SceneAutoGame;
        set
        {
            if (_settings.SceneAutoGame == value) return;
            _settings.SceneAutoGame = value;
            Save(nameof(SceneAutoGame));
        }
    }

    public bool SceneAutoNight
    {
        get => _settings.SceneAutoNight;
        set
        {
            if (_settings.SceneAutoNight == value) return;
            _settings.SceneAutoNight = value;
            Save(nameof(SceneAutoNight));
        }
    }

    public bool SceneAutoLock
    {
        get => _settings.SceneAutoLock;
        set
        {
            if (_settings.SceneAutoLock == value) return;
            _settings.SceneAutoLock = value;
            Save(nameof(SceneAutoLock));
        }
    }

    public string OpenRgbPath
    {
        get => _settings.OpenRgbPath;
        set { _settings.OpenRgbPath = value; Save(nameof(OpenRgbPath)); }
    }
    public bool RouterEnabled
    {
        get => _settings.RouterEnabled;
        set { _settings.RouterEnabled = value; Save(nameof(RouterEnabled)); }
    }
    public string IntegrationNote => "Save service options to apply these integration settings.";

    public string LastSavedText { get => _lastSavedText; private set => Set(ref _lastSavedText, value); }
    public string AppVersion => typeof(SettingsViewModel).Assembly.GetName().Version?.ToString(3) ?? "1.0.0";
    public string PipeName => PipeContract.PipeName;
    public string DataFolder => System.IO.Path.GetDirectoryName(_store.FilePath) ?? _store.FilePath;
    public string ServiceText => Client.Connection switch
    {
        ServiceConnection.Connected => "Connected · receiving state once a second",
        ServiceConnection.Connecting => "Connecting…",
        _ => "Service not running · the UI is read-only"
    };

    public string ServiceDot => ColorUtil.ToHex(Client.Connection == ServiceConnection.Connected ? ColorUtil.Good : ColorUtil.Poor);
    public bool IsMock => State?.Health.Mock ?? Client is MockControlSpawnClient;
    public string ModeText => IsMock ? "Mock data" : "Live hardware";
    public string ElevationText => State?.Health.Elevated == true ? "Elevated service" : "Standard user";

    protected override void Refresh()
    {
        discoveryOptions = State?.Discovery ?? discoveryOptions;
        OnPropertiesChanged(nameof(StartWithWindows), nameof(StartMinimizedToTray), nameof(KeepRunningWhenClosed),
            nameof(ShowWidgetsAtStartup), nameof(ReduceAnimations), nameof(CheckForUpdates), nameof(SceneAutoGame),
            nameof(SceneAutoNight), nameof(SceneAutoLock), nameof(ServiceText), nameof(ServiceDot),
            nameof(IsMock), nameof(ModeText), nameof(ElevationText));
        NotifyAll();
    }

    private void Save(string property)
    {
        try
        {
            _store.Save(_settings);
            LastSavedText = "Saved · " + property;
        }
        catch (Exception ex)
        {
            LastSavedText = "Could not save: " + ex.Message;
        }
        _changed?.Invoke();
        OnPropertyChanged(string.Empty);
    }

    private bool TryStartup(bool enabled, bool tray)
    {
        try { _startup.SetEnabled(enabled, tray); return true; }
        catch (Exception ex) { LastSavedText = "Startup preference could not be applied: " + ex.Message; OnPropertyChanged(string.Empty); return false; }
    }

    private void Reset()
    {
        if (!TryStartup(false, false)) return;
        var defaults = new AppSettings();
        _settings.StartWithWindows = defaults.StartWithWindows;
        _settings.StartMinimizedToTray = defaults.StartMinimizedToTray;
        _settings.KeepRunningWhenClosed = defaults.KeepRunningWhenClosed;
        _settings.ShowWidgetsAtStartup = defaults.ShowWidgetsAtStartup;
        _settings.ReduceAnimations = defaults.ReduceAnimations;
        _settings.CheckForUpdates = defaults.CheckForUpdates;
        _settings.TemperatureUnit = defaults.TemperatureUnit;
        _settings.AccentColor = defaults.AccentColor;
        _settings.NotifyRunningHot = defaults.NotifyRunningHot;
        _settings.NotifyCookingFinished = defaults.NotifyCookingFinished;
        _settings.NotifyDeviceOffline = defaults.NotifyDeviceOffline;
        DisplayPreferences.Apply(_settings);
        _settings.SceneAutoGame = defaults.SceneAutoGame;
        _settings.SceneAutoNight = defaults.SceneAutoNight;
        _settings.SceneAutoLock = defaults.SceneAutoLock;
        Motion.ReduceAnimations = defaults.ReduceAnimations;
        _settings.OpenRgbPath = defaults.OpenRgbPath;
        _settings.RouterEnabled = defaults.RouterEnabled;
        _settings.LaunchOpenRgb = defaults.LaunchOpenRgb;
        _settings.RouterHost = defaults.RouterHost;
        Save("reset");
    }

    private void OpenDataFolder()
    {
        if (System.Windows.Application.Current is null) return;
        try
        {
            var folder = DataFolder;
            if (!Directory.Exists(folder)) Directory.CreateDirectory(folder);
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{folder}\"") { UseShellExecute = true });
        }
        catch
        {
            // Opening Explorer is a convenience, never a reason to fail.
        }
    }
}
