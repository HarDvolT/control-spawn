using System.Collections.ObjectModel;
using Prism.App.Common;
using Prism.App.Models;
using Prism.App.Services;
using Prism.App.Widgets;
using Prism.Core;
using Prism.Core.Ipc;

namespace Prism.App.ViewModels;

/// <summary>
/// One desktop widget. The same view-model feeds the real borderless window and the preview on the
/// Widgets page, so both always show identical values.
/// </summary>
public sealed class WidgetViewModel : ObservableObject
{
    private readonly IPrismClient _client;
    private bool _isSelected;
    private double _cpu = double.NaN;
    private double _gpu = double.NaN;
    private double _cpuTemp = double.NaN;
    private double _gpuTemp = double.NaN;
    private double _ramPercent;
    private string _ramText = Format.Dash;
    private string _downText = Format.Dash;
    private string _upText = Format.Dash;
    private string _fanMode = "Standard";
    private string _lightingColor = "#FF4D1A";
    private LightingEffect _lightingEffect = LightingEffect.Smart;
    private double _brightness = 0.8;
    private double _speed = 1;
    private double _temperature = 45;
    private bool _lampOn;
    private string _lampSubtitle = "Synced · 70%";
    private string _lampColor = "#FF4D1A";
    private string _modemNetwork = "5G NSA · n78";
    private string _modemVerdict = Format.Dash;
    private string _modemColor = ColorUtil.ToHex(ColorUtil.TextTertiary);
    private string _modemRsrp = Format.Dash;
    private string _modemSinr = Format.Dash;
    private string _pingText = Format.Dash;
    private string _pingColor = ColorUtil.ToHex(ColorUtil.Good);
    private string _activeScene = "Unknown";

    public WidgetViewModel(WidgetLayout layout, IPrismClient client, Action? changed = null)
    {
        Layout = layout;
        _client = client;
        Changed = changed;

        FanModes = ["Silent", "Standard", "Turbo", "Full"];
        Swatches = ["#FF4D1A", "#F4F4F5", "#3DDC84", "#5AA7FF", "#B26BFF", "#F5A524"];

        foreach (var scene in SceneCatalog.All)
            Scenes.Add(new SceneOption(scene.Name, scene.Color));

        SetFanModeCommand = new RelayCommand<string>(name => _ = SetFanModeAsync(name ?? "Standard"));
        SelectColorCommand = new RelayCommand<string>(hex => _ = SelectColorAsync(hex ?? LightingColor));
        ToggleLampCommand = new RelayCommand(() => _ = SetLampAsync(!LampOn));
        ApplySceneCommand = new RelayCommand<string>(name => _ = ApplySceneAsync(name ?? string.Empty));
    }

    public WidgetLayout Layout { get; }
    public Action? Changed { get; }
    public IPrismClient Client => _client;

    public string Id => Layout.Id;
    public WidgetKind Kind => Layout.Kind;
    public string Name => WidgetNames.Name(Kind);
    public string Subtitle => WidgetNames.Subtitle(Kind);
    public string SizeName => WidgetSizes.Name(Layout.Size);

    public double Width => WidgetSizes.Width(Layout.Size);
    public double Height => WidgetSizes.Height(Kind, Layout.Size);

    public double PreviewLeft => Layout.X;
    public double PreviewTop => Layout.Y;

    public void MoveTo(double x, double y, string? monitor)
    {
        Layout.X = double.IsFinite(x) ? x : 0; Layout.Y = double.IsFinite(y) ? y : 0;
        Layout.Monitor = monitor;
        OnPropertiesChanged(nameof(PreviewLeft), nameof(PreviewTop));
        Persist();
    }

    public bool IsSystemMonitor => Kind == WidgetKind.SystemMonitor;
    public bool IsCpu => Kind == WidgetKind.Cpu;
    public bool IsGpu => Kind == WidgetKind.Gpu;
    public bool IsFans => Kind == WidgetKind.Fans;
    public bool IsLighting => Kind == WidgetKind.Lighting;
    public bool IsLamp => Kind == WidgetKind.Lamp;
    public bool IsModem => Kind == WidgetKind.Modem;
    public bool IsPing => Kind == WidgetKind.Ping;
    public bool IsNetwork => Kind == WidgetKind.Network;
    public bool IsScenes => Kind == WidgetKind.Scenes;
    public bool ShowsMetrics => IsSystemMonitor || IsCpu || IsGpu || IsNetwork;

    public WidgetSize Size
    {
        get => Layout.Size;
        set
        {
            if (Layout.Size == value) return;
            Layout.Size = value;
            OnPropertiesChanged(nameof(Size), nameof(SizeName), nameof(Width), nameof(Height));
            Persist();
        }
    }

