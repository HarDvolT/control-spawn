using System.Collections.ObjectModel;
using Prism.App.Common;
using Prism.App.Controls;
using Prism.App.Services;
using Prism.Core;
using Prism.Core.Ipc;

namespace Prism.App.ViewModels;

public sealed class FansViewModel : PageViewModel
{
    // The service's built-in curves (FanCurveEngine.Preset); they used to differ here, so Silent showed a curve it never ran.
    private static readonly IReadOnlyDictionary<FanMode, FanPoint[]> DefaultCurves = new Dictionary<FanMode, FanPoint[]>
    {
        [FanMode.Silent] = [new(30, 20), new(40, 22), new(50, 25), new(55, 27.5), new(60, 30), new(67, 42), new(70, 55), new(100, 100)],
        [FanMode.Standard] = [new(30, 26), new(40, 34), new(50, 45), new(55, 52.5), new(60, 60), new(67, 80), new(70, 100), new(100, 100)],
        [FanMode.Turbo] = [new(30, 45), new(40, 55), new(50, 65), new(55, 72.5), new(60, 80), new(67, 95), new(70, 100), new(100, 100)],
        [FanMode.Full] = [new(30, 100), new(40, 100), new(50, 100), new(60, 100), new(70, 100), new(80, 100), new(90, 100), new(100, 100)],
        [FanMode.Custom] = [new(30, 25), new(38, 25), new(45, 35), new(55, 50), new(65, 70), new(72, 90), new(80, 100), new(100, 100)]
    };
    public static IReadOnlyList<FanPoint> BuiltIn(FanMode mode) => DefaultCurves[mode];

    // Unsaved edits per fan and mode, so flicking between fans and profiles doesn't lose them; saved curves come from the service.
    private readonly Dictionary<(int Header, FanMode Mode), List<FanPoint>> _curves = new();
    private readonly Stack<FanPoint[]> undo = new();
    private FanMode _mode = FanMode.Standard;
    private string _selectedModeName = "Standard";
    private string _selectedFanName = "Radiator";
    private int selectedHeader = 2;
    private bool _suppressMode;
    private double _nowTemp = 48;
    private double _nowPercent = 45;
    private string _sourceName = "CPU";
    private double _minSpeed = 20;
    private string _measureText = string.Empty;
    private bool _isPumpSelected;
    private bool curveDirty;

    public FansViewModel(IPrismClient client) : base(client)
    {
        PageTitle = "Fans";

        SelectFanCommand = new RelayCommand<FanGroupItem>(item => SelectFan(item));
        SelectModeCommand = new RelayCommand<string>(name => SelectedModeName = name ?? "Standard");
        ApplyCurveCommand = new RelayCommand(() => _ = ApplyCurveAsync());
        DiscardCommand = new RelayCommand(Discard);
        ResetProfileCommand = new RelayCommand(() => _ = ResetProfileAsync());
        CopyCommand = new RelayCommand(() => _ = CopyAsync());
        MeasureCommand = new RelayCommand(() => _ = MeasureAsync());
        UndoCommand = new RelayCommand(Undo);
        ShapeCommand = new RelayCommand<string>(Shape);
        Curve.CollectionChanged += (_, _) => UpdateNow();
        LoadCurve(_mode);
        Refresh();
    }

    // ---- Curve point editing -----------------------------------------------------------------------------

    private int selectedCurvePoint = 2;
    public int SelectedCurvePoint
    {
        get => selectedCurvePoint;
        set { if (Set(ref selectedCurvePoint, value)) OnPropertiesChanged(nameof(PointLabel), nameof(SelectedTemperatureText), nameof(SelectedPercentText)); }
    }
    public string PointLabel => $"Point {SelectedCurvePoint + 1} of {Curve.Count}";
    private FanPoint? SelectedPoint => SelectedCurvePoint >= 0 && SelectedCurvePoint < Curve.Count ? Curve[SelectedCurvePoint] : null;
    /// <summary>The selected point typed in exactly; the box shows °C or °F as the display setting says.</summary>
    public string SelectedTemperatureText
    {
        get => SelectedPoint is { } p ? DisplayPreferences.Temperature(p.Temperature).ToString("0") : "";
        set
        {
            if (SelectedPoint is not { } p || !double.TryParse(value.Trim().TrimEnd('°', 'C', 'F', 'c', 'f'), out var typed)) { OnPropertyChanged(); return; }
            double celsius = DisplayPreferences.Fahrenheit ? (typed - 32) * 5 / 9 : typed;
            var (low, high) = Bounds(SelectedCurvePoint);
            SetPoint(p with { Temperature = Math.Clamp(Math.Round(celsius), low, high) });
        }
    }
    public string SelectedPercentText
    {
        get => SelectedPoint is { } p ? p.Percent.ToString("0") : "";
        set
        {
            if (SelectedPoint is not { } p || !double.TryParse(value.Trim().TrimEnd('%'), out var typed)) { OnPropertyChanged(); return; }
            SetPoint(p with { Percent = Math.Clamp(Math.Round(typed), Floor, 100) });
        }
    }
    private (double Low, double High) Bounds(int index) =>
        (index == 0 ? FanCurveEditor.TempMin : Curve[index - 1].Temperature + 1, index == Curve.Count - 1 ? FanCurveEditor.TempMax : Curve[index + 1].Temperature - 1);
    // Points never go below where the measured fan stops spinning.
    private double Floor => SelectedRpmMap?.StopsBelow ?? 0;
    private void SetPoint(FanPoint point)
    {
        if (!CanEditCurve || SelectedPoint is not { } current || current == point) { OnPropertiesChanged(nameof(SelectedTemperatureText), nameof(SelectedPercentText)); return; }
        BeginEdit(); Curve[SelectedCurvePoint] = point; OnCurveEdited();
    }

