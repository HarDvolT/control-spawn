using System.Collections.ObjectModel;
using Prism.App.Common;
using Prism.App.Models;
using Prism.App.Services;
using Prism.Core;
using Prism.Core.Ipc;

namespace Prism.App.ViewModels;

public sealed class DashboardViewModel : PageViewModel
{
    private const int HistoryLength = 30;

    private string _selectedFanMode = "Standard";
    private bool _suppressMode;
    private bool _lampOn;
    private double _lampBrightness = 70;
    private bool _suppressLamp;
    private double _cpuLoad = double.NaN;
    private double _gpuLoad = double.NaN;
    private double _cpuTemp = double.NaN;
    private double _gpuTemp = double.NaN;
    private string _lightingColor = "#FF4D1A";
    private LightingEffect _lightingEffect = LightingEffect.Smart;
    private double _lightingBrightness = 0.8;
    private double _lightingSpeed = 1;
    private string _activeScene = "Unknown";
    private string _lampSubtitle = "Synced to PC · music mode";
    private string _statusText = "All systems normal · live";

    public DashboardViewModel(IPrismClient client) : base(client)
    {
        PageTitle = "Dashboard";


        foreach (var scene in SceneCatalog.All)
            Scenes.Add(new SceneOption(scene.Name, scene.Color));

        Refresh();
    }

    public ObservableCollection<double> CpuHistory { get; } = new();
    public ObservableCollection<double> GpuHistory { get; } = new();
    public ObservableCollection<double> PingHistory { get; } = new();
    public IReadOnlyList<string> Swatches { get; } = ["#FF4D1A", "#FF4D4F", "#F5A524", "#3DDC84", "#36D6FF", "#4D6BFF", "#B26BFF"];
    public string CpuChip => "CPU " + Format.Temp(State?.System.CpuTemp);
    public string HotspotChip => "Hotspot " + Format.Temp(State?.System.GpuHotspot);
    public string InternetScore => $"{State?.Router?.Verdict ?? "Unavailable"} · {State?.Router?.Score?.ToString() ?? Format.Dash}";
    public string InternetDetail => "Ping " + Format.Number(State?.Router?.PingMs) + " ms · ↓ " + Format.Mbps(State?.System.NetDownMbps);
    public string FanSubtitle => SelectedFanMode switch { "Silent" => "Quiet · you will barely hear it", "Turbo" => "Performance cooling", _ => "Automatic temperature control" };
    public RelayCommand<string> PickColorCommand => new(color => { if (color is not null) _ = RunAsync(() => Client.SetLightingAsync(LightingEffect.Static, color, LightingBrightness, LightingSpeed)); });
    public RelayCommand LightsOffCommand => new(() => _ = RunAsync(() => Client.SetLightingAsync(LightingEffect, LightingColorHex, LightingBrightness > 0 ? 0 : 0.8, LightingSpeed)));
    public ObservableCollection<GaugeItem> Gauges { get; } = new();
    public ObservableCollection<FanRow> Fans { get; } = new();
    public ObservableCollection<SceneOption> Scenes { get; } = new();

    public IReadOnlyList<string> FanModes { get; } = ["Silent", "Standard", "Turbo", "Full speed"];

    public IReadOnlyList<LampRow> LampRows => State is { } s ? LampRow.From(s) : [];

    public string Greeting
    {
        get
        {
            var hour = DateTime.Now.Hour;
            return hour is >= 5 and < 12 ? "Good morning" : hour is >= 12 and < 18 ? "Good afternoon" : "Good evening";
        }
    }

    public string MachineLine => $"{Environment.MachineName} · {State?.Lighting.Devices.Count ?? 0} lighting devices · {LampRows.Count} lamps";
    public string GreetingLabel => $"{Greeting}, {Environment.UserName}".ToUpperInvariant();
    public string Headline => State is null ? "Waiting for your system" : State.Health.Warnings.Count > 0 ? "Your system needs attention" :
        State.System.CpuTemp is null && State.System.GpuTemp is null ? "Waiting for live readings" :
        State.System.CpuTemp >= 85 || State.System.GpuTemp >= 85 ? "Your system is running hot" : "Everything is running cool";
    public string FryerStatus => State?.Appliances?.FirstOrDefault()?.Status ?? "Not connected";
    public string FryerDetail => State?.Appliances?.FirstOrDefault() is { } fryer
        ? $"{fryer.TargetTemperature:0} °C · {fryer.RemainingMinutes:0} min remaining" : "Add your air fryer in Devices";
    public string StatusText { get => _statusText; private set => Set(ref _statusText, value); }
    public string ActiveScene { get => _activeScene; private set => Set(ref _activeScene, value); }