    public double Opacity
    {
        get => Layout.Opacity;
        set
        {
            var clamped = Math.Clamp(value, 0.2, 1);
            if (Math.Abs(Layout.Opacity - clamped) < 0.001) return;
            Layout.Opacity = clamped;
            OnPropertyChanged();
            OnPropertyChanged(nameof(OpacityPercent));
            Persist();
        }
    }

    public double OpacityPercent { get => Opacity * 100; set => Opacity = value / 100; }
    public IReadOnlyList<string> EffectNames { get; } = Enum.GetNames<LightingEffect>();
    public string SelectedEffectName
    {
        get => LightingEffectKind.ToString();
        set { if (Enum.TryParse<LightingEffect>(value, out var effect) && effect != LightingEffectKind) { LightingEffectKind = effect; _ = RunAsync(() => _client.SetLightingAsync(effect, LightingColor, Brightness, Speed)); } }
    }
    private string? _lastError;
    public string? LastError { get => _lastError; private set => Set(ref _lastError, value); }
    private double _lampBrightness = 70;
    private string _lampFollow = "pc";
    public double LampBrightness
    {
        get => _lampBrightness;
        set { if (Set(ref _lampBrightness, Math.Clamp(value, 1, 100))) _ = SetLampAsync(LampOn); }
    }
    private async Task RunAsync(Func<Task<Response>> action)
    {
        try { var response = await action(); LastError = response.Ok ? null : response.Error ?? "Command refused"; }
        catch (Exception ex) { LastError = ex.Message; }
    }

    public bool AlwaysOnTop
    {
        get => Layout.AlwaysOnTop;
        set { if (Layout.AlwaysOnTop == value) return; Layout.AlwaysOnTop = value; OnPropertyChanged(); Persist(); }
    }

    public bool ClickThrough
    {
        get => Layout.ClickThrough;
        set { if (Layout.ClickThrough == value) return; Layout.ClickThrough = value; OnPropertyChanged(); Persist(); }
    }

    public bool Locked
    {
        get => Layout.Locked;
        set { if (Layout.Locked == value) return; Layout.Locked = value; OnPropertyChanged(); Persist(); }
    }

    public bool SnapToGrid
    {
        get => Layout.SnapToGrid;
        set { if (Layout.SnapToGrid == value) return; Layout.SnapToGrid = value; OnPropertyChanged(); Persist(); }
    }

    public bool IsSelected { get => _isSelected; set => Set(ref _isSelected, value); }

    public bool ShowCpu
    {
        get => Layout.Metrics.Contains(WidgetMetrics.Cpu);
        set => SetMetric(WidgetMetrics.Cpu, value);
    }

    public bool ShowGpu
    {
        get => Layout.Metrics.Contains(WidgetMetrics.Gpu);
        set => SetMetric(WidgetMetrics.Gpu, value);
    }

    public bool ShowTemps
    {
        get => Layout.Metrics.Contains(WidgetMetrics.Temps);
        set => SetMetric(WidgetMetrics.Temps, value);
    }

    public bool ShowRam
    {
        get => Layout.Metrics.Contains(WidgetMetrics.Ram);
        set => SetMetric(WidgetMetrics.Ram, value);
    }

    public bool ShowNet
    {
        get => Layout.Metrics.Contains(WidgetMetrics.Net);
        set => SetMetric(WidgetMetrics.Net, value);
    }

    public IReadOnlyList<string> FanModes { get; }
    public IReadOnlyList<string> Swatches { get; }
    public ObservableCollection<FanRow> FanRows { get; } = new();
    public ObservableCollection<SceneOption> Scenes { get; } = new();
    public ObservableCollection<double> PingHistory { get; } = new();
    public ObservableCollection<double> NetDownHistory { get; } = new();
    public ObservableCollection<double> NetUpHistory { get; } = new();
    public ObservableCollection<BarItem> ModemBars { get; } = new();

    public RelayCommand<string> SetFanModeCommand { get; }
    public RelayCommand<string> SelectColorCommand { get; }
    public RelayCommand ToggleLampCommand { get; }
    public RelayCommand<string> ApplySceneCommand { get; }