    public RelayCommand<string> NudgePointCommand => new(direction =>
    {
        if (!CanEditCurve || SelectedPoint is not { } point) return;
        var (low, high) = Bounds(SelectedCurvePoint);
        if (low > high) return;
        SetPoint(direction switch
        {
            "up" => point with { Percent = Math.Clamp(point.Percent + 5, Floor, 100) },
            "down" => point with { Percent = Math.Clamp(point.Percent - 5, Floor, 100) },
            "cooler" => point with { Temperature = Math.Clamp(point.Temperature - 5, low, high) },
            "hotter" => point with { Temperature = Math.Clamp(point.Temperature + 5, low, high) }, _ => point
        });
    });

    /// <summary>Remembers the curve before an edit, so Undo can step back to it. Drags call it once, at the start.</summary>
    public void BeginEdit()
    {
        if (Curve.Count != 8) return;
        var now = Curve.ToArray();
        if (undo.Count == 0 || !undo.Peek().SequenceEqual(now)) undo.Push(now);
        OnPropertyChanged(nameof(CanUndo));
    }
    public bool CanUndo => undo.Count > 0 && CanEditCurve;
    public RelayCommand UndoCommand { get; }
    private void Undo()
    {
        if (!CanUndo) return;
        var previous = undo.Pop();
        Curve.Clear(); foreach (var p in previous) Curve.Add(p);
        curveDirty = !previous.SequenceEqual(SavedCurve ?? []);
        if (!curveDirty) _curves.Remove((selectedHeader, _mode));
        SaveCurve(); NotifyAll();
    }

    /// <summary>One-click reshapes: quieter or cooler by 5% where the fan works, or one flat speed across idle.</summary>
    public RelayCommand<string> ShapeCommand { get; }
    private void Shape(string? shape)
    {
        if (!CanEditCurve || Curve.Count != 8) return;
        BeginEdit();
        var points = Curve.ToArray();
        double idle = points.Where(p => p.Temperature <= 55).Select(p => p.Percent).DefaultIfEmpty(points[0].Percent).Min();
        // Points at full speed stay there: "quieter" shouldn't take the top off the curve.
        var shaped = points.Select(p => shape switch
        {
            "quieter" => p.Percent >= 100 ? p : p with { Percent = Math.Max(Floor, p.Percent - 5) },
            "cooler" => p with { Percent = Math.Min(100, p.Percent + 5) },
            "flatten" => p.Temperature <= 55 ? p with { Percent = idle } : p,
            _ => p
        }).ToArray();
        for (int i = 0; i < 8; i++) if (Curve[i] != shaped[i]) Curve[i] = shaped[i];
        OnCurveEdited();
    }

    // ---- What the page shows -----------------------------------------------------------------------------

    public FanControlState? SelectedConfiguration => State?.Fans.FirstOrDefault(f => f.Header == selectedHeader)?.Configuration;
    private FanState? SelectedFan => State?.Fans.FirstOrDefault(f => f.Header == selectedHeader);
    public FanRpmMap? SelectedRpmMap => State?.FanRpm?.GetValueOrDefault(selectedHeader);
    public string Headline => State is null ? "Waiting for your fans" : State.Health.FanFailsafeActive ? "Overheat guard is on" : AllFansMode switch
    { "" => "Each fan its own way", "Silent" => "Quiet and cool", "Turbo" => "Turbo cooling", "Full speed" => "Full speed, maximum cooling", _ => "Balanced cooling" };

    /// <summary>The header switch: sets every fan at once. Empty while the fans run different profiles (or Custom).</summary>
    public IReadOnlyList<string> AllModes { get; } = ["Silent", "Standard", "Turbo", "Full speed"];
    private FanState[] DrivenFans => State?.Fans.Where(f => !f.IsPump && Fans.Any(i => i.Id == $"fan:{f.Header}")).ToArray() ?? [];
    public string AllFansMode
    {
        get => DrivenFans.Select(f => f.Curve).Distinct().ToArray() is [var only] && Enum.TryParse<FanMode>(only, out var m) && m != FanMode.Custom
            ? (m == FanMode.Full ? "Full speed" : m.ToString()) : "";
        set { if (AllModes.Contains(value) && value != AllFansMode) _ = SetAllFansAsync(ParseMode(value)); }
    }
    public string AllFansSummary => DrivenFans is { Length: > 0 } fans && AllFansMode == ""
        ? string.Join(" · ", fans.Select(f => $"{f.Name} on {(f.Curve == "Full" ? "Full speed" : f.Curve)}")) : "Sets every fan · each fan keeps its own curve per profile";
    private async Task SetAllFansAsync(FanMode mode)
    {
        foreach (var fan in DrivenFans) await RunAsync(() => Client.SetFanModeAsync(GroupOf(fan.Header), mode));
        MeasureText = HasError ? LastError! : $"Every fan on {(mode == FanMode.Full ? "Full speed" : mode.ToString())}";
    }
    private static string GroupOf(int header) => header == 2 ? "Radiator" : header == 6 ? "Case fans" : $"fan:{header}";

