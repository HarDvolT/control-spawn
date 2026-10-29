using System.Collections.ObjectModel;
using Prism.App.Common;
using Prism.App.Services;
using Prism.App.Widgets;
using Prism.Core.Ipc;

namespace Prism.App.ViewModels;

/// <summary>The Widgets page: desktop preview, customize panel, gallery and startup toggles.</summary>
public sealed class WidgetsViewModel : PageViewModel
{
    private readonly WidgetLayoutStore _store;
    private readonly IWidgetHost _host;
    private readonly PixelRect _workArea;
    private readonly WidgetLayoutFile _file;
    private WidgetViewModel? _selected;
    private bool _isEditing = true;
    private bool _widgetsVisible;
    private string _statusText = string.Empty;

    public WidgetsViewModel(IPrismClient client, WidgetLayoutStore store, IWidgetHost host,
        SettingsViewModel settings, PixelRect? workArea = null) : base(client)
    {
        PageTitle = "Widgets";
        _store = store;
        _host = host;
        Settings = settings;
        _workArea = workArea ?? new PixelRect(0, 0, 1920, 1080);
        _file = store.Load();

        foreach (var kind in Enum.GetValues<WidgetKind>())
            Gallery.Add(new WidgetGalleryItem(kind, WidgetNames.Name(kind), WidgetNames.Subtitle(kind)));

        if (_file.Widgets.Count == 0 && !File.Exists(store.FilePath)) SeedDefaults();
        foreach (var layout in _file.Widgets) AddViewModel(layout);

        SelectCommand = new RelayCommand<WidgetViewModel>(Select);
        AddCommand = new RelayCommand<WidgetGalleryItem>(Add);
        RemoveCommand = new RelayCommand(RemoveSelected);
        SetSizeCommand = new RelayCommand<string>(SetSize);
        ShowOnDesktopCommand = new RelayCommand(ShowOnDesktop);
        ToggleVisibilityCommand = new RelayCommand(ToggleVisibility);
        ToggleEditCommand = new RelayCommand(() => IsEditing = !IsEditing);

        _host.VisibilityChanged += () => { WidgetsVisible = _host.IsVisible; OnPropertyChanged(nameof(VisibilityLabel)); };
        _widgetsVisible = _host.IsVisible;
        Selected = Widgets.FirstOrDefault();
        Refresh();
    }

    public SettingsViewModel Settings { get; }
    public ObservableCollection<WidgetViewModel> Widgets { get; } = new();
    public ObservableCollection<WidgetGalleryItem> Gallery { get; } = new();

    public RelayCommand<WidgetViewModel> SelectCommand { get; }
    public RelayCommand<WidgetGalleryItem> AddCommand { get; }
    public RelayCommand RemoveCommand { get; }
    public RelayCommand<string> SetSizeCommand { get; }
    public RelayCommand ShowOnDesktopCommand { get; }
    public RelayCommand ToggleVisibilityCommand { get; }
    public RelayCommand ToggleEditCommand { get; }

    public IReadOnlyList<string> Sizes { get; } = ["Small", "Medium", "Large"];

    public WidgetViewModel? Selected
    {
        get => _selected;
        set
        {
            if (ReferenceEquals(_selected, value)) return;
            if (_selected is not null) _selected.IsSelected = false;
            _selected = value;
            if (_selected is not null) _selected.IsSelected = true;
            OnPropertiesChanged(nameof(Selected), nameof(SelectedName), nameof(SelectedHint), nameof(HasSelection));
        }
    }

