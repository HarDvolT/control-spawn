using System.Collections.ObjectModel;
using Prism.App.Common;
using Prism.App.Navigation;
using Prism.App.Services;
using Prism.App.Widgets;
using Prism.Core;
using Prism.Core.Ipc;

namespace Prism.App.ViewModels;

public sealed class NavItem : ObservableObject
{
    private bool _isCurrent;

    public NavItem(PageDefinition definition)
    {
        Definition = definition;
    }

    public PageDefinition Definition { get; }
    public string Title => Definition.Title;
    public string Glyph => Definition.Glyph;
    public string Subtitle => Definition.Subtitle;

    public bool IsCurrent
    {
        get => _isCurrent;
        set => Set(ref _isCurrent, value);
    }
}

public sealed class NavGroupViewModel
{
    public NavGroupViewModel(string name, IEnumerable<NavItem> items)
    {
        Name = name;
        Items = new ObservableCollection<NavItem>(items);
    }

    public string Name { get; }
    public ObservableCollection<NavItem> Items { get; }
}

/// <summary>
/// Shell state: navigation, command search, the service banner and the actions shared by the
/// tray menu, the search overlay and the pages.
/// </summary>
public sealed class MainViewModel : ObservableObject
{
    private readonly IPrismClient _client;
    private readonly IWidgetHost _host;
    private readonly CommandSearch _search = new();
    private readonly Dictionary<PageKey, PageViewModel> _pages;
    private PageViewModel _currentPage;
    private PageKey _currentKey = PageKey.Dashboard;
    private bool _isSearchOpen;
    private string _searchQuery = string.Empty;
    private SearchResult? _selectedResult;
    private bool _isConnected;
    private string _bannerText = string.Empty;
    private string _statusText = "Connecting to the service…";
    private string _serviceDot = ColorUtil.ToHex(ColorUtil.Fair);
    private string _serviceStatusText = "Service · connecting";
    private StateMessage? _lastState;

    public MainViewModel(IPrismClient client, AppSettings settings, SettingsStore settingsStore,
        IStartupRegistry startup, WidgetLayoutStore widgetStore, IWidgetHost host)
    {
        _client = client;
        _host = host;

        Dashboard = new DashboardViewModel(client);
        Monitor = new MonitorViewModel(client);
        Lighting = new LightingViewModel(client);
        Fans = new FansViewModel(client);
        Lamp = new LampViewModel(client);
        Scenes = new ScenesViewModel(client, settings, settingsStore);
        Modem = new ModemViewModel(client);
        Settings = new SettingsViewModel(client, settingsStore, startup, settings);
        Widgets = new WidgetsViewModel(client, widgetStore, host, Settings);

        _pages = new Dictionary<PageKey, PageViewModel>
        {
            [PageKey.Dashboard] = Dashboard,
            [PageKey.Monitor] = Monitor,
            [PageKey.Widgets] = Widgets,
            [PageKey.Lighting] = Lighting,
            [PageKey.Fans] = Fans,
            [PageKey.Lamp] = Lamp,
            [PageKey.Scenes] = Scenes,
            [PageKey.Modem] = Modem,
            [PageKey.Settings] = Settings
        };
        _currentPage = Dashboard;

        foreach (var page in _pages.Values)
            page.NavigateRequested += (_, key) => Navigate(key);

        NavigationGroups = new ObservableCollection<NavGroupViewModel>(
            Navigation.Pages.Groups.Select(group => new NavGroupViewModel(group,
                Navigation.Pages.All.Where(p => p.Group == group).Select(p => new NavItem(p)))));

        NavigateCommand = new RelayCommand<string>(key =>
        {
            if (Enum.TryParse<PageKey>(key, true, out var page)) Navigate(page);
        });
        OpenSearchCommand = new RelayCommand(() => IsSearchOpen = true);
        CloseSearchCommand = new RelayCommand(() => IsSearchOpen = false);
        ExecuteResultCommand = new RelayCommand<SearchResult>(ExecuteResult);

        _client.StateReceived += OnStateReceived;
        _client.ConnectionChanged += OnConnectionChanged;

        if (_client is MockPrismClient mock) ApplyState(mock.CurrentState);
        UpdateNavigation();
        RebuildSearch();
        if (!IsConnected) SetConnection(client.Connection);
    }