    /// <summary>One line that says what the fans are doing and why, e.g. "Quiet · CPU 46 °C · Radiator 1,067 rpm".</summary>
    public string StatusLine
    {
        get
        {
            if (State is not { } state) return "Connecting to the service";
            if (state.Fans.FirstOrDefault(f => f.Stalled) is { } stuck) return $"{stuck.Name} has stopped spinning · check its cable";
            if (state.Health.FanFailsafeActive) return "Every fan at 100% until temperatures drop · " + (state.Health.LastFailsafeReason ?? "overheat guard");
            var driven = state.Fans.Where(f => f.Controllable && !f.IsPump && f.Rpm is > 0).ToArray();
            double target = driven.Select(f => f.Target ?? f.Percent ?? 0).DefaultIfEmpty(0).Max();
            string mood = target < 35 ? "Quiet" : target < 65 ? "Working" : "Cooling hard";
            var parts = new List<string> { mood, $"CPU {Format.TempUnit(state.System.CpuTemp ?? double.NaN)}" };
            if (state.System.GpuTemp is { } gpu) parts.Add($"GPU {Format.TempUnit(gpu)}");
            parts.AddRange(driven.Select(f => $"{f.Name} {Format.Rpm(f.Rpm)}"));
            return string.Join(" · ", parts);
        }
    }

    /// <summary>What the chosen profile does, read off its curve: idle speed, where it reaches half and full speed.</summary>
    public string ProfileSummary
    {
        get
        {
            if (IsPumpSelected) return "The pump always runs at 100%.";
            if (_mode == FanMode.Full) return "Every fan at 100%, whatever the temperature.";
            var points = Curve.ToArray();
            if (points.Length < 2) return "";
            double idle = DutyAt(points, 50);
            string Temp(double t) => Format.TempUnit(t, 0);
            var half = Enumerable.Range(30, 71).FirstOrDefault(t => DutyAt(points, t) >= 50, 0);
            var full = Enumerable.Range(30, 71).FirstOrDefault(t => DutyAt(points, t) >= 100, 0);
            var rpm = FanCurveEditor.RpmAt(SelectedRpmMap, idle) is { } r ? $" (~{r:N0} rpm, {FanCurveEditor.Noise(r)})" : "";
            return $"{idle:0}% at {Temp(50)}{rpm}" + (half > 0 ? $" · half speed from {Temp(half)}" : "") + (full > 0 ? $" · full speed from {Temp(full)}" : "")
                + (_mode == FanMode.Custom ? " · this fan only" : ProfileEdited ? " · edited" : " · built in");
        }
    }

    /// <summary>Where the followed temperature sat over the last hour (10th to 90th percentile), for the band under the curve.</summary>
    public double UsageLow => Usage().Low;
    public double UsageHigh => Usage().High;
    private (StateMessage? State, string Source, double Low, double High) usage;
    private (double Low, double High) Usage()
    {
        if (usage.State != State || usage.Source != SourceName) { var (low, high) = ComputeUsage(); usage = (State, SourceName, low, high); }
        return (usage.Low, usage.High);
    }
    private (double Low, double High) ComputeUsage()
    {
        var samples = (State?.History ?? []).Select(s => SourceName switch
        {
            "GPU" => s.GpuTemp, "Highest" => Math.Max(s.CpuTemp ?? double.MinValue, s.GpuTemp ?? double.MinValue), _ => s.CpuTemp
        }).Where(v => v is > -100 and < 150).Select(v => v!.Value).Order().ToArray();
        if (samples.Length < 10) return (double.NaN, double.NaN);
        return (samples[samples.Length / 10], samples[samples.Length * 9 / 10]);
    }

    public string EmptyHeaders => State?.Fans.Where(f => !(f.Rpm is > 0 || f.IsPump)).OrderBy(f => f.Header).Select(f => f.Header.ToString()).ToArray() is { Length: > 0 } empty
        ? $"Nothing connected on header{(empty.Length == 1 ? "" : "s")} {string.Join(", ", empty)}" : "";
    public ObservableCollection<FanGroupItem> Fans { get; } = new();
    public ObservableCollection<FanPoint> Curve { get; } = new();

    public IReadOnlyList<string> ControllableFanNames => Fans.Where(f => !f.IsPump).Select(f => f.Name).ToArray();
    public string SelectedCurveFan { get => SelectedFanName; set { if (value != SelectedFanName && Fans.FirstOrDefault(f => !f.IsPump && f.Name == value) is { } fan) { SelectFan(fan); OnPropertyChanged(); } } }
    public IReadOnlyList<string> PrimarySources { get; } = ["CPU", "GPU", "Hottest"];
    public string SelectedPrimarySource { get => SourceName == "Highest" ? "Hottest" : SourceName; set { if (PrimarySources.Contains(value)) SourceName = value == "Hottest" ? "Highest" : value; } }
    public IReadOnlyList<string> Modes { get; } = ["Silent", "Standard", "Turbo", "Full speed", "Custom"];
    public IReadOnlyList<string> Sources { get; } = ["CPU", "Motherboard", "GPU", "Highest"];