    public bool HasSelection => _selected is not null;
    public string SelectedName => _selected?.Name ?? "No widget";
    public string SelectedHint => _selected?.Kind switch
    {
        WidgetKind.Fans => "Shows the fan mode switch and live RPM. Changing the mode here changes it for the whole PC.",
        WidgetKind.Lighting => "Quick colour and effect control for every RGB device and the lamp.",
        WidgetKind.Modem => "Your router signal in one glance: verdict, bars, RSRP, SINR and ping.",
        WidgetKind.Lamp => "On/off, brightness and sync for the Yeelight lamp.",
        WidgetKind.Scenes => "One-tap scene switching straight from the desktop.",
        WidgetKind.Ping => "Live latency graph with the current ping.",
        WidgetKind.Network => "Download and upload speed with a short history.",
        WidgetKind.Cpu => "CPU usage and temperature.",
        WidgetKind.Gpu => "GPU usage and temperature.",
        _ => "CPU, GPU and temperature at a glance."
    };

    public bool IsEditing
    {
        get => _isEditing;
        set
        {
            if (!Set(ref _isEditing, value)) return;
            OnPropertyChanged(nameof(EditLabel));
        }
    }

    public string EditLabel => IsEditing ? "Done editing" : "Edit layout";
    public bool WidgetsVisible { get => _widgetsVisible; private set => Set(ref _widgetsVisible, value); }
    public string StatusText { get => _statusText; private set => Set(ref _statusText, value); }
    public string VisibilityLabel => WidgetsVisible ? "Hide from desktop" : "Show on desktop";

    public override void Apply(StateMessage state)
    {
        base.Apply(state);
        OnPropertiesChanged(nameof(SelectedName), nameof(SelectedHint));
    }

    protected override void Refresh()
    {
        if (State is not null)
            foreach (var widget in Widgets) widget.Apply(State);
        NotifyAll();
    }

    private void Select(WidgetViewModel? widget)
    {
        if (widget is not null) Selected = widget;
    }

    private void Add(WidgetGalleryItem? item)
    {
        if (item is null) return;
        var layout = WidgetDefaults.Create(item.Kind, _workArea, Widgets.Count);
        _file.Widgets.Add(layout);
        var vm = AddViewModel(layout);
        Selected = vm;
        Save("Added " + item.Name);
    }

    private void RemoveSelected()
    {
        if (_selected is null) return;
        var name = _selected.Name;
        _file.Widgets.RemoveAll(w => w.Id == _selected.Id);
        Widgets.Remove(_selected);
        Selected = Widgets.FirstOrDefault();
        Save("Removed " + name);
    }

    private void SetSize(string? name)
    {
        if (_selected is null) return;
        var size = name switch
        {
            "Small" => WidgetSize.Small,
            "Large" => WidgetSize.Large,
            _ => WidgetSize.Medium
        };
        _selected.Size = size;
        Save(_selected.Name + " is now " + WidgetSizes.Name(size));
    }

    private void ShowOnDesktop()
    {
        _host.ShowAll();
        WidgetsVisible = true;
        OnPropertyChanged(nameof(VisibilityLabel));
        StatusText = "Widgets pinned to the desktop";
    }

    private void ToggleVisibility()
    {
        _host.Toggle();
        WidgetsVisible = _host.IsVisible;
        OnPropertyChanged(nameof(VisibilityLabel));
        StatusText = WidgetsVisible ? "Widgets visible" : "Widgets hidden";
    }

    private WidgetViewModel AddViewModel(WidgetLayout layout)
    {
        var vm = new WidgetViewModel(layout, Client, () => Save(null));
        Widgets.Add(vm);
        return vm;
    }

    private void SeedDefaults()
    {
        foreach (var kind in new[]
                 {
                     WidgetKind.SystemMonitor, WidgetKind.Fans, WidgetKind.Lighting, WidgetKind.Modem, WidgetKind.Lamp
                 })
        {
            _file.Widgets.Add(WidgetDefaults.Create(kind, _workArea, _file.Widgets.Count));
        }
    }

    private void Save(string? message)
    {
        _file.Widgets = Widgets.Select(w => w.Layout).ToList();
        try
        {
            _store.Save(_file);
        }
        catch (Exception ex)
        {
            StatusText = "Could not save widgets: " + ex.Message;
            return;
        }
        _host.Sync();
        if (message is not null) StatusText = message;
        OnPropertyChanged(nameof(StatusText));
    }
}