    public DashboardViewModel Dashboard { get; }
    public MonitorViewModel Monitor { get; }
    public LightingViewModel Lighting { get; }
    public FansViewModel Fans { get; }
    public LampViewModel Lamp { get; }
    public ScenesViewModel Scenes { get; }
    public ModemViewModel Modem { get; }
    public SettingsViewModel Settings { get; }
    public WidgetsViewModel Widgets { get; }
    public IPrismClient Client => _client;

    public ObservableCollection<NavGroupViewModel> NavigationGroups { get; }
    public ObservableCollection<SearchResult> SearchResults { get; } = new();

    public RelayCommand<string> NavigateCommand { get; }
    public RelayCommand OpenSearchCommand { get; }
    public RelayCommand CloseSearchCommand { get; }
    public RelayCommand<SearchResult> ExecuteResultCommand { get; }

    public PageViewModel CurrentPage { get => _currentPage; private set => Set(ref _currentPage, value); }
    public PageKey CurrentKey { get => _currentKey; private set => Set(ref _currentKey, value); }
    public string CurrentTitle => CurrentPage.PageTitle;
    public string BreadcrumbLeft => CurrentKey == PageKey.Modem ? "Insights" : "Prism";
    public string BreadcrumbTitle => CurrentTitle;
    public StateMessage? LastState => _lastState;

    public bool IsSearchOpen
    {
        get => _isSearchOpen;
        set
        {
            if (!Set(ref _isSearchOpen, value)) return;
            if (value)
            {
                SearchQuery = string.Empty;
                SelectedResult = SearchResults.FirstOrDefault();
            }
        }
    }

    public string SearchQuery
    {
        get => _searchQuery;
        set
        {
            if (!Set(ref _searchQuery, value)) return;
            RebuildSearch();
        }
    }

    public SearchResult? SelectedResult
    {
        get => _selectedResult;
        set => Set(ref _selectedResult, value);
    }

    public bool IsConnected { get => _isConnected; private set => Set(ref _isConnected, value); }
    public bool ShowBanner => !IsConnected;
    public string BannerText { get => _bannerText; private set => Set(ref _bannerText, value); }
    public string StatusText { get => _statusText; private set => Set(ref _statusText, value); }
    public string ServiceDot { get => _serviceDot; private set => Set(ref _serviceDot, value); }
    public string ServiceStatusText { get => _serviceStatusText; private set => Set(ref _serviceStatusText, value); }
    public bool IsMock => _lastState?.Health.Mock ?? _client is MockPrismClient;
    public string ModeBadge => IsMock ? "MOCK" : "LIVE";

    public void Navigate(PageKey key)
    {
        if (!_pages.TryGetValue(key, out var page)) return;
        CurrentKey = key;
        CurrentPage = page;
        UpdateNavigation();
        OnPropertiesChanged(nameof(CurrentTitle), nameof(BreadcrumbLeft), nameof(BreadcrumbTitle));
    }

    public void ApplyState(StateMessage state)
    {
        _lastState = state;
        foreach (var page in _pages.Values) page.Apply(state);
        IsConnected = true;
        ServiceDot = ColorUtil.ToHex(ColorUtil.Good);
        ServiceStatusText = state.Health.Mock ? "Service · mock data" : "Service · live";
        StatusText = state.Health.Warnings.Count > 0
            ? state.Health.Warnings[0]
            : state.Health.FanFailsafeActive ? "Fan failsafe active · BIOS control" : "All systems normal · live";
        OnPropertiesChanged(nameof(ShowBanner), nameof(IsMock), nameof(ModeBadge));
    }

