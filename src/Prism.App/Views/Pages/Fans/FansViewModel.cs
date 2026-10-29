using System.Collections.ObjectModel;
using Prism.App.Common;
using Prism.App.Services;
using Prism.Core;
using Prism.Core.Ipc;

namespace Prism.App.ViewModels;

public sealed class FansViewModel : PageViewModel
{
    private static readonly IReadOnlyDictionary<FanMode, FanPoint[]> DefaultCurves = new Dictionary<FanMode, FanPoint[]>
    {
        [FanMode.Silent] = [new(20, 15), new(30, 18), new(40, 22), new(50, 30), new(60, 42), new(70, 60), new(80, 80), new(90, 100)],
        [FanMode.Standard] = [new(20, 20), new(30, 26), new(40, 34), new(50, 45), new(60, 60), new(67, 80), new(70, 100), new(100, 100)],
        [FanMode.Turbo] = [new(20, 35), new(30, 42), new(40, 50), new(50, 62), new(60, 75), new(65, 90), new(70, 100), new(100, 100)],
        [FanMode.Full] = [new(0, 100), new(20, 100), new(40, 100), new(60, 100), new(80, 100), new(90, 100), new(95, 100), new(100, 100)],
        [FanMode.Custom] = [new(20, 25), new(35, 25), new(45, 35), new(55, 50), new(65, 70), new(72, 90), new(80, 100), new(100, 100)]
    };

    private readonly Dictionary<FanMode, List<FanPoint>> _curves = new();
    private FanMode _mode = FanMode.Standard;
    private string _selectedModeName = "Standard";
    private string _selectedFanName = "Radiator";
    private int selectedHeader = 2;
    private bool _suppressMode;
    private double _nowTemp = 48;
    private double _nowPercent = 45;
    private string _sourceName = "CPU";
    private double _smoothUp = 30;
    private double _smoothDown = 60;
    private double _minSpeed = 33;
    private string _measureText = string.Empty;
    private bool _isPumpSelected;

    public FansViewModel(IPrismClient client) : base(client)
    {
        PageTitle = "Fans";
        foreach (var pair in DefaultCurves) _curves[pair.Key] = new List<FanPoint>(pair.Value);

        SelectFanCommand = new RelayCommand<FanGroupItem>(item => SelectFan(item));
        SelectModeCommand = new RelayCommand<string>(name => SelectedModeName = name ?? "Standard");
        ApplyCurveCommand = new RelayCommand(() => _ = ApplyCurveAsync());
        MeasureCommand = new RelayCommand(() => _ = MeasureAsync());
        Curve.CollectionChanged += (_, _) => UpdateNow();
        LoadCurve(_mode);
        Refresh();
    }

    public string Headline => State is null ? "Waiting for your fans" : State.Health.FanFailsafeActive ? "BIOS is keeping things safe" : "Cool hardware. Quiet room.";
    public string SafetyText => State?.Health.FanFailsafeActive == true ? "Failsafe active ? BIOS control" : "BIOS hand-back is armed";
    public IReadOnlyList<FanGroupItem> HeaderRows => State?.Fans.Select(f => new FanGroupItem(f.Name, $"HEADER {f.Header}", Format.Rpm(f.Rpm), f.IsPump ? 100 : f.Percent ?? 0, f.IsPump, 1, f.Header)).ToArray() ?? [];
    public ObservableCollection<FanGroupItem> Fans { get; } = new();
    public ObservableCollection<FanPoint> Curve { get; } = new();

    public IReadOnlyList<string> Modes { get; } = ["Silent", "Standard", "Turbo", "Full speed", "Custom"];
    public IReadOnlyList<string> Sources { get; } = ["CPU", "Motherboard", "GPU", "Highest"];

    public RelayCommand<FanGroupItem> SelectFanCommand { get; }
    public RelayCommand<string> SelectModeCommand { get; }
    public RelayCommand ApplyCurveCommand { get; }
    public RelayCommand MeasureCommand { get; }