    public RelayCommand<FanGroupItem> SelectFanCommand { get; }
    public RelayCommand<string> SelectModeCommand { get; }
    public RelayCommand ApplyCurveCommand { get; }
    public RelayCommand DiscardCommand { get; }
    /// <summary>"Back to built-in" only when there's a saved edit and nothing unsaved (the bar handles those).</summary>
    public bool ShowReset => CanResetProfile && !HasUnsavedChanges;
    public RelayCommand ResetProfileCommand { get; }
    public RelayCommand CopyCommand { get; }
    public RelayCommand MeasureCommand { get; }

    /// <summary>Silent, Standard and Turbo are profiles, kept per fan: editing Standard on the radiator leaves the case fans' Standard alone.</summary>
    public bool IsProfileMode => !IsPumpSelected && _mode is FanMode.Silent or FanMode.Standard or FanMode.Turbo;
    private bool RunsMode => SelectedFan?.Curve == _mode.ToString();
    public bool ProfileEdited => RunsMode && SelectedConfiguration?.Edited == true;
    public bool CanEditCurve => !IsPumpSelected && _mode != FanMode.Full;
    public bool CanResetProfile => IsProfileMode && (ProfileEdited || curveDirty);
    public bool HasUnsavedChanges => curveDirty && CanEditCurve;
    public string CurveTitle => IsPumpSelected ? "Pump" : _mode switch
    {
        FanMode.Full => SelectedFanName + " · Full speed", FanMode.Custom => SelectedFanName + " · Custom curve", _ => $"{SelectedFanName} · {ModeName} curve" + (ProfileEdited ? " · edited" : "")
    };
    public string CurveHint => IsPumpSelected ? "The pump always runs at 100%." : _mode switch
    {
        FanMode.Full => "Every fan at 100%. Pick Silent, Standard, Turbo or Custom to shape a curve.",
        FanMode.Custom => $"Only {SelectedFanName} uses this curve. Drag a point, or select one and type its values.",
        _ => $"Only {SelectedFanName} changes. Scenes on {ModeName} use this curve for it. Drag a point or type its values."
    };
    public string ApplyLabel => IsProfileMode ? $"Save {ModeName}" : "Apply to " + SelectedFanName;
    /// <summary>The curve the service runs now: the ghost line while editing.</summary>
    public IReadOnlyList<FanPoint>? SavedCurve
    {
        get
        {
            var now = IsPumpSelected ? null : _mode == FanMode.Custom ? (RunsMode ? SelectedConfiguration?.Points : null)
                : RunsMode && SelectedConfiguration is { } running ? running.Points : DefaultCurves[_mode];
            if (now is null || savedCurve is null || !now.SequenceEqual(savedCurve)) savedCurve = now;
            return savedCurve;
        }
    }
    private IReadOnlyList<FanPoint>? savedCurve;
    /// <summary>The other controllable fan, for copying this curve across when both should match.</summary>
    private FanGroupItem? OtherFan => Fans.Count(f => !f.IsPump) == 2 ? Fans.FirstOrDefault(f => !f.IsPump && f.Id != SelectedFanId) : null;
    public bool CanCopy => CanEditCurve && OtherFan is not null;
    public string CopyLabel => OtherFan is { } other ? "Copy to " + other.Name : "";
    public string EditStatus => MeasureText;

    public string SelectedModeName
    {
        get => _selectedModeName;
        set
        {
            if (!Set(ref _selectedModeName, value)) return;
            var mode = ParseMode(value);
            if (mode == _mode) return;
            SaveCurve();
            _mode = mode; undo.Clear();
            curveDirty = _curves.ContainsKey((selectedHeader, mode));
            LoadCurve(mode);
            NotifyAll();
            if (_suppressMode) return;
            _ = SetFanModeAsync(mode);
        }
    }

    public string ModeName => _mode == FanMode.Full ? "Full speed" : _mode.ToString();
    public string SelectedFanId => $"fan:{selectedHeader}";
    private string SelectedFanGroup => GroupOf(selectedHeader);

    public string SelectedFanName
    {
        get => _selectedFanName;
        private set
        {
            if (!Set(ref _selectedFanName, value)) return;
            foreach (var fan in Fans) fan.IsSelected = fan.Id == SelectedFanId;
            IsPumpSelected = selectedHeader == 7;
            LoadCurve(_mode);
            UpdateNow();
        }
    }

    public bool IsPumpSelected { get => _isPumpSelected; private set => Set(ref _isPumpSelected, value); }
    public double NowTemp { get => _nowTemp; private set => Set(ref _nowTemp, value); }
    public double NowPercent { get => _nowPercent; private set => Set(ref _nowPercent, value); }
    public string NowTempText => Format.TempUnit(NowTemp);
    public string NowPercentText => Format.Percent(NowPercent);
    public string CurveSubtitle => $"{(IsPumpSelected ? "Fixed" : ModeName)} curve · now {NowPercentText} at {NowTempText}";

