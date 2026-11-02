using System.Collections.ObjectModel;
using Prism.App.Common;
using Prism.App.Services;
using Prism.Core;
using Prism.Lighting;
using Prism.Core.Ipc;

namespace Prism.App.ViewModels;

public sealed class LightingViewModel : PageViewModel
{
    private LightingEffect _effect = LightingEffect.Smart;
    private string _colorHex = "#FF4D1A";
    private double _brightness = 0.8;
    private double _speed = 1;
    private double _brightnessPercent = 80;
    private double _speedPercent = 50;
    private double _temperature = 45;
    private string _follow = "pc";
    private bool _suppress;
    private string _savedText = string.Empty;
    private string _scanText = string.Empty;
    private string _syncCountText = "0 devices in sync";

    public LightingViewModel(IPrismClient client) : base(client)
    {
        PageTitle = "Lighting";
        Editor = new(client);

        Effects =
        [
            new EffectOption(LightingEffect.Static, "Static", "One colour", "#FF4D1A"),
            new EffectOption(LightingEffect.Breathing, "Breathing", "Fade in/out", "#F5A524"),
            new EffectOption(LightingEffect.Strobing, "Strobing", "Flash", "#F4F4F5"),
            new EffectOption(LightingEffect.ColorCycle, "Color cycle", "All colours", "#A78BFA"),
            new EffectOption(LightingEffect.RainbowWave, "Rainbow", "Moving wave", "#60A5FA"),
            new EffectOption(LightingEffect.StarryNight, "Starry night", "Twinkles", "#E4E4E7"),
            new EffectOption(LightingEffect.Music, "Music", "Follows audio", "#3DDC84"),
            new EffectOption(LightingEffect.Smart, "Smart", "CPU temperature", "#FF4D1A"),
            new EffectOption(LightingEffect.Adaptive, "Adaptive", "Screen colours", "#60A5FA"),
            new EffectOption(LightingEffect.Custom, "Custom", "Per-LED colours", "#B26BFF"),
            new EffectOption(LightingEffect.Off, "Off", "Lights off", "#3F3F46")
        ];

        foreach (var effect in Enum.GetValues<LightingEffect>().Where(e => Effects.All(o => o.Effect != e)))
            Effects.Add(new(effect, effect == LightingEffect.Fire ? "Fire / Lava" : effect == LightingEffect.Rain ? "Rain / Matrix" : System.Text.RegularExpressions.Regex.Replace(effect.ToString(), "([a-z])([A-Z])", "$1 $2"), "Spatial lighting", "#5AA7FF"));
        SelectEffectCommand = new RelayCommand<EffectOption>(option => _ = SelectEffectAsync(option));
        SelectColorCommand = new RelayCommand<string>(hex => _ = SelectColorAsync(hex ?? ColorHex));
        SetDeviceCommand = new RelayCommand<DeviceOption>(option => _ = SetDeviceAsync(option));
        SetFollowCommand = new RelayCommand<string>(follow => _ = SetFollowAsync(follow ?? "pc"));
        SaveToSceneCommand = new RelayCommand(() => _ = SaveSceneAsync());
        AddDeviceCommand = new RelayCommand(() => _ = ScanAsync());
        Refresh();
    }

    public Task SyncAllAsync(bool resetOverrides = false) => RunLatestAsync("lighting:all", Commands.SetAllLighting,
        new { effect = Effect.ToString(), color = ColorHex, brightness = Brightness, speed = Speed, resetOverrides });

    public EffectParameters Parameters { get; private set; } = new();