    public double CpuPercent => double.IsFinite(_cpu) ? Math.Clamp(_cpu, 0, 100) : 0;
    public double GpuPercent => double.IsFinite(_gpu) ? Math.Clamp(_gpu, 0, 100) : 0;
    public string CpuText => Format.Percent(_cpu);
    public string GpuText => Format.Percent(_gpu);
    public string CpuTempText => Format.Temp(_cpuTemp);
    public string GpuTempText => Format.Temp(_gpuTemp);
    public string RamText => _ramText;
    public double RamPercent => _ramPercent;
    private string _cpuClockText = Format.Dash, _gpuClockText = Format.Dash, _vramText = Format.Dash;
    public string CpuClockText => _cpuClockText;
    public string GpuClockText => _gpuClockText;
    public string VramText => _vramText;
    public string DownText => _downText;
    public string UpText => _upText;

    public string FanMode
    {
        get => _fanMode;
        set
        {
            if (!Set(ref _fanMode, value)) return;
            _ = SetFanModeAsync(value);
        }
    }

    public string LightingColor { get => _lightingColor; private set => Set(ref _lightingColor, value); }
    public LightingEffect LightingEffectKind { get => _lightingEffect; private set => Set(ref _lightingEffect, value); }
    public double Brightness { get => _brightness; private set => Set(ref _brightness, value); }
    public double Speed { get => _speed; private set => Set(ref _speed, value); }
    public double Temperature { get => _temperature; private set => Set(ref _temperature, value); }
    public string EffectName => Format.EffectName(LightingEffectKind);

    public bool LampOn
    {
        get => _lampOn;
        set
        {
            if (!Set(ref _lampOn, value)) return;
            _ = SetLampAsync(value);
        }
    }

    public string LampSubtitle { get => _lampSubtitle; private set => Set(ref _lampSubtitle, value); }
    public string LampColor { get => _lampColor; private set => Set(ref _lampColor, value); }

    public string ModemNetwork { get => _modemNetwork; private set => Set(ref _modemNetwork, value); }
    public string ModemVerdict { get => _modemVerdict; private set => Set(ref _modemVerdict, value); }
    public string ModemColor { get => _modemColor; private set => Set(ref _modemColor, value); }
    public string ModemRsrp { get => _modemRsrp; private set => Set(ref _modemRsrp, value); }
    public string ModemSinr { get => _modemSinr; private set => Set(ref _modemSinr, value); }
    public string PingText { get => _pingText; private set => Set(ref _pingText, value); }
    public string PingColor { get => _pingColor; private set => Set(ref _pingColor, value); }
    public string ActiveScene => _activeScene;