    public string SourceName
    {
        get => _sourceName;
        set { if (Set(ref _sourceName, value)) { curveDirty = true; UpdateNow(); OnPropertiesChanged(nameof(SelectedPrimarySource), nameof(UsageLow), nameof(UsageHigh), nameof(EditStatus)); } }
    }

    public double MinSpeed { get => _minSpeed; set { if (Set(ref _minSpeed, Math.Clamp(value, 0, 60))) { curveDirty = true; OnPropertyChanged(nameof(MinSpeedText)); } } }
    public string MinSpeedText => Format.Percent(MinSpeed);
    public string MeasureText { get => _measureText; private set { if (Set(ref _measureText, value)) OnPropertyChanged(nameof(EditStatus)); } }

    // ---- Measuring ---------------------------------------------------------------------------------------

    public bool IsMeasuring => SelectedFan?.Measuring is not null;
    public bool CanMeasure => !IsPumpSelected && !IsMeasuring && selectedHeader is >= 1 and <= 6;
    public string MeasureLabel => SelectedFan?.Measuring is { } step ? $"Measuring · {step}" : SelectedRpmMap is null ? "Measure fan" : "Measure again";
    public string MeasureHint => SelectedRpmMap is { } map
        ? $"Measured {map.Measured.LocalDateTime:d MMM}: {FanCurveEditor.RpmAt(map, 100):N0} rpm at full speed" + (map.StopsBelow is { } stops ? $", stops below {stops:0}%" : ", never stops")
        : "Steps the fan through its speeds (about 45 s) to show rpm and noise on the curve.";

    // ---- Smoothing ---------------------------------------------------------------------------------------

    // Plain words for the four FanResponse values, in the same order.
    public IReadOnlyList<string> Responses { get; } = ["Instant", "Normal", "Relaxed", "Slow"];
    private static readonly FanResponse[] ResponseValues = [FanResponse.Fast, FanResponse.Normal, FanResponse.Calm, FanResponse.Slow];
    // What the smoothing demo animates: the selected fan's real averaging, hold and ramp.
    public double SmoothAveraging => SelectedConfiguration?.Averaging ?? 4;
    public double SmoothHold => SelectedConfiguration?.Hysteresis ?? 2;
    public double SmoothUp => SelectedConfiguration?.SpinUpPerSecond ?? 30;
    public double SmoothDown => SelectedConfiguration?.SpinDownPerSecond ?? 10;
    public IReadOnlyList<string> Holds { get; } = ["0°", "2°", "4°", "6°"];
    public string SelectedResponse
    {
        get => Responses[Array.IndexOf(ResponseValues, SelectedConfiguration?.Response ?? FanResponse.Normal) is var i and >= 0 ? i : 1];
        set { if (!IsPumpSelected && Responses.ToList().IndexOf(value) is var i and >= 0 && value != SelectedResponse) _ = SetTuningAsync(ResponseValues[i], SelectedConfiguration?.Hysteresis ?? 2); }
    }
    public string SelectedHold
    {
        get => $"{SelectedConfiguration?.Hysteresis ?? 2:0}°";
        set { if (!IsPumpSelected && double.TryParse(value.TrimEnd('°'), out var hold) && value != SelectedHold) _ = SetTuningAsync(SelectedConfiguration?.Response ?? FanResponse.Normal, hold); }
    }
    public string ResponseHint => (SelectedConfiguration?.Response ?? FanResponse.Normal) switch
    {
        FanResponse.Fast => "Follows every change within a second. Loudest choice.",
        FanResponse.Calm => "Ignores spikes up to ~8 s and changes speed gently.",
        FanResponse.Slow => "Only real, lasting heat changes the speed. Quietest choice.",
        _ => "Ignores the CPU's 1–2 s spikes but still reacts within a few seconds."
    };
    public string RampText => SelectedConfiguration is { } c ? $"Up {c.SpinUpPerSecond:0}%/s · down {c.SpinDownPerSecond:0}%/s · {c.Averaging:0} s average" : "Unavailable";
    public string HoldHint => $"Slows down only once the temperature drops {SelectedHold} below where it sped up.";

    // ---- Safety ------------------------------------------------------------------------------------------