    public string SelectedFanMode
    {
        get => _selectedFanMode;
        set
        {
            if (!Set(ref _selectedFanMode, value) || _suppressMode) return;
            _ = ApplyFanModeAsync(value);
        }
    }

    public double CpuLoad { get => _cpuLoad; private set => Set(ref _cpuLoad, value); }
    public double GpuLoad { get => _gpuLoad; private set => Set(ref _gpuLoad, value); }
    public double CpuTemp { get => _cpuTemp; private set => Set(ref _cpuTemp, value); }
    public double GpuTemp { get => _gpuTemp; private set => Set(ref _gpuTemp, value); }
    public string CpuNowText => Format.Percent(CpuLoad);
    public string GpuNowText => Format.Percent(GpuLoad);

    public LightingEffect LightingEffect { get => _lightingEffect; private set => Set(ref _lightingEffect, value); }
    public string LightingColorHex { get => _lightingColor; private set => Set(ref _lightingColor, value); }
    public double LightingBrightness { get => _lightingBrightness; private set => Set(ref _lightingBrightness, value); }
    public double LightingSpeed { get => _lightingSpeed; private set => Set(ref _lightingSpeed, value); }
    public string LightingTitle => "Lighting · " + Format.EffectName(LightingEffect);

    public string LightingSubtitle => LightingEffect == LightingEffect.Smart
        ? "Colour follows CPU temperature · board, strip, RAM, GPU and lamp in sync"
        : Format.EffectSubtitle(LightingEffect) + " · every device in sync";

    public bool LampOn
    {
        get => _lampOn;
        set
        {
            if (!Set(ref _lampOn, value) || _suppressLamp) return;
            _ = SetLampAsync(value, (int)Math.Round(_lampBrightness));
        }
    }

    public double LampBrightness
    {
        get => _lampBrightness;
        set
        {
            var clamped = Math.Clamp(Math.Round(value), 1, 100);
            if (!Set(ref _lampBrightness, clamped) || _suppressLamp) return;
            _ = SetLampAsync(_lampOn, (int)clamped);
        }
    }

    public string LampBrightnessText => Format.Percent(LampBrightness);
    public string LampSubtitle { get => _lampSubtitle; private set => Set(ref _lampSubtitle, value); }

    public RelayCommand<string> ApplySceneCommand { get; private set; } = null!;
    public RelayCommand ToggleLampCommand { get; private set; } = null!;