    public string SelectedModeName
    {
        get => _selectedModeName;
        set
        {
            if (!Set(ref _selectedModeName, value)) return;
            var mode = ParseMode(value);
            if (mode == _mode) return;
            SaveCurve();
            _mode = mode;
            LoadCurve(mode);
            OnPropertyChanged(nameof(ModeName));
            OnPropertyChanged(nameof(CurveSubtitle));
            if (_suppressMode) return;
            _ = SetFanModeAsync(mode);
        }
    }

    public string ModeName => _mode == FanMode.Full ? "Full speed" : _mode.ToString();

    public string SelectedFanId => $"fan:{selectedHeader}";
    private string SelectedFanGroup => selectedHeader == 2 ? "Radiator" : selectedHeader == 6 ? "Case fans" : SelectedFanId;

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

    public double NowTemp
    {
        get => _nowTemp;
        private set => Set(ref _nowTemp, value);
    }

    public double NowPercent
    {
        get => _nowPercent;
        private set => Set(ref _nowPercent, value);
    }

    public string NowTempText => Format.TempUnit(NowTemp);
    public string NowPercentText => Format.Percent(NowPercent);
    public string CurveSubtitle => $"{(IsPumpSelected ? "Fixed" : ModeName)} curve · now {NowPercentText} at {NowTempText}";

    public string SourceName
    {
        get => _sourceName;
        set => Set(ref _sourceName, value);
    }

    public double SmoothUp { get => _smoothUp; set => Set(ref _smoothUp, Math.Clamp(value, 0, 100)); }
    public double SmoothDown { get => _smoothDown; set => Set(ref _smoothDown, Math.Clamp(value, 0, 100)); }
    public double MinSpeed { get => _minSpeed; set { if (Set(ref _minSpeed, Math.Clamp(value, 0, 60))) OnPropertyChanged(nameof(MinSpeedText)); } }
    public string SmoothUpText => Format.Percent(SmoothUp);
    public string SmoothDownText => Format.Percent(SmoothDown);
    public string MinSpeedText => Format.Percent(MinSpeed);

    public string MeasureText { get => _measureText; private set => Set(ref _measureText, value); }

    protected override void Refresh()
    {
        var state = State;
        if (state is null)
        {
            NotifyAll();
            return;
        }

        _selectedFanName = state.Fans.FirstOrDefault(f => f.Header == selectedHeader)?.Name ?? _selectedFanName;
        var currentFans = new List<FanGroupItem>();
        foreach (var fan in state.Fans.Where(f => (f.Controllable || f.IsPump) && (f.Rpm is > 0 || f.IsPump)))
        {
            var percent = fan.IsPump ? 100 : Math.Clamp(fan.Percent ?? 0, 0, 100);
            var spin = Math.Max(0.3, 3000.0 / Math.Max(fan.Rpm ?? 300, 300));
            var next = new FanGroupItem(fan.Name,
                $"Header {fan.Header} · {fan.Name}",
                Format.RpmShort(fan.Rpm), percent, fan.IsPump, spin, fan.Header);
            var existing = Fans.FirstOrDefault(f => f.Id == next.Id);
            if (existing is null) { Fans.Add(next); existing = next; } else existing.Update(next);
            currentFans.Add(existing);
        }
        foreach (var removed in Fans.Where(f => !currentFans.Contains(f)).ToArray()) Fans.Remove(removed);
        foreach (var fan in Fans) fan.IsSelected = fan.Id == SelectedFanId;
        if (Fans.Count > 0 && Fans.All(f => !f.IsSelected))
        { _selectedFanName = Fans[0].Name; selectedHeader = int.Parse(Fans[0].Id.AsSpan(4)); Fans[0].IsSelected = true; }
        IsPumpSelected = selectedHeader == 7;

        if (state.FanMode != _mode)
        {
            _suppressMode = true;
            _mode = state.FanMode;
            _selectedModeName = _mode == FanMode.Full ? "Full speed" : _mode.ToString();
            LoadCurve(_mode);
            _suppressMode = false;
            OnPropertyChanged(nameof(SelectedModeName));
            OnPropertyChanged(nameof(ModeName));
        }

        UpdateNow();
        NotifyAll();
    }

