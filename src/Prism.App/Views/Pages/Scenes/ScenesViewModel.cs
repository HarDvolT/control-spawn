using System.Collections.ObjectModel;
using Prism.App.Common;
using Prism.App.Models;
using Prism.App.Services;
using Prism.Core;
using Prism.Core.Ipc;

namespace Prism.App.ViewModels;

/// <summary>The service's saved scenes, plus an editor that saves a scene and applies it.</summary>
public sealed class ScenesViewModel : PageViewModel
{
    private static readonly (string Label, LightingEffect Effect)[] Effects =
        [("Static", LightingEffect.Static), ("Smart", LightingEffect.Smart), ("Breathing", LightingEffect.Breathing), ("Screen ambient", LightingEffect.Adaptive), ("Off", LightingEffect.Off)];
    private static readonly (string Label, FanMode Mode)[] FanChoices =
        [("Silent", FanMode.Silent), ("Standard", FanMode.Standard), ("Turbo", FanMode.Turbo), ("Full", FanMode.Full)];
    private static readonly (string Label, string Key)[] LampChoices =
        [("Same as PC", "pc"), ("Warm white", "warm"), ("Cool white", "cool"), ("Off", "off")];

    private string _activeScene = "Unknown";
    private string _statusText = string.Empty;
    private string _editName = "Gaming", _editEffect = "Smart", _editColor = "#FF4D1A", _editFans = "Turbo", _editLamps = "Same as PC";
    private double _editBrightness = 60;
    private bool _isNew;
    private string signature = "";
    private readonly AppSettings settings;
    private readonly SettingsStore? store;
    private string presetName = "", presetScene = "Gaming";
    public IReadOnlyList<string> PresetScenes => Scenes.Select(s => s.Name).ToArray();
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
        ApplyCommand = new RelayCommand<string>(name => _ = ApplyAsync(name ?? string.Empty));
        EditCommand = new RelayCommand<string>(name => { if (Summaries.FirstOrDefault(s => s.Name == name) is { } scene) LoadEditor(scene, isNew: false); });
        NewSceneCommand = new RelayCommand(() =>
        {
            var name = "My scene"; for (int i = 2; Summaries.Any(s => s.Name == name); i++) name = $"My scene {i}";
            LoadEditor(new SceneSummary(name, FanMode.Standard, LightingEffect.Static, "#3DDC84", 0.8, "pc"), isNew: true);
        });
        SaveCommand = new RelayCommand(() => _ = SaveAsync());
        PickColorCommand = new RelayCommand<string>(hex => { if (hex is not null) EditColor = hex; });
        Refresh();
        if (Summaries.FirstOrDefault() is { } first) LoadEditor(first, isNew: false);
    }

    public ObservableCollection<SceneCardItem> Scenes { get; } = new();
    public RelayCommand<string> ApplyCommand { get; }
    public RelayCommand<string> EditCommand { get; }
    public RelayCommand NewSceneCommand { get; }
    public RelayCommand SaveCommand { get; }
    public RelayCommand<string> PickColorCommand { get; }

    public IReadOnlyList<string> EffectOptions { get; } = Effects.Select(e => e.Label).ToArray();
    public IReadOnlyList<string> FanOptions { get; } = FanChoices.Select(f => f.Label).ToArray();
    public IReadOnlyList<string> LampOptions { get; } = LampChoices.Select(l => l.Label).ToArray();
    public IReadOnlyList<string> Swatches { get; } = ["#FF4D1A", "#FF4D4F", "#F5A524", "#3DDC84", "#36D6FF", "#4D6BFF", "#6A4DFF"];

    public string EditName { get => _editName; set { if (Set(ref _editName, value)) OnPropertyChanged(nameof(EditorTitle)); } }
    public string EditEffect { get => _editEffect; set => Set(ref _editEffect, value); }
    public string EditColor { get => _editColor; set => Set(ref _editColor, value); }
    public double EditBrightness { get => _editBrightness; set => Set(ref _editBrightness, Math.Clamp(Math.Round(value), 5, 100)); }
    public string EditFans { get => _editFans; set => Set(ref _editFans, value); }
    public string EditLamps { get => _editLamps; set => Set(ref _editLamps, value); }
    public bool IsNew { get => _isNew; private set { if (Set(ref _isNew, value)) OnPropertyChanged(nameof(EditorTitle)); } }
    public string EditorTitle => IsNew ? "New scene" : "Editing · " + EditName;

    // Automations: each setter sends just that field to the service; refreshes from the service don't echo back.
    private AutomationSettings _rules = new();
    private bool _loadingRules;
    private void SendRule(object change) { if (!_loadingRules) _ = RunAsync(() => Client.SendAsync(Commands.SetAutomations, change)); }
    public IReadOnlyList<string> SceneNames => Scenes.Select(s => s.Name).ToArray();
    public bool GameEnabled { get => _rules.GameEnabled; set { if (value == _rules.GameEnabled) return; _rules = _rules with { GameEnabled = value }; OnPropertyChanged(); SendRule(new { gameEnabled = value }); } }
    public string GameScene { get => _rules.GameScene; set { if (value is null || value == _rules.GameScene) return; _rules = _rules with { GameScene = value }; OnPropertyChanged(); SendRule(new { gameScene = value }); } }
    public bool NightEnabled { get => _rules.NightEnabled; set { if (value == _rules.NightEnabled) return; _rules = _rules with { NightEnabled = value }; OnPropertyChanged(); SendRule(new { nightEnabled = value }); } }
    public string NightScene { get => _rules.NightScene; set { if (value is null || value == _rules.NightScene) return; _rules = _rules with { NightScene = value }; OnPropertyChanged(); SendRule(new { nightScene = value }); } }
    public string NightStart { get => _rules.NightStart; set => SetTime(value, true); }
    public string NightEnd { get => _rules.NightEnd; set => SetTime(value, false); }
    public bool LockEnabled { get => _rules.LockEnabled; set { if (value == _rules.LockEnabled) return; _rules = _rules with { LockEnabled = value }; OnPropertyChanged(); SendRule(new { lockEnabled = value }); } }
    public string LockScene { get => _rules.LockScene; set { if (value is null || value == _rules.LockScene) return; _rules = _rules with { LockScene = value }; OnPropertyChanged(); SendRule(new { lockScene = value }); } }
    public string ExtraGamesText
    {
        get => string.Join(", ", _rules.ExtraGames ?? []);
        set
        {
            var games = (value ?? "").Split([',', ';', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            if (games.SequenceEqual(_rules.ExtraGames ?? [])) return;
            _rules = _rules with { ExtraGames = games }; SendRule(new { extraGames = games });
        }
    }
    private void SetTime(string value, bool start)
    {
        var text = (value ?? "").Trim();
        if (!TimeSpan.TryParseExact(text, [@"h\:mm", @"hh\:mm"], System.Globalization.CultureInfo.InvariantCulture, out var time) || time >= TimeSpan.FromDays(1))
        { StatusText = "Night hours look like 23:00"; OnPropertyChanged(start ? nameof(NightStart) : nameof(NightEnd)); return; }
        text = time.ToString(@"hh\:mm");
        if (text == (start ? _rules.NightStart : _rules.NightEnd)) return;
        _rules = start ? _rules with { NightStart = text } : _rules with { NightEnd = text };
        OnPropertyChanged(start ? nameof(NightStart) : nameof(NightEnd));
        SendRule(start ? new { nightStart = text } : (object)new { nightEnd = text });
    }
    public bool AutomationActive => State?.Automations?.Rule is not null;
    public string AutomationStatus => State?.Automations is not { } a ? "Waiting for the Control Spawn service"
        : a.Rule is null ? "No rule is active right now. Your own scene stays until one is."
        : $"{a.Scene} is on because {Lower(a.Detail)}. Your previous setup comes back when it ends.";
    private static string Lower(string? text) => string.IsNullOrEmpty(text) ? "a rule matched" : char.ToLowerInvariant(text[0]) + text[1..];

    public string ActiveScene { get => _activeScene; private set => Set(ref _activeScene, value); }
    public string StatusText { get => _statusText; private set => Set(ref _statusText, value); }
    public string Summary => $"One click sets lighting, fans and the lamp together. Active: {ActiveScene}";

    /// <summary>Scenes from the service, or the built-in four when talking to an older service.</summary>
    private IReadOnlyList<SceneSummary> Summaries => State?.Scenes is { Count: > 0 } live ? live
        : SceneCatalog.All.Select(s => new SceneSummary(s.Name, s.FanMode, s.Effect, s.Color, s.LampBrightness / 100.0, !s.LampOn ? "off" : "pc")).ToArray();

    protected override void Refresh()
    {
        var summaries = Summaries;
        var next = string.Join(";", summaries.Select(s => $"{s.Name}|{s.Fans}|{s.Effect}|{s.Color}|{s.Brightness}|{s.Lamps}"));
        if (next != signature)
        {
            signature = next;
            Scenes.Clear();
            foreach (var s in summaries) Scenes.Add(new SceneCardItem(ToDefinition(s), false));
            OnPropertyChanged(nameof(PresetScenes));
        }
        if (State?.Automations?.Settings is { } rules && rules != _rules)
        {
            _loadingRules = true;
            try { _rules = rules; } finally { _loadingRules = false; }
        }
        OnPropertyChanged(nameof(SceneNames));
        if (State is { } state)
        {
            var match = summaries.FirstOrDefault(s => s.Effect == state.Lighting.Effect && s.Fans == state.FanMode && string.Equals(s.Color, state.Lighting.Color, StringComparison.OrdinalIgnoreCase))
                ?? summaries.FirstOrDefault(s => s.Effect == state.Lighting.Effect && s.Fans == state.FanMode);
            ActiveScene = match?.Name ?? "Custom";
        }
        foreach (var scene in Scenes) scene.IsActive = string.Equals(scene.Name, ActiveScene, StringComparison.OrdinalIgnoreCase);
        OnPropertiesChanged(nameof(Summary));
        NotifyAll();
    }

    private void LoadEditor(SceneSummary s, bool isNew)
    {
        EditName = s.Name;
        EditEffect = Effects.FirstOrDefault(e => e.Effect == s.Effect).Label ?? "Static";
        EditColor = s.Color;
        EditBrightness = s.Brightness * 100;
        EditFans = FanChoices.FirstOrDefault(f => f.Mode == s.Fans).Label ?? "Standard";
        EditLamps = LampChoices.FirstOrDefault(l => l.Key == s.Lamps).Label ?? "Same as PC";
        IsNew = isNew;
    }

    private static SceneDefinition ToDefinition(SceneSummary s)
    {
        var effect = Effects.FirstOrDefault(e => e.Effect == s.Effect).Label ?? s.Effect.ToString();
        var fans = FanChoices.FirstOrDefault(f => f.Mode == s.Fans).Label ?? s.Fans.ToString();
        var lamps = LampChoices.FirstOrDefault(l => l.Key == s.Lamps).Label ?? "Same as PC";
        var color = s.Effect == LightingEffect.Off ? "#5A5D66" : s.Color;
        return new SceneDefinition(s.Name, color, s.Effect, s.Fans, s.Lamps, s.Lamps != "off", (int)Math.Round(s.Brightness * 100),
            "Manual", $"{effect}, {Math.Round(s.Brightness * 100)}%", fans, s.Lamps switch { "off" => "Lamps off", "pc" => "Lamps follow PC", "cool" => "Lamps cool white", _ => "Lamps warm white" }, s.Fans switch { FanMode.Silent => 22, FanMode.Turbo => 70, FanMode.Full => 90, _ => 44 });
    }

    private async Task SaveAsync()
    {
        var name = EditName.Trim();
        if (name.Length is 0 or > 32) { StatusText = "Give the scene a name of up to 32 characters"; return; }
        await RunAsync(() => Client.SendAsync(Commands.SaveScene, new
        {
            name,
            fans = FanChoices.First(f => f.Label == EditFans).Mode.ToString(),
            effect = Effects.First(e => e.Label == EditEffect).Effect.ToString(),
            color = EditColor,
            brightness = EditBrightness / 100.0,
            lamps = LampChoices.First(l => l.Label == EditLamps).Key
        }));
        if (HasError) { StatusText = LastError!; return; }
        IsNew = false;
        await ApplyAsync(name);
    }

    private async Task ApplyAsync(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return;
        await RunAsync(() => Client.ApplySceneAsync(name));
        if (HasError) { StatusText = LastError!; return; }
        ActiveScene = name;
        StatusText = name + " scene applied";
        foreach (var scene in Scenes)
            scene.IsActive = string.Equals(scene.Name, ActiveScene, StringComparison.OrdinalIgnoreCase);
        OnPropertyChanged(nameof(Summary));
    }
}
