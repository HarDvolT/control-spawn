using System.Collections.ObjectModel;
using Prism.App.Common;
using Prism.App.Models;
using Prism.App.Services;
using Prism.Core.Ipc;

namespace Prism.App.ViewModels;

/// <summary>Four scenes plus the automatic-switch preferences.</summary>
public sealed class ScenesViewModel : PageViewModel
{
    private string _activeScene = "Unknown";
    private string _statusText = string.Empty;
    private bool _autoGame = true;
    private bool _autoNight = true;
    private bool _autoLock;

    public ScenesViewModel(IPrismClient client) : base(client)
    {
        PageTitle = "Scenes";
        foreach (var scene in SceneCatalog.All)
            Scenes.Add(new SceneCardItem(scene, string.Equals(scene.Name, _activeScene, StringComparison.OrdinalIgnoreCase)));

        ApplyCommand = new RelayCommand<string>(name => _ = ApplyAsync(name ?? string.Empty));
        NewSceneCommand = new RelayCommand(() => StatusText = "Custom scenes arrive with the service update");
        Refresh();
    }

    public ObservableCollection<SceneCardItem> Scenes { get; } = new();
    public RelayCommand<string> ApplyCommand { get; }
    public RelayCommand NewSceneCommand { get; }

    public string ActiveScene { get => _activeScene; private set => Set(ref _activeScene, value); }
    public string StatusText { get => _statusText; private set => Set(ref _statusText, value); }

    public bool AutoGame
    {
        get => _autoGame;
        set => Set(ref _autoGame, value);
    }

    public bool AutoNight
    {
        get => _autoNight;
        set => Set(ref _autoNight, value);
    }

    public bool AutoLock
    {
        get => _autoLock;
        set => Set(ref _autoLock, value);
    }

    public string Summary => $"One click sets lighting, fans and the lamp together. Active: {ActiveScene}";

    protected override void Refresh()
    {
        var state = State;
        if (state is null)
        {
            NotifyAll();
            return;
        }

        var match = SceneCatalog.All.FirstOrDefault(s =>
            s.Effect == state.Lighting.Effect && s.FanMode == state.FanMode);
        ActiveScene = match?.Name ?? "Custom";

        foreach (var scene in Scenes)
            scene.IsActive = string.Equals(scene.Name, ActiveScene, StringComparison.OrdinalIgnoreCase);

        OnPropertiesChanged(nameof(Summary));
        NotifyAll();
    }

    private async Task ApplyAsync(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return;
        await RunAsync(() => Client.ApplySceneAsync(name));
        if (HasError) { StatusText = LastError!; return; }
        ActiveScene = SceneCatalog.Get(name).Name;
        StatusText = name + " scene applied";
        foreach (var scene in Scenes)
            scene.IsActive = string.Equals(scene.Name, ActiveScene, StringComparison.OrdinalIgnoreCase);
        OnPropertyChanged(nameof(Summary));
    }
}