    /// <summary>
    /// The part the Per device view has selected ("device" or "device/zone/N"), or null for the whole case. While set,
    /// effect, colour, brightness and speed show and change that part's own look, not the case's.
    /// </summary>
    public string? PartTarget
    {
        get => _partTarget;
        set { if (_partTarget == value) return; _partTarget = value; OnPropertyChanged(); Refresh(); }
    }
    private string? _partTarget;
    public PartLook? PartLookOf(string key) => State?.Lighting.PartLooks?.GetValueOrDefault(key);
    public Task SetPartLookAsync(string part, bool clear = false) => clear
        ? RunAsync(() => Client.SendAsync(Commands.SetPartLook, new { part, clear = true }))
        : RunAsync(() => Client.SendAsync(Commands.SetPartLook, new { part, effect = Effect.ToString(), color = ColorHex, brightness = Brightness, speed = Speed }));
    public LightingMap Map { get; private set; } = new([]);
    public EventEffectOptions Events { get; private set; } = new();
    public string SceneName { get; set; } = "Gaming";
    /// <summary>Switches one lamp on or off, as clicking its orb in the case picture does.</summary>
    public Task ToggleLampAsync(LampState lamp) => RunAsync(() => Client.SendAsync(Commands.SetLamp, new { lamp = lamp.Id, on = !lamp.On }));
    public async Task SaveSceneAsync()
    {
        await RunAsync(() => Client.SendAsync(Commands.SaveLightingScene, new { name = SceneName }));
        SavedText = LastError ?? $"Saved lighting to {SceneName}";
    }
    public async Task SetParametersAsync(EffectParameters parameters)
    {
        try { LightingValidation.Parameters(parameters); }
        catch (ArgumentException e) { SavedText = e.Message; return; }
        Parameters = parameters; OnPropertyChanged(nameof(Parameters));
        await RunLatestAsync("lighting:parameters", Commands.SetEffectParams, new { parameters });
    }
    public async Task SetMapAsync(LightingMap map)
    {
        Map = map; OnPropertyChanged(nameof(Map));
        await RunLatestAsync("lighting:map", Commands.SetLightingMap, new { map });
    }
    public async Task SetEventsAsync(EventEffectOptions events)
    {
        Events = events; OnPropertyChanged(nameof(Events));
        await RunAsync(() => Client.SendAsync(Commands.SetEventEffects, new { events }));
    }
    public LedEditorViewModel Editor { get; }
    public ObservableCollection<EffectOption> Effects { get; }
    public ObservableCollection<DeviceOption> Devices { get; } = new();

    public IReadOnlyList<string> Swatches { get; } = ["#FF4D1A", "#F4F4F5", "#3DDC84", "#5AA7FF", "#B26BFF", "#F5A524"];

    public RelayCommand<EffectOption> SelectEffectCommand { get; }
    public RelayCommand<string> SelectColorCommand { get; }
    public RelayCommand<DeviceOption> SetDeviceCommand { get; }
    public RelayCommand<string> SetFollowCommand { get; }
    public RelayCommand SaveToSceneCommand { get; }
    public RelayCommand AddDeviceCommand { get; }

    public LightingEffect Effect
    {
        get => _effect;
        private set => Set(ref _effect, value);
    }

    public string ColorEntry { get => ColorHex; set { if (Prism.Lighting.LedLayers.Valid(value, false) && value != ColorHex) _ = SelectColorAsync(value); } }
    public string EffectName => Format.EffectName(Effect);
    public string EffectSubtitle => Format.EffectSubtitle(Effect);
    public string ColorHex { get => _colorHex; private set => Set(ref _colorHex, value); }
    public double Brightness { get => _brightness; private set => Set(ref _brightness, value); }
    public double Speed { get => _speed; private set => Set(ref _speed, value); }
    public double Temperature { get => _temperature; private set => Set(ref _temperature, value); }
    public string SyncCountText { get => _syncCountText; private set => Set(ref _syncCountText, value); }

    public double BrightnessPercent
    {
        get => _brightnessPercent;
        set
        {
            var clamped = Math.Clamp(Math.Round(value), 0, 100);
            if (!Set(ref _brightnessPercent, clamped) || _suppress) return;
            _brightness = clamped / 100.0;
            OnPropertyChanged(nameof(Brightness));
            OnPropertyChanged(nameof(BrightnessText));
            _ = PushLightingAsync();
        }
    }

    public double SpeedPercent
    {
        get => _speedPercent;
        set
        {
            var clamped = Math.Clamp(Math.Round(value), 0, 100);
            if (!Set(ref _speedPercent, clamped) || _suppress) return;
            _speed = clamped / 100.0 * 2;
            OnPropertyChanged(nameof(Speed));
            OnPropertyChanged(nameof(SpeedText));
            _ = PushLightingAsync();
        }
    }

    public string BrightnessText => Format.Percent(BrightnessPercent);
    public string SpeedText => Format.Percent(SpeedPercent);
    public string SavedText { get => _savedText; private set => Set(ref _savedText, value); }
    public string ScanText { get => _scanText; private set => Set(ref _scanText, value); }

    public bool LampFollowPc => _follow == "pc";
    public bool LampFollowAverage => _follow == "average";
    public bool LampFollowOwn => _follow == "own";