    public ObservableCollection<SafetyLine> SafetyLines { get; } = new();
    public string SafetyHistory => State?.Health.LastFailsafe is { } at
        ? $"Overheat guard last stepped in {at.LocalDateTime:d MMM HH:mm} · {State.Health.LastFailsafeReason}" : "The overheat guard hasn't needed to step in since the service started.";
    private void SyncSafety()
    {
        var s = State?.System;
        string Reading(double? value, double limit) => value is { } v ? $"{Format.TempUnit(v, 0)} / {Format.TempUnit(limit, 0)}" : Format.Dash;
        StatusLevel Level(double? value, double limit) => value is not { } v ? StatusLevel.Unavailable : v >= limit ? StatusLevel.Critical : v >= limit - 10 ? StatusLevel.Hot : StatusLevel.Ok;
        var pump = State?.Fans.FirstOrDefault(f => f.IsPump);
        SafetyLine[] lines =
        [
            new("CPU guard", "All fans 100% at 90°", Reading(s?.CpuTemp, 90), Level(s?.CpuTemp, 90)),
            new("GPU guard", "Edge 90°, hotspot 105°", s?.GpuHotspot is { } hot ? $"{Format.TempUnit(s.GpuTemp ?? double.NaN, 0)} · {Format.TempUnit(hot, 0)}" : Reading(s?.GpuTemp, 90), Level(Math.Max(s?.GpuTemp ?? 0, (s?.GpuHotspot ?? 0) - 15), 90)),
            new("Pump", "Always 100%", pump?.Stalled == true ? "Not spinning" : pump?.Rpm is > 0 ? Format.Rpm(pump.Rpm) : Format.Dash,
                pump?.Stalled == true ? StatusLevel.Critical : pump?.Rpm is > 0 ? StatusLevel.Ok : StatusLevel.Unavailable),
            // The overheat guard runs the fans at 100% itself; the BIOS hand-back only happens when Control Spawn stops.
            new("BIOS hand-back", "If Control Spawn stops", "Armed", StatusLevel.Ok)
        ];
        // Only rows whose text changed are replaced, so the rest of the card isn't rebuilt every second.
        for (int i = 0; i < lines.Length; i++)
            if (i >= SafetyLines.Count) SafetyLines.Add(lines[i]); else if (SafetyLines[i] != lines[i]) SafetyLines[i] = lines[i];
    }

    // ---- State -------------------------------------------------------------------------------------------

    protected override void Refresh()
    {
        var state = State;
        if (state is null) { NotifyAll(); return; }

        _selectedFanName = state.Fans.FirstOrDefault(f => f.Header == selectedHeader)?.Name ?? _selectedFanName;
        var currentFans = new List<FanGroupItem>();
        foreach (var fan in state.Fans.Where(f => (f.Controllable || f.IsPump) && (f.Rpm is > 0 || f.IsPump || f.Stalled)))
        {
            var percent = fan.IsPump ? 100 : Math.Clamp(fan.Percent ?? double.NaN, 0, 100);
            var spin = Math.Max(0.3, 3000.0 / Math.Max(fan.Rpm ?? 300, 300));
            var next = new FanGroupItem(fan.Name, $"Header {fan.Header} · {fan.Name}", Format.RpmShort(fan.Rpm), percent, fan.IsPump, spin, fan.Header);
            var existing = Fans.FirstOrDefault(f => f.Id == next.Id);
            if (existing is null)
            {
                if (!next.IsPump) { var card = next; next.ModeOptions = Modes.Select(m => new FanModeChoice(m, new RelayCommand(() => ChooseCardMode(card, m)))).ToArray(); }
                Fans.Add(next); existing = next;
            }
            else existing.Update(next, notify: false);
            // The last two minutes is all a fan row's sparkline shows; the full hour cost thousands of points per fan each second.
            existing.SetHistory((state.History ?? []).TakeLast(120).Select(s => s.FanRpms?.GetValueOrDefault(fan.Header)).Where(v => v.HasValue && double.IsFinite(v.Value)).Select(v => v!.Value), notify: false);
            existing.SetControl(fan, notify: false);
            existing.Changed();
            currentFans.Add(existing);
        }
        foreach (var removed in Fans.Where(f => !currentFans.Contains(f)).ToArray()) Fans.Remove(removed);
        foreach (var fan in Fans) fan.IsSelected = fan.Id == SelectedFanId;
        if (Fans.Count > 0 && Fans.All(f => !f.IsSelected))
        { _selectedFanName = Fans[0].Name; selectedHeader = int.Parse(Fans[0].Id.AsSpan(4)); Fans[0].IsSelected = true; }
        IsPumpSelected = selectedHeader == 7;

        var reportedMode = Enum.TryParse<FanMode>(state.Fans.FirstOrDefault(f => f.Header == selectedHeader)?.Curve, out var headerMode) ? headerMode : state.FanMode;
        if (reportedMode != _mode)
        {
            _suppressMode = true;
            _mode = reportedMode; undo.Clear();
            _selectedModeName = _mode == FanMode.Full ? "Full speed" : _mode.ToString();
            curveDirty = _curves.ContainsKey((selectedHeader, _mode));
            LoadCurve(_mode);
            _suppressMode = false;
        }

        if (!curveDirty && SelectedConfiguration is { } config && !IsPumpSelected)
        {
            var reported = Normalize(config.Points);
            if (!Curve.SequenceEqual(reported)) { Curve.Clear(); foreach (var point in reported) Curve.Add(point); }
            _sourceName = SourceText(config.Source);
            _minSpeed = config.Minimum;
        }
        // The pump runs at a fixed 100%, so its curve is always the flat full-speed line.
        else if (IsPumpSelected && !Curve.SequenceEqual(DefaultCurves[FanMode.Full])) LoadCurve(_mode);
        UpdateNow();
        SyncSafety();
        NotifyAll();
    }

    private static string SourceText(TemperatureSource source) => source switch { TemperatureSource.Cpu => "CPU", TemperatureSource.Gpu => "GPU", TemperatureSource.Motherboard => "Motherboard", _ => "Highest" };