    protected override void Refresh()
    {
        ApplySceneCommand ??= new RelayCommand<string>(name => _ = ApplySceneAsync(name ?? string.Empty));
        ToggleLampCommand ??= new RelayCommand(() => LampOn = !LampOn);

        var state = State;
        if (state is null)
        {
            BuildEmptyGauges();
            NotifyAll();
            return;
        }

        var system = state.System;
        CpuLoad = system.CpuLoad ?? double.NaN;
        GpuLoad = system.GpuLoad ?? double.NaN;
        CpuTemp = system.CpuTemp ?? double.NaN;
        GpuTemp = system.GpuTemp ?? double.NaN;

        Push(CpuHistory, system.CpuLoad ?? double.NaN);
        Push(GpuHistory, system.GpuLoad ?? double.NaN);


        SetGauge(0, new GaugeItem("CPU load", Format.Percent(system.CpuLoad), "8 cores · 16 threads",
            ColorUtil.ToHex(Controls.Thresholds.Color(Controls.Thresholds.Level(system.CpuLoad ?? double.NaN, Controls.SensorKind.Load))), Math.Clamp(system.CpuLoad ?? double.NaN, 0, 100)));
        SetGauge(1, new GaugeItem("CPU temp", Format.Temp(system.CpuTemp), "Tctl / Tdie",
            ColorUtil.ToHex(ColorUtil.Heat(system.CpuTemp)), Math.Clamp(system.CpuTemp ?? double.NaN, 0, 100)));
        SetGauge(2, new GaugeItem("GPU load", Format.Percent(system.GpuLoad), "Graphics processor",
            ColorUtil.ToHex(Controls.Thresholds.Color(Controls.Thresholds.Level(system.GpuLoad ?? double.NaN, Controls.SensorKind.Load))), Math.Clamp(system.GpuLoad ?? double.NaN, 0, 100)));
        SetGauge(3, new GaugeItem("GPU temp", Format.Temp(system.GpuTemp), "Graphics temperature",
            ColorUtil.ToHex(ColorUtil.Heat(system.GpuTemp)), Math.Clamp(system.GpuTemp ?? double.NaN, 0, 100)));

        if (state.History is { } history)
        {
            Func<TelemetrySample, double?>[] readings = [s => s.CpuLoad, s => s.CpuTemp, s => s.GpuLoad, s => s.GpuTemp];
            for (var i = 0; i < readings.Length; i++)
            {
                Gauges[i].History.Clear();
                foreach (var sample in history.TakeLast(24))
                    if (readings[i](sample) is { } v && double.IsFinite(v)) Gauges[i].History.Add(v);
            }
            PingHistory.Clear();
            foreach (var sample in history.TakeLast(24))
                if (sample.PingMs is { } ping && double.IsFinite(ping)) PingHistory.Add(ping);
        }

        Fans.Clear();
        foreach (var fan in state.Fans.Where(f => (f.Controllable || f.IsPump) && (f.Rpm is > 0 || f.IsPump)))
        {
            Fans.Add(new FanRow(
                fan.Name,
                fan.Name == "Case fans" ? "front 3 + rear" : fan.IsPump ? "Fixed at 100%" : "CPU_FAN",
                Format.Rpm(fan.Rpm),
                fan.IsPump ? 100 : Math.Clamp(fan.Percent ?? 0, 0, 100),
                fan.IsPump));
        }

        _suppressMode = true;
        SelectedFanMode = Format.FanModeName(state.FanMode);
        _suppressMode = false;

        _lightingEffect = state.Lighting.Effect;
        _lightingColor = state.Lighting.Color;
        _lightingBrightness = state.Lighting.Brightness;
        _lightingSpeed = state.Lighting.Speed;

        _suppressLamp = true;
        _lampOn = state.Lamp?.On ?? false;
        _lampBrightness = state.Lamp?.Brightness ?? 0;
        _suppressLamp = false;
        LampSubtitle = state.Lamp is { } lamp
            ? (lamp.Follow switch { "pc" => "Synced to PC", "average" => "Average of PC colours", _ => "Own colour" })
              + " · " + Controls.FriendlyLabelConverter.Label(lamp.Transport)
            : "Not connected";


        ActiveScene = SceneCatalog.All
            .FirstOrDefault(s => s.Effect == state.Lighting.Effect && s.FanMode == state.FanMode)?.Name ?? "Custom";
        foreach (var scene in Scenes)
            scene.IsActive = string.Equals(scene.Name, ActiveScene, StringComparison.OrdinalIgnoreCase);

        StatusText = state.Health.Warnings.Count > 0
            ? state.Health.Warnings[0]
            : state.Health.FanFailsafeActive ? "Fan failsafe active · BIOS control" : "All systems normal · live";

        NotifyAll();
    }

    private void SetGauge(int index, GaugeItem item)
    { if (Gauges.Count <= index) Gauges.Add(item); else Gauges[index].Update(item); }

    private void BuildEmptyGauges()
    {
        if (Gauges.Count > 0) return;
        Gauges.Add(new GaugeItem("CPU load", Format.Dash, "8 cores · 16 threads", ColorUtil.ToHex(ColorUtil.TextPrimary), 0));
        Gauges.Add(new GaugeItem("CPU temp", Format.Dash, "Tctl / Tdie", ColorUtil.ToHex(ColorUtil.TextTertiary), double.NaN));
        Gauges.Add(new GaugeItem("GPU load", Format.Dash, "Graphics processor", ColorUtil.ToHex(ColorUtil.TextTertiary), double.NaN));
        Gauges.Add(new GaugeItem("GPU temp", Format.Dash, "Graphics temperature", ColorUtil.ToHex(ColorUtil.TextTertiary), double.NaN));
    }

    private static void Push(ObservableCollection<double> history, double value)
    {
        if (!double.IsFinite(value)) return;
        history.Add(Math.Round(value, 1));
        while (history.Count > HistoryLength) history.RemoveAt(0);
    }

    private async Task ApplyFanModeAsync(string name)
    {
        var normalized = name == "Full speed" ? "Full" : name;
        if (!Enum.TryParse<FanMode>(normalized, ignoreCase: true, out var mode)) return;
        await RunAsync(() => Client.SetFanModeAsync("Radiator", mode));
        await RunAsync(() => Client.SetFanModeAsync("Case fans", mode));
    }

    private async Task ApplySceneAsync(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return;
        await RunAsync(() => Client.ApplySceneAsync(name));
    }

    private async Task SetLampAsync(bool on, int brightness)
    {
        var follow = State?.Lamp?.Follow ?? "pc";
        var color = State?.Lamp?.Color ?? LightingColorHex;
        await RunAsync(() => Client.SetLampAsync(on, brightness, follow, color));
    }
}
