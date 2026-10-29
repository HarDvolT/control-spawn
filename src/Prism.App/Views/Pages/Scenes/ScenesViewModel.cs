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
    private readonly AppSettings settings;
    private readonly SettingsStore? store;
    private string presetName = "", presetScene = "Gaming";
    public IReadOnlyList<string> PresetScenes => SceneCatalog.All.Select(s => s.Name).ToArray();
    public string PresetScene { get => presetScene; set { if (Set(ref presetScene, value)) { presetName = settings.SceneLedPresets.GetValueOrDefault(value) ?? ""; OnPropertyChanged(nameof(PresetName)); } } }
    public string PresetName { get => presetName; set => Set(ref presetName, value); }
    public RelayCommand SavePresetReferenceCommand { get; }

    public ScenesViewModel(IPrismClient client, AppSettings? settings = null, SettingsStore? store = null) : base(client)
    {
        PageTitle = "Scenes";
        this.settings = settings ?? new(); this.store = store;
        presetName = this.settings.SceneLedPresets.GetValueOrDefault(presetScene) ?? "";
        SavePresetReferenceCommand = new RelayCommand(() =>
        {
            this.settings.SceneLedPresets[PresetScene] = PresetName;
            try { this.store?.Save(this.settings); StatusText = "LED layout reference saved"; }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { StatusText = "LED layout reference could not be saved"; }
        });
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