    public void OnCurveEdited()
    {
        curveDirty = true;
        SaveCurve();
        UpdateNow();
        OnPropertiesChanged(nameof(CurveSubtitle), nameof(NowPercent), nameof(NowPercentText), nameof(SelectedTemperatureText), nameof(SelectedPercentText),
            nameof(ProfileSummary), nameof(CanResetProfile), nameof(HasUnsavedChanges), nameof(ShowReset), nameof(EditStatus), nameof(CanUndo), nameof(CurveTitle));
    }

    /// <summary>A card's profile chip: picks that fan and puts it on the profile, so the curve below shows what it now runs.</summary>
    private void ChooseCardMode(FanGroupItem item, string mode)
    {
        if (item.IsPump) return;
        if (item.Id != SelectedFanId) SelectFan(item);
        SelectedModeName = mode;
    }

    private void SelectFan(FanGroupItem? item)
    {
        if (item is null) return;
        string name = item.Name;
        SaveCurve();
        curveDirty = false; undo.Clear();
        selectedHeader = int.TryParse(item.Id.AsSpan(4), out var header) && header > 0 ? header : State?.Fans.FirstOrDefault(f => f.Name == name)?.Header ?? selectedHeader;
        foreach (var fan in Fans) fan.IsSelected = fan.Id == SelectedFanId;
        IsPumpSelected = selectedHeader == 7;
        SelectedFanName = name;
        // Each fan keeps its own unsaved edits; otherwise it shows the curve it runs.
        if (!IsPumpSelected && _curves.ContainsKey((selectedHeader, _mode))) { curveDirty = true; LoadCurve(_mode); }
        else if (SelectedConfiguration is { } config && !IsPumpSelected)
        {
            Curve.Clear(); foreach (var point in Normalize(config.Points)) Curve.Add(point);
            _sourceName = SourceText(config.Source);
            _minSpeed = config.Minimum;
        }
        NotifyAll();
    }

    private void UpdateNow()
    {
        var state = State;
        if (state is null) return;
        var fan = SelectedFan;
        // The curve reads the averaged temperature, so the marker sits where the fan actually is on it.
        NowTemp = fan?.Following ?? SourceName switch
        {
            "Motherboard" => state.System.BoardTemp ?? double.NaN,
            "GPU" => state.System.GpuTemp ?? double.NaN,
            "Highest" => new[] { state.System.CpuTemp, state.System.GpuTemp, state.System.BoardTemp }.Max() ?? double.NaN,
            _ => state.System.CpuTemp ?? double.NaN
        };
        NowPercent = IsPumpSelected ? 100 : fan?.Percent ?? double.NaN;
        OnPropertiesChanged(nameof(NowTempText), nameof(NowPercentText), nameof(CurveSubtitle));
    }

    private void SaveCurve()
    {
        if (!IsPumpSelected && Curve.Count == 8 && curveDirty) _curves[(selectedHeader, _mode)] = Curve.ToList();
    }

    private void LoadCurve(FanMode mode)
    {
        IEnumerable<FanPoint> points = IsPumpSelected
            ? DefaultCurves[FanMode.Full]
            : _curves.TryGetValue((selectedHeader, mode), out var stored) ? stored
            : SelectedFan?.Curve == mode.ToString() && SelectedConfiguration is { } running ? running.Points : DefaultCurves[mode];
        Curve.Clear();
        foreach (var point in Normalize(points.ToArray())) Curve.Add(point);
    }

    /// <summary>
    /// Moves a curve onto the 30–100 °C the page draws without changing it there: points below 30 °C collapse into one at
    /// 30 °C (at the curve's value there), and the freed slots go in the middle of the widest gaps, on the existing line.
    /// </summary>
    public static IReadOnlyList<FanPoint> Normalize(IReadOnlyList<FanPoint> points)
    {
        if (points.Count != 8 || points[0].Temperature >= FanCurveEditor.TempMin) return points;
        var kept = points.Where(p => p.Temperature > FanCurveEditor.TempMin).ToList();
        kept.Insert(0, new(FanCurveEditor.TempMin, Math.Round(DutyAt(points, FanCurveEditor.TempMin), 1)));
        while (kept.Count < 8)
        {
            int widest = Enumerable.Range(0, kept.Count - 1).MaxBy(i => kept[i + 1].Temperature - kept[i].Temperature);
            double t = Math.Round((kept[widest].Temperature + kept[widest + 1].Temperature) / 2);
            kept.Insert(widest + 1, new(t, Math.Round(DutyAt(points, t), 1)));
        }
        return kept;
    }

    private static FanMode ParseMode(string name) => name switch
    {
        "Full speed" => FanMode.Full,
        "Full" => FanMode.Full,
        _ => Enum.TryParse<FanMode>(name, ignoreCase: true, out var mode) ? mode : FanMode.Standard
    };

    public static double DutyAt(IReadOnlyList<FanPoint> points, double temperature)
    {
        if (points.Count == 0) return 0;
        if (temperature <= points[0].Temperature) return points[0].Percent;
        for (var i = 1; i < points.Count; i++)
        {
            if (temperature > points[i].Temperature) continue;
            var a = points[i - 1];
            var b = points[i];
            var span = b.Temperature - a.Temperature;
            return span <= 0 ? b.Percent : a.Percent + (b.Percent - a.Percent) * (temperature - a.Temperature) / span;
        }
        return points[^1].Percent;
    }