    public void Apply(StateMessage state)
    {
        var system = state.System;
        _cpuClockText = Format.Ghz(system.CpuClockMhz);
        _gpuClockText = Format.Mhz(system.GpuClockMhz);
        _vramText = Format.GbPair(system.VramUsedGb,system.VramTotalGb);
        _cpu = system.CpuLoad ?? double.NaN;
        _gpu = system.GpuLoad ?? double.NaN;
        _cpuTemp = system.CpuTemp ?? double.NaN;
        _gpuTemp = system.GpuTemp ?? double.NaN;
        _ramPercent = system is { RamUsedGb: { } used, RamTotalGb: { } total } && total > 0 ? used / total * 100 : 0;
        _ramText = Format.GbPair(system.RamUsedGb, system.RamTotalGb);
        _downText = Format.Mbps(system.NetDownMbps);
        _upText = Format.Mbps(system.NetUpMbps);
        Push(NetDownHistory, system.NetDownMbps ?? 0);
        Push(NetUpHistory, system.NetUpMbps ?? 0);

        _fanMode = Format.FanModeName(state.FanMode);
        FanRows.Clear();
        foreach (var fan in state.Fans.Where(f => (f.Controllable || f.IsPump) && (f.Rpm is > 0 || f.IsPump)))
        {
            FanRows.Add(new FanRow(fan.Name, fan.IsPump ? "Fixed" : "Curve",
                Format.RpmShort(fan.Rpm), fan.IsPump ? 100 : Math.Clamp(fan.Percent ?? 0, 0, 100), fan.IsPump));
        }

        LightingEffectKind = state.Lighting.Effect;
        LightingColor = state.Lighting.Color;
        Brightness = state.Lighting.Brightness;
        Speed = state.Lighting.Speed;
        Temperature = system.CpuTemp ?? 45;

        if (state.Lamp is { } lamp)
        {
            _lampOn = lamp.On;
            _lampBrightness = lamp.Brightness;
            _lampFollow = lamp.Follow;
            LampSubtitle = (lamp.Follow switch { "pc" => "Synced", "average" => "Average", _ => "Own colour" })
                           + " · " + Format.Percent(lamp.Brightness);
            LampColor = lamp.Color;
        }
        else
        {
            _lampOn = false;
            LampSubtitle = "Not connected";
            LampColor = "#3F3F46";
        }

        if (state.Router is { } router)
        {
            ModemNetwork = router.Network ?? "5G modem";
            ModemVerdict = router.Verdict ?? Format.Dash;
            ModemColor = ColorUtil.ToHex(router.Verdict == "Good" ? ColorUtil.Good : router.Verdict == "Fair" ? ColorUtil.Fair : router.Verdict == "Poor" ? ColorUtil.Poor : ColorUtil.TextTertiary);
            ModemRsrp = Format.Dbm(router.Rsrp5g, 0);
            ModemSinr = Format.Db(router.Sinr5g);
            PingText = Format.MsValue(router.PingMs);
            PingColor = ColorUtil.ToHex(router.PingMs is null ? ColorUtil.TextTertiary : router.PingMs < 35 ? ColorUtil.Good : router.PingMs < 80 ? ColorUtil.Fair : ColorUtil.Poor);
            if (router.PingMs is { } ping) Push(PingHistory, ping);

            ModemBars.Clear();
            var bars = router.Bars ?? 0;
            for (var i = 0; i < 5; i++)
                ModemBars.Add(new BarItem(5 + i * 3, i < bars ? ModemColor : "#3A3A40"));
        }
        else
        {
            ModemBars.Clear();
            ModemNetwork = "5G modem";
            ModemVerdict = Format.Dash;
            ModemColor = ColorUtil.ToHex(ColorUtil.TextTertiary);
            ModemRsrp = Format.Dash;
            ModemSinr = Format.Dash;
            PingText = Format.Dash;
        }

        var match = SceneCatalog.All.FirstOrDefault(s => s.Effect == state.Lighting.Effect && s.FanMode == state.FanMode);
        _activeScene = match?.Name ?? "Custom";
        foreach (var scene in Scenes)
            scene.IsActive = string.Equals(scene.Name, _activeScene, StringComparison.OrdinalIgnoreCase);

        OnPropertiesChanged(nameof(CpuPercent), nameof(GpuPercent), nameof(CpuText), nameof(GpuText),
            nameof(CpuTempText), nameof(GpuTempText), nameof(RamText), nameof(RamPercent), nameof(DownText),
            nameof(UpText), nameof(FanMode), nameof(LightingColor), nameof(LightingEffectKind), nameof(Brightness),
            nameof(SelectedEffectName), nameof(LampBrightness), nameof(Speed), nameof(Temperature), nameof(EffectName), nameof(LampOn), nameof(LampSubtitle),
            nameof(LampColor), nameof(ModemNetwork), nameof(ModemVerdict), nameof(ModemColor), nameof(ModemRsrp),
            nameof(ModemSinr), nameof(PingText), nameof(PingColor), nameof(ActiveScene));
        OnPropertyChanged(string.Empty);
    }

    private void SetMetric(string key, bool enabled)
    {
        var has = Layout.Metrics.Contains(key);
        if (has == enabled) return;
        if (enabled) Layout.Metrics.Add(key);
        else Layout.Metrics.RemoveAll(m => m == key);
        OnPropertyChanged(key switch
        {
            WidgetMetrics.Cpu => nameof(ShowCpu),
            WidgetMetrics.Gpu => nameof(ShowGpu),
            WidgetMetrics.Temps => nameof(ShowTemps),
            WidgetMetrics.Ram => nameof(ShowRam),
            _ => nameof(ShowNet)
        });
        Persist();
    }

    private async Task SetFanModeAsync(string name)
    {
        var normalized = name == "Full speed" ? "Full" : name;
        if (!Enum.TryParse<FanMode>(normalized, true, out var mode)) return;
        await RunAsync(() => _client.SetFanModeAsync("Radiator", mode));
        await RunAsync(() => _client.SetFanModeAsync("Case fans", mode));
    }

    private async Task SelectColorAsync(string hex)
    {
        LightingColor = hex;
        await RunAsync(() => _client.SetLightingAsync(LightingEffectKind, hex, Brightness, Speed));
    }

    private async Task SetLampAsync(bool on)
    {
        await RunAsync(() => _client.SetLampAsync(on, (int)LampBrightness, _lampFollow, LampColor));
    }

    private async Task ApplySceneAsync(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return;
        await RunAsync(() => _client.ApplySceneAsync(name));
    }

    private void Persist() => Changed?.Invoke();

    private static void Push(ObservableCollection<double> history, double value)
    {
        if (!double.IsFinite(value)) return;
        history.Add(Math.Round(value, 1));
        while (history.Count > 40) history.RemoveAt(0);
    }
}
