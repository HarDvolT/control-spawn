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
    private IReadOnlyList<double> _pingHistory = [];
    public IReadOnlyList<double> PingHistory { get => _pingHistory; private set { _pingHistory = value; OnPropertyChanged(); OnPropertyChanged(nameof(HasPingHistory)); } }
    public bool HasPingHistory => _pingHistory.Count > 1;
    public IReadOnlyList<string> Swatches { get; } = ["#FF4D1A", "#FF4D4F", "#F5A524", "#3DDC84", "#36D6FF", "#4D6BFF", "#B26BFF"];
    public string CpuChip => "CPU " + Format.Temp(State?.System.CpuTemp, 0);
    public string HotspotChip => "Hotspot " + Format.Temp(State?.System.GpuHotspot, 0);
    public string NetworkChip => State?.Router is { } router ? (router.Network ?? "Unavailable") + " " + router.Band : "Unavailable";
    public string InternetColor => ColorUtil.ToHex(ColorUtil.ForVerdict(Format.SignalWord(State?.Router)));
    public string InternetScore => $"{Format.SignalWord(State?.Router) ?? "Unavailable"} · {State?.Router?.Score?.ToString() ?? Format.Dash}";
    public string InternetDetail => "Ping " + Format.Number(State?.Router?.PingMs) + " ms · ↓ " + Format.Mbps(State?.System.NetDownMbps);
    public string FanSubtitle => SelectedFanMode switch { "Silent" => "Quiet · you will barely hear it", "Turbo" => "Performance cooling", "Full" => "Every fan at 100%", "" => "Fans on different profiles", _ => "Automatic temperature control" };
    public RelayCommand<string> PickColorCommand => new(color => { if (color is not null) _ = RunAsync(() => Client.SetLightingAsync(LightingEffect.Static, color, LightingBrightness, LightingSpeed)); });
    public RelayCommand LightsOffCommand => new(() => _ = RunAsync(() => Client.SetLightingAsync(LightingEffect, LightingColorHex, LightingBrightness > 0 ? 0 : 0.8, LightingSpeed)));
    public ObservableCollection<GaugeItem> Gauges { get; } = new();
    public ObservableCollection<FanRow> Fans { get; } = new();
    public ObservableCollection<SceneOption> Scenes { get; } = new();

    public IReadOnlyList<string> FanModes { get; } = ["Silent", "Standard", "Turbo", "Full"];

    /// <summary>Stable rows replaced only when a lamp changes, so the orbs aren't rebuilt every second.</summary>
    public ObservableCollection<LampRow> LampRows { get; } = new();
    public RelayCommand<LampRow> ToggleLampRowCommand => new(row => _ = ToggleLampAsync(row));
    public string LampsHint => LampRows.Count == 0 ? "No lamps yet · add one in Devices" : "Click a lamp to switch it on or off";
    public string CpuLevelText => Format.Temp(State?.System.CpuTemp, 0);
    public Controls.StatusLevel CpuLevel => Level(State?.System.CpuTemp, 85);
    public Controls.StatusLevel HotspotLevel => Level(State?.System.GpuHotspot, 100);
    private static Controls.StatusLevel Level(double? value, double hot) => value is not { } v ? Controls.StatusLevel.Unavailable
        : v >= hot ? Controls.StatusLevel.Critical : v >= hot - 10 ? Controls.StatusLevel.Hot : Controls.StatusLevel.Ok;
    // The router's network name often already ends with the band ("5G NSA · n78"), so add it only when missing.
    public string NetworkText => State?.Router is { Connected: true } router
        ? string.Join(" · ", new[] { router.Network, router.Network?.Contains(router.Band ?? "\0", StringComparison.OrdinalIgnoreCase) == true ? null : router.Band }.Where(s => !string.IsNullOrWhiteSpace(s)))
        : "Router not connected";

    public string Greeting
    {
        get
        {
            var hour = DateTime.Now.Hour;
            return hour is >= 5 and < 12 ? "Good morning" : hour is >= 12 and < 18 ? "Good afternoon" : "Good evening";
        }
    }

    public string MachineLine => string.Join(" · ", new[] { State?.System.BoardName, State?.System.CpuName, State?.System.GpuName }.Where(n => !string.IsNullOrWhiteSpace(n)).DefaultIfEmpty(Environment.MachineName)) + $" · {LampRows.Count} lamps" + (State?.Appliances?.Count > 0 ? " · air fryer" : "");
    public string FryerName => State?.Appliances?.FirstOrDefault()?.Name ?? "Air fryer";
    public string GreetingLabel => $"{Greeting}, {Environment.UserName}".ToUpperInvariant();
    public string Headline => State is null ? "Waiting for your system" : State.Health.Warnings.Count > 0 ? "Your system needs attention" :
        State.System.CpuTemp is null && State.System.GpuTemp is null ? "Waiting for live readings" :
        State.System.CpuTemp >= 85 || State.System.GpuTemp >= 85 ? "Your system is running hot" : "Everything is running cool";
    public string FryerRingText => FryerStatus == "Not connected" ? Format.Dash : FryerStatus;
    public string FryerStatus => State?.Appliances?.FirstOrDefault() is { } fryer ? !fryer.Connected ? "Offline" : fryer.Status == "idle" ? "Ready" : fryer.Status : "Not connected";
    public string FryerDetail => State?.Appliances?.FirstOrDefault() is { } fryer ? !fryer.Connected ? "Can't reach it on the network" : fryer.Status == "idle" ? "Idle · ready to cook" : "Cooking now" : "Add your air fryer in Devices";
    public string StatusText { get => _statusText; private set => Set(ref _statusText, value); }
    public string ActiveScene { get => _activeScene; private set => Set(ref _activeScene, value); }

    public string SelectedFanMode
    {
        get => _selectedFanMode;
        set
        {
            if (!Set(ref _selectedFanMode, value) || _suppressMode || string.IsNullOrEmpty(value)) return;
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
    public string LightingTitle => LightingBrightness <= 0 || LightingEffect == LightingEffect.Off ? "Off" : $"{Format.EffectName(LightingEffect)} · {LightingBrightness * 100:0}%";

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


        SetGauge(0, new GaugeItem("CPU load", Format.Percent(system.CpuLoad), "Processor",
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
            var recent = history.TakeLast(24).ToArray();
            for (var i = 0; i < readings.Length; i++)
                Gauges[i].History = recent.Select(readings[i]).Where(v => v is { } d && double.IsFinite(d)).Select(v => v!.Value).ToArray();
            var pings = recent.Select(s => s.PingMs).Where(v => v is { } d && double.IsFinite(d)).Select(v => v!.Value).ToArray();
            if (!pings.SequenceEqual(PingHistory)) PingHistory = pings;
        }

        // Swap only the rows that changed, so unchanged fan tiles (and their spinning icons) are left alone.
        var rows = state.Fans.Where(f => (f.Controllable || f.IsPump) && (f.Rpm is > 0 || f.IsPump))
            .Select(fan => new FanRow(fan.Name, fan.IsPump ? "Fixed at 100%" : $"Header {fan.Header}", Format.Rpm(fan.Rpm),
                fan.IsPump ? 100 : Math.Clamp(fan.Percent ?? double.NaN, 0, 100), fan.IsPump)).ToList();
        if (rows.Count != Fans.Count) { Fans.Clear(); foreach (var row in rows) Fans.Add(row); }
        else for (int i = 0; i < rows.Count; i++) if (rows[i] != Fans[i]) Fans[i] = rows[i];

        _suppressMode = true;
        var modes = state.Fans.Where(f => f.Controllable && !f.IsPump && f.Rpm is > 0).Select(f => f.Curve).Distinct().ToArray();
        SelectedFanMode = modes.Length > 1 ? "" : state.FanMode == FanMode.Full ? "Full" : Format.FanModeName(state.FanMode);
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


        var lamps = LampRow.From(state);
        if (lamps.Count != LampRows.Count || lamps.Select(l => l.Id).Where((id, i) => id != LampRows[i].Id).Any()) { LampRows.Clear(); foreach (var row in lamps) LampRows.Add(row); }
        else for (int i = 0; i < lamps.Count; i++) if (lamps[i] != LampRows[i]) LampRows[i] = lamps[i];

        foreach (var scene in Scenes)
            scene.Description = state.Scenes?.FirstOrDefault(s => s.Name == scene.Name) is { } saved
                ? $"{Format.EffectName(saved.Effect)} lighting · {Format.FanModeName(saved.Fans)} fans"
                : SceneCatalog.All.FirstOrDefault(s => s.Name == scene.Name) is { } built ? $"{Format.EffectName(built.Effect)} lighting · {Format.FanModeName(built.FanMode)} fans" : "";
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
        Gauges.Add(new GaugeItem("CPU load", Format.Dash, "Processor", ColorUtil.ToHex(ColorUtil.TextPrimary), double.NaN));
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
        var normalized = name == "Full speed" ? "Full" : name == "Balanced" ? "Standard" : name;
        if (!Enum.TryParse<FanMode>(normalized, ignoreCase: true, out var mode)) return;
        await RunAsync(() => Client.SetFanModeAsync("Radiator", mode));
        await RunAsync(() => Client.SetFanModeAsync("Case fans", mode));
    }

    private async Task ApplySceneAsync(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return;
        await RunAsync(() => Client.ApplySceneAsync(name));
    }

    /// <summary>Flips one lamp. The orb changes at once; the next state from the service confirms it.</summary>
    private async Task ToggleLampAsync(LampRow? row)
    {
        if (row is null || !row.Connected) return;
        int index = LampRows.IndexOf(row);
        if (index >= 0) LampRows[index] = row with { On = !row.On, Color = row.On ? "#3F3F46" : State?.Lamps?.FirstOrDefault(l => l.Id == row.Id)?.Color ?? row.Color, Status = row.On ? "Off" : "On" };
        await RunAsync(() => Client.SendAsync(Commands.SetLamp, new { lamp = row.Id, on = !row.On }));
    }

    private async Task SetLampAsync(bool on, int brightness)
    {
        var follow = State?.Lamp?.Follow ?? "pc";
        var color = State?.Lamp?.Color ?? LightingColorHex;
        await RunAsync(() => Client.SetLampAsync(on, brightness, follow, color));
    }
}