    public void SetConnection(ServiceConnection connection)
    {
        IsConnected = connection == ServiceConnection.Connected;
        ServiceDot = ColorUtil.ToHex(IsConnected ? ColorUtil.Good : connection == ServiceConnection.Connecting ? ColorUtil.Fair : ColorUtil.Poor);
        ServiceStatusText = connection switch
        {
            ServiceConnection.Connected => "Service · live",
            ServiceConnection.Connecting => "Service · connecting",
            _ => "Service · offline"
        };
        if (!IsConnected)
        {
            StatusText = "Service not running";
            BannerText = "Service not running — showing the last known values. Start Prism.Service to control the hardware.";
        }
        OnPropertiesChanged(nameof(ShowBanner), nameof(StatusText), nameof(BannerText));
    }

    public void ExecuteResult(SearchResult? result)
    {
        if (result is null) return;
        switch (result.Action)
        {
            case SearchAction.Navigate:
                if (Enum.TryParse<PageKey>(result.Key, true, out var page)) Navigate(page);
                break;
            case SearchAction.FanMode:
                _ = SetFanModeAsync(result.Key);
                break;
            case SearchAction.Scene:
                _ = ApplySceneAsync(result.Key);
                break;
            case SearchAction.LightsOff:
                _ = LightsOffAsync();
                break;
            case SearchAction.ToggleWidgets:
                if (string.Equals(result.Key, "hide", StringComparison.OrdinalIgnoreCase)) _host.HideAll();
                else _host.ShowAll();
                break;
            case SearchAction.StartSpeedTest:
                Navigate(PageKey.Modem);
                Modem.SelectedView = "Speed";
                _ = RunCommandAsync(() => _client.StartSpeedTestAsync());
                break;
            case SearchAction.MeasureFans:
                Navigate(PageKey.Fans);
                break;
        }
        IsSearchOpen = false;
    }

    public async Task RunCommandAsync(Func<Task<Response>> command)
    {
        try
        {
            var response = await command();
            if (!response.Ok) StatusText = response.Error ?? "The service refused the command";
        }
        catch (Exception ex) { StatusText = ex.Message; }
    }

    public async Task SetFanModeAsync(string key)
    {
        var normalized = key == "Full speed" ? "Full" : key;
        if (!Enum.TryParse<FanMode>(normalized, true, out var mode)) return;
        await RunCommandAsync(() => _client.SetFanModeAsync("Radiator", mode));
        await RunCommandAsync(() => _client.SetFanModeAsync("Case fans", mode));
    }

    public async Task ApplySceneAsync(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return;
        await RunCommandAsync(() => _client.ApplySceneAsync(name));
    }

    public async Task LightsOffAsync()
    {
        await RunCommandAsync(() => _client.SetLightingAsync(LightingEffect.Off, "#000000", 0, 0));
        await RunCommandAsync(() => _client.SetLampAsync(false, 1, "own", "#000000"));
    }

    private void RebuildSearch()
    {
        SearchResults.Clear();
        foreach (var result in _search.Search(SearchQuery)) SearchResults.Add(result);
        SelectedResult = SearchResults.FirstOrDefault();
    }

    private void UpdateNavigation()
    {
        foreach (var group in NavigationGroups)
            foreach (var item in group.Items)
                item.IsCurrent = Enum.TryParse<PageKey>(item.Definition.Key.ToString(), out var key) && key == _currentKey;
    }

    private void OnStateReceived(object? sender, StateMessage state)
    {
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is not null && !dispatcher.CheckAccess())
        {
            dispatcher.BeginInvoke(() => ApplyState(state));
            return;
        }
        ApplyState(state);
    }

    private void OnConnectionChanged(object? sender, ServiceConnection connection)
    {
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is not null && !dispatcher.CheckAccess())
        {
            dispatcher.BeginInvoke(() => SetConnection(connection));
            return;
        }
        SetConnection(connection);
    }
}