    // ---- Commands to the service -------------------------------------------------------------------------

    private async Task SetFanModeAsync(FanMode mode)
    {
        if (IsPumpSelected) return;
        await RunAsync(() => Client.SetFanModeAsync(SelectedFanGroup, mode));
    }

    private async Task SetTuningAsync(FanResponse response, double hold)
    {
        await RunAsync(() => Client.SendAsync(Commands.SetFanTuning, new { group = SelectedFanGroup, response = response.ToString(), hold }));
        MeasureText = HasError ? LastError! : $"{SelectedFanName}: {Responses[Array.IndexOf(ResponseValues, response)].ToLowerInvariant()}, waits {hold:0}°";
    }

    /// <summary>Saves the edited curve: to the profile for Silent, Standard and Turbo, or to this fan alone for Custom.</summary>
    public async Task SaveAsync()
    {
        if (Curve.Count != 8 || !CanEditCurve) return;
        SaveCurve();
        var source = SourceName switch
        {
            "Motherboard" => TemperatureSource.Motherboard,
            "GPU" => TemperatureSource.Gpu,
            "Highest" => TemperatureSource.Maximum,
            _ => TemperatureSource.Cpu
        };
        var points = Curve.Select(p => new FanPoint(p.Temperature, Math.Clamp(p.Percent, MinSpeed, 100))).ToList();
        if (IsProfileMode)
        {
            // Saving a profile keeps the fan on it (Apply used to switch the fan to Custom); the source stays per fan.
            var mode = _mode;
            await RunAsync(() => Client.SendAsync(Commands.SetFanProfile, new { mode = mode.ToString(), group = SelectedFanGroup, points = points.Select(p => new[] { p.Temperature, p.Percent }).ToArray() }));
            if (!HasError && SelectedConfiguration is { } current && current.Source != source)
                await RunAsync(() => Client.SendAsync(Commands.SetFanMode, new { group = SelectedFanGroup, mode = mode.ToString(), source = source.ToString() }));
            if (!HasError) { _curves.Remove((selectedHeader, mode)); curveDirty = false; }
            MeasureText = HasError ? LastError! : $"{ModeName} saved for {SelectedFanName}";
        }
        else
        {
            await RunAsync(() => Client.SetFanCurveAsync(SelectedFanGroup, points, source));
            if (!HasError) curveDirty = false;
            MeasureText = HasError ? LastError! : "Curve applied";
        }
        undo.Clear();
        NotifyAll();
    }
    private Task ApplyCurveAsync() => SaveAsync();

    /// <summary>Drops unsaved edits and shows the saved curve again.</summary>
    public void Discard()
    {
        _curves.Remove((selectedHeader, _mode)); curveDirty = false; undo.Clear();
        if (SavedCurve is { } saved) { Curve.Clear(); foreach (var p in Normalize(saved)) Curve.Add(p); }
        NotifyAll();
    }

    /// <summary>Puts the profile back to its built-in curve, dropping saved and unsaved edits.</summary>
    private async Task ResetProfileAsync()
    {
        if (!IsProfileMode) return;
        var mode = _mode;
        if (ProfileEdited) await RunAsync(() => Client.SendAsync(Commands.SetFanProfile, new { mode = mode.ToString(), group = SelectedFanGroup, reset = true }));
        if (HasError) { MeasureText = LastError!; return; }
        _curves.Remove((selectedHeader, mode)); curveDirty = false; undo.Clear();
        Curve.Clear(); foreach (var point in DefaultCurves[mode]) Curve.Add(point);
        MeasureText = $"{ModeName} is back to its built-in curve";
        NotifyAll();
    }

    /// <summary>Gives the other fan this curve (as shown, saved or not): the same profile, or its own Custom curve.</summary>
    private async Task CopyAsync()
    {
        if (!CanCopy || OtherFan is not { } other) return;
        string group = other.Id == "fan:2" ? "Radiator" : other.Id == "fan:6" ? "Case fans" : other.Id;
        var points = Curve.Select(p => new FanPoint(p.Temperature, Math.Clamp(p.Percent, MinSpeed, 100))).ToList();
        if (IsProfileMode)
            await RunAsync(() => Client.SendAsync(Commands.SetFanProfile, new { mode = _mode.ToString(), group, points = points.Select(p => new[] { p.Temperature, p.Percent }).ToArray() }));
        else await RunAsync(() => Client.SetFanCurveAsync(group, points, SelectedConfiguration?.Source ?? TemperatureSource.Cpu));
        if (!HasError) _curves.Remove((int.Parse(other.Id.AsSpan(4)), _mode));
        MeasureText = HasError ? LastError! : $"{other.Name} now uses this {(IsProfileMode ? ModeName : "custom")} curve";
    }

    private async Task MeasureAsync()
    {
        if (!CanMeasure) return;
        int header = selectedHeader;
        await RunAsync(() => Client.SendAsync(Commands.MeasureFan, new { header }));
        MeasureText = HasError ? LastError! : $"Measuring {SelectedFanName}: it will speed up and slow down for about 45 s";
    }
}

public sealed record FanModeChoice(string Name, RelayCommand Command);

public sealed record SafetyLine(string Title, string Detail, string Reading, StatusLevel Level);