    protected override void Refresh()
    {
        var state = State;
        if (state is null)
        {
            NotifyAll();
            return;
        }

        Editor.Apply(state);
        var lighting = state.Lighting;
        _suppress = true;
        Parameters = lighting.Parameters ?? new() { Palette = Palettes.ForEffect(lighting.Effect) };
        Map = lighting.Map ?? new([]);
        Events = lighting.Events ?? new();
        // A selected part with its own look shows that look; otherwise the case's, which its first change copies.
        var own = PartTarget is { } target ? lighting.PartLooks?.GetValueOrDefault(target) : null;
        Effect = own?.Effect ?? lighting.Effect;
        ColorHex = own?.Color ?? lighting.Color;
        Brightness = own?.Brightness ?? lighting.Brightness;
        Speed = own?.Speed ?? lighting.Speed;
        BrightnessPercent = Math.Clamp(Brightness * 100, 0, 100);
        SpeedPercent = Math.Clamp(Speed / 2 * 100, 0, 100);
        _suppress = false;

        Temperature = state.System.CpuTemp ?? 45;
        _follow = state.Lamp?.Follow ?? "pc";

        foreach (var effect in Effects)
            effect.IsSelected = effect.Effect == Effect;

        Devices.Clear();
        foreach (var device in lighting.Devices)
        {
            Devices.Add(new DeviceOption(device.Id, device.Name, DeviceSubtitle(device.Id, device.Kind), device.Synced));
        }
        SyncCountText = $"{lighting.Devices.Count(d => d.Synced)} devices in sync";

        OnPropertyChanged(nameof(EffectName));
        OnPropertyChanged(nameof(EffectSubtitle));
        OnPropertyChanged(nameof(BrightnessText));
        OnPropertyChanged(nameof(SpeedText));
        OnPropertyChanged(nameof(LampFollowPc));
        OnPropertyChanged(nameof(LampFollowAverage));
        OnPropertyChanged(nameof(LampFollowOwn));
        NotifyAll();
    }

    private static string DeviceSubtitle(string id, string kind) => id switch
    {
        "board" => "ROG STRIX B850-F",
        "strip" => "ARGB header",
        "ram" => "Vengeance RGB ×2",
        "gpu" => "RX 9070 XT Taichi",
        "lamp" => "Yeelight bulb",
        _ => kind
    };

    private async Task SelectEffectAsync(EffectOption? option)
    {
        if (option is null) return;
        Effect = option.Effect;
        foreach (var effect in Effects) effect.IsSelected = effect.Effect == option.Effect;
        OnPropertyChanged(nameof(EffectName));
        OnPropertyChanged(nameof(EffectSubtitle));
        await PushLightingAsync();
        // A part's effect uses its own default palette in the service, so the case's tuning is left alone.
        if (PartTarget is null && Parameters.Stops is null) await SetParametersAsync(Parameters with { Palette = Palettes.ForEffect(Effect) });
    }

    /// <summary>Sets effect, colour and palette in one go and sends a single update, so a preset never flashes through in-between states.</summary>
    public async Task ApplyLookAsync(LightingEffect effect, string hex, string? palette)
    {
        Effect = effect; ColorHex = hex;
        foreach (var option in Effects) option.IsSelected = option.Effect == effect;
        OnPropertyChanged(nameof(ColorEntry)); OnPropertyChanged(nameof(EffectName)); OnPropertyChanged(nameof(EffectSubtitle));
        await PushLightingAsync();
        if (PartTarget is null && palette is not null && palette != Parameters.Palette) await SetParametersAsync(Parameters with { Palette = palette, Stops = null });
    }

    private async Task SelectColorAsync(string hex)
    {
        ColorHex = hex;
        OnPropertyChanged(nameof(ColorEntry));
        await PushLightingAsync();
    }

    private async Task PushLightingAsync()
    {
        if (PartTarget is { } part)
            await RunLatestAsync("lighting:part:" + part, Commands.SetPartLook, new { part, effect = Effect.ToString(), color = ColorHex, brightness = Brightness, speed = Speed });
        else await RunLatestAsync("lighting", Commands.SetLighting, new { effect = Effect.ToString(), color = ColorHex, brightness = Brightness, speed = Speed });
    }

    private async Task SetDeviceAsync(DeviceOption? option)
    {
        if (option is null) return;
        await RunAsync(() => Client.SetDeviceSyncAsync(option.Id, option.Synced));
    }

    private async Task SetFollowAsync(string follow)
    {
        _follow = follow;
        OnPropertyChanged(nameof(LampFollowPc));
        OnPropertyChanged(nameof(LampFollowAverage));
        OnPropertyChanged(nameof(LampFollowOwn));
        var lamp = State?.Lamp;
        await RunAsync(() => Client.SetLampAsync(lamp?.On ?? true, lamp?.Brightness ?? 70, follow, lamp?.Color));
    }

    private async Task ScanAsync()
    {
        ScanText = "Scanning…";
        await Task.Delay(600);
        ScanText = "No new devices found";
    }
}