    public void OnCurveEdited()
    {
        SaveCurve();
        UpdateNow();
        OnPropertyChanged(nameof(CurveSubtitle));
        OnPropertyChanged(nameof(NowPercent));
        OnPropertyChanged(nameof(NowPercentText));
    }

    private void SelectFan(FanGroupItem? item)
    {
        if (item is null) return;
        string name = item.Name;
        SaveCurve();
        selectedHeader = int.TryParse(item.Id.AsSpan(4), out var header) && header > 0 ? header : State?.Fans.FirstOrDefault(f => f.Name == name)?.Header ?? selectedHeader;
        foreach (var fan in Fans) fan.IsSelected = fan.Id == SelectedFanId;
        IsPumpSelected = selectedHeader == 7;
        SelectedFanName = name;
        OnPropertyChanged(nameof(SelectedFanId));
        OnPropertyChanged(nameof(CurveSubtitle));
    }

    private void UpdateNow()
    {
        var state = State;
        if (state is null) return;
        NowTemp = SourceName switch
        {
            "Motherboard" => state.System.BoardTemp ?? 0,
            "GPU" => state.System.GpuTemp ?? 0,
            "Highest" => new[] { state.System.CpuTemp ?? 0, state.System.GpuTemp ?? 0, state.System.BoardTemp ?? 0 }.Max(),
            _ => state.System.CpuTemp ?? 0
        };
        NowPercent = IsPumpSelected ? 100 : DutyAt(Curve, NowTemp);
        OnPropertyChanged(nameof(NowTempText));
        OnPropertyChanged(nameof(NowPercentText));
        OnPropertyChanged(nameof(CurveSubtitle));
    }

    private void SaveCurve()
    {
        if (!IsPumpSelected && Curve.Count == 8) _curves[_mode] = Curve.ToList();
    }

    private void LoadCurve(FanMode mode)
    {
        IEnumerable<FanPoint> points = IsPumpSelected
            ? DefaultCurves[FanMode.Full]
            : _curves.TryGetValue(mode, out var stored) ? stored : DefaultCurves[mode];
        Curve.Clear();
        foreach (var point in points) Curve.Add(point);
    }

    private static FanMode ParseMode(string name) => name switch
    {
        "Full speed" => FanMode.Full,
        "Full" => FanMode.Full,
        _ => Enum.TryParse<FanMode>(name, ignoreCase: true, out var mode) ? mode : FanMode.Standard
    };

    private static double DutyAt(IReadOnlyList<FanPoint> points, double temperature)
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

    private async Task SetFanModeAsync(FanMode mode)
    {
        if (IsPumpSelected) return;
        await RunAsync(() => Client.SetFanModeAsync(SelectedFanGroup, mode));
    }

    private async Task ApplyCurveAsync()
    {
        if (Curve.Count != 8 || IsPumpSelected) return;
        SaveCurve();
        var source = SourceName switch
        {
            "Motherboard" => TemperatureSource.Motherboard,
            "GPU" => TemperatureSource.Gpu,
            "Highest" => TemperatureSource.Maximum,
            _ => TemperatureSource.Cpu
        };
        var points = Curve.Select(p => new FanPoint(p.Temperature, Math.Clamp(p.Percent, MinSpeed, 100))).ToList();
        await RunAsync(() => Client.SetFanCurveAsync(SelectedFanGroup, points, source));
        MeasureText = HasError ? LastError! : "Curve applied";
    }

    private async Task MeasureAsync()
    {
        MeasureText = "Fan calibration is not supported by this service protocol.";
        await Task.CompletedTask;
    }
}
