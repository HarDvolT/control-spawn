using System.Collections.ObjectModel;
using System.Windows.Media;
using Prism.App.Common;
using Prism.App.Services;
using Prism.Core.Ipc;

namespace Prism.App.ViewModels;

/// <summary>
/// The modem page: Simple / Detailed / Scan / Speed views, a live ping strip,
/// the spike detective and the connected-device list.
/// </summary>
public sealed class ModemViewModel : PageViewModel
{
    private const int HistoryLength = 60;
    private const int PingLength = 40;

    private readonly List<double> _rsrpHistory = new();
    private readonly List<double> _sinrHistory = new();
    private readonly List<double> _pingHistory = new();

    private string _selectedView = "Detailed";
    private bool _connected;
    private string _network = Format.Dash;
    private string _band = Format.Dash;
    private double _score = double.NaN;
    private int _bars;
    private string _verdict = Format.Dash;
    private string _sentence = string.Empty;
    private string _verdictColor = ColorUtil.ToHex(ColorUtil.TextTertiary);
    private double _rsrp5 = double.NaN;
    private double _sinr5 = double.NaN;
    private double _rsrq5 = double.NaN;
    private double _rsrp4 = double.NaN;
    private double _sinr4 = double.NaN;
    private double _rsrq4 = double.NaN;
    private double _ping = double.NaN;
    private double _loss = double.NaN;
    private string _pingNowText = Format.Dash;
    private string _pingColor = ColorUtil.ToHex(ColorUtil.Good);
    private string _spikeCountText = "0 spikes";
    private bool _spikesOpen;
    private string _copyLabel = "Copy diagnostics";
    private double _speedProgress;
    private bool _speedRunning;
    private string _speedStatus = "Idle · no test yet";
    private string _trendText = "Waiting for the service";
    private string _trendColor = ColorUtil.ToHex(ColorUtil.TextTertiary);
    private string _sinrNow = Format.Dash;
    private string _sinrColor = ColorUtil.ToHex(ColorUtil.TextTertiary);
    private double _sinrKnob;

    public ModemViewModel(IPrismClient client) : base(client)
    {
        PageTitle = "Modem";
        Views = ["Simple", "Detailed", "Scan", "Speed"];

        ToggleSpikesCommand = new RelayCommand(() => SpikesOpen = !SpikesOpen);
        CopyCommand = new RelayCommand(CopyDiagnostics);
        RunSpeedTestCommand = new RelayCommand(() => _ = RunSpeedTestAsync());
        RefreshPing();
        Refresh();
    }

    private string selectedRadio = "5G";
    private double download = double.NaN, upload = double.NaN;
    public double Download => download;
    public string UploadText => "Upload " + Format.Mbps(upload);
    public string NetworkChip => Network + " \u00b7 " + Band;
    public string PingChip => "Ping " + Format.Ms(_ping);
    public string ConnectionChip => Connected ? "Connected" : "Offline";
    public IReadOnlyList<string> Radios { get; } = ["5G", "LTE anchor"];
    public string SelectedRadio { get => selectedRadio; set { if (Set(ref selectedRadio, value)) OnPropertyChanged(nameof(RadioMetrics)); } }
    public IReadOnlyList<MetricRow> RadioMetrics => MetricGroups.Select(g => g.Rows[SelectedRadio == "5G" ? 0 : 1] with { Net = g.Code }).Append(new("RSSI", Format.Dash, "Unavailable", "#7C808A", double.NaN)).ToArray();
    public IReadOnlyList<double> PingHistory => _pingHistory;
    public string AveragePing => _pingHistory.Count > 0 ? Format.Ms(_pingHistory.Average()) : Format.Dash;
    public string JitterText => _pingHistory.Count > 1 ? Format.Ms(_pingHistory.Zip(_pingHistory.Skip(1), (a, b) => Math.Abs(a - b)).Average()) : Format.Dash;
    public IReadOnlyList<StatRow> PingStats => [new("Now", PingNowText, "#36D6FF"), new("Average", AveragePing, "#F4F5F7"), new("Jitter", JitterText, "#F4F5F7"), new("Lost", LossText, "#3DDC84")];
    public IReadOnlyList<StatRow> CellStats => [new("Band", Band, "#F4F5F7"), new("Bandwidth", Format.Dash, "#7C808A"), new("PCI", PciText, "#7C808A"), new("Cell ID", EnodebText, "#7C808A"), new("ARFCN", Format.Dash, "#7C808A"), new("Anchor", AnchorText, "#7C808A"), new("Carrier aggregation", Format.Dash, "#7C808A"), new("Tower distance", Format.Dash, "#7C808A")];

    public IReadOnlyList<string> Views { get; }
    public ObservableCollection<SimpleNetworkRow> SimpleRows { get; } = new();
    public ObservableCollection<MetricGroup> MetricGroups { get; } = new();
    public ObservableCollection<LaneData> Lanes { get; } = new();
    public ObservableCollection<NeighborRow> Neighbors { get; } = new();
    public ObservableCollection<PingBar> Pings { get; } = new();
    public ObservableCollection<SpikeItem> Spikes { get; } = new();
    public ObservableCollection<DeviceRow> Devices { get; } = new();
    public ObservableCollection<SpeedStat> SpeedStats { get; } = new();
    public ObservableCollection<BarItem> Bars { get; } = new();
    public ObservableCollection<SpikeItem> SpeedHistory { get; } = new();

    public RelayCommand ToggleSpikesCommand { get; }
    public RelayCommand CopyCommand { get; }
    public RelayCommand RunSpeedTestCommand { get; }

    public string SelectedView
    {
        get => _selectedView;
        set
        {
            if (!Set(ref _selectedView, value)) return;
            OnPropertiesChanged(nameof(IsSimple), nameof(IsDetailed), nameof(IsScan), nameof(IsSpeed));
        }
    }

    public bool IsSimple => _selectedView == "Simple";
    public bool IsDetailed => _selectedView == "Detailed";
    public bool IsScan => _selectedView == "Scan";
    public bool IsSpeed => _selectedView == "Speed";

    public bool Connected { get => _connected; private set => Set(ref _connected, value); }
    public string Network { get => _network; private set => Set(ref _network, value); }
    public string Band { get => _band; private set => Set(ref _band, value); }
    public double Score { get => _score; private set => Set(ref _score, value); }
    public string ScoreText => Format.Number(Score);
    public int BarCount { get => _bars; private set => Set(ref _bars, value); }
    public string BarsText => $"{BarCount} of 5 bars";
    public string Verdict { get => _verdict; private set => Set(ref _verdict, value); }
    public string Sentence { get => _sentence; private set => Set(ref _sentence, value); }
    public string VerdictColor { get => _verdictColor; private set => Set(ref _verdictColor, value); }
    public string PingNowText { get => _pingNowText; private set => Set(ref _pingNowText, value); }
    public string PingColor { get => _pingColor; private set => Set(ref _pingColor, value); }
    public string SpikeCountText { get => _spikeCountText; private set => Set(ref _spikeCountText, value); }
    public string LossText => Format.Percent(_loss, 1);
    public bool SpikesOpen { get => _spikesOpen; private set => Set(ref _spikesOpen, value); }
    public string CopyLabel { get => _copyLabel; private set => Set(ref _copyLabel, value); }
    public double SpeedProgress { get => _speedProgress; private set => Set(ref _speedProgress, value); }
    public bool SpeedRunning { get => _speedRunning; private set => Set(ref _speedRunning, value); }
    public string SpeedStatus { get => _speedStatus; private set => Set(ref _speedStatus, value); }
    public string TrendText { get => _trendText; private set => Set(ref _trendText, value); }
    public string TrendColor { get => _trendColor; private set => Set(ref _trendColor, value); }
    public string SinrNow { get => _sinrNow; private set => Set(ref _sinrNow, value); }
    public string SinrColor { get => _sinrColor; private set => Set(ref _sinrColor, value); }
    public double SinrKnob { get => _sinrKnob; private set => Set(ref _sinrKnob, value); }

    public string Headline => Connected ? $"5G signal is {Verdict.ToLowerInvariant()}" : "Waiting for your router";
    public string BandText => Band;
    public string AnchorText => Format.Dash;
    public string PciText => Format.Dash;
    public string EnodebText => Format.Dash;
    public string DevicesOnlineText => $"{Devices.Count(d => d.DotColorHex != ColorUtil.ToHex(ColorUtil.TextTertiary))} online";
    public string ServiceLine => Connected ? $"{Network} · updated every 1 s" : "Router not connected · retrying";

    protected override void Refresh()
    {
        var router = State?.Router;
        if (router is null)
        {
            Connected = false;
            Score = double.NaN; BarCount = 0; Band = Format.Dash;
            Sentence = "Router telemetry unavailable";
            _rsrp5 = _sinr5 = _rsrq5 = _rsrp4 = _sinr4 = _rsrq4 = _ping = _loss = double.NaN;
            _rsrpHistory.Clear(); _sinrHistory.Clear(); _pingHistory.Clear();
            BuildSimpleRows(); BuildMetrics(); BuildLanes(); BuildNeighbors(); BuildBars();
            RefreshPing(); RefreshTrend();
            Verdict = Format.Dash;
            Network = Format.Dash;
            BuildPlaceholderRows();
            NotifyAll();
            return;
        }

        Connected = router.Connected;
        Network = router.Network ?? Format.Dash;
        Band = router.Band ?? Format.Dash;
        Score = router.Score ?? double.NaN;
        BarCount = router.Bars ?? 0;
        Verdict = router.Verdict ?? Format.Dash;
        Sentence = router.Sentence ?? string.Empty;
        VerdictColor = ColorUtil.ToHex(Verdict == "Good" ? ColorUtil.Good : Verdict == "Fair" ? ColorUtil.Fair : Verdict == "Poor" ? ColorUtil.Poor : ColorUtil.TextTertiary);

        _rsrp5 = router.Rsrp5g ?? double.NaN;
        _sinr5 = router.Sinr5g ?? double.NaN;
        _rsrq5 = router.Rsrq5g ?? double.NaN;
        _rsrp4 = router.Rsrp4g ?? double.NaN;
        _sinr4 = router.Sinr4g ?? double.NaN;
        _rsrq4 = router.Rsrq4g ?? double.NaN;
        _ping = router.PingMs ?? double.NaN;
        _loss = router.PingLossPercent ?? double.NaN;

        if (double.IsFinite(_rsrp5)) Push(_rsrpHistory, Normalize(_rsrp5, -140, -44));
        if (double.IsFinite(_sinr5)) Push(_sinrHistory, Normalize(_sinr5, -10, 30));
        if (State?.History is { } history)
        {
            _pingHistory.Clear();
            _pingHistory.AddRange(history.TakeLast(60).Where(s => s.PingMs.HasValue && double.IsFinite(s.PingMs.Value)).Select(s => s.PingMs!.Value));
        }
        else if (double.IsFinite(_ping)) Push(_pingHistory, _ping);

        BuildBars();
        BuildSimpleRows();
        BuildMetrics();
        BuildLanes();
        BuildNeighbors();
        BuildDevices(router);
        RefreshPing();
        RefreshTrend();

        SinrNow = Format.Db(_sinr5);
        SinrColor = ColorUtil.ToHex(RatingColor("sinr", _sinr5));
        SinrKnob = Normalize(_sinr5, -10, 30);

        OnPropertiesChanged(nameof(ScoreText), nameof(BarsText), nameof(LossText), nameof(BandText),
            nameof(AnchorText), nameof(PciText), nameof(EnodebText), nameof(ServiceLine), nameof(DevicesOnlineText));
        NotifyAll();
    }

    private void BuildBars()
    {
        Bars.Clear();
        for (var i = 0; i < 5; i++)
        {
            var on = i < BarCount;
            Bars.Add(new BarItem(8 + i * 5, ColorUtil.ToHex(on ? RatingColorFromVerdict() : Color.FromRgb(0x3A, 0x3A, 0x40))));
        }
    }

    private System.Windows.Media.Color RatingColorFromVerdict() =>
        Verdict == "Good" ? ColorUtil.Good : Verdict == "Fair" ? ColorUtil.Fair : ColorUtil.Poor;

    private void BuildSimpleRows()
    {
        SimpleRows.Clear();
        SimpleRows.Add(new SimpleNetworkRow("5G", MiniBars(BarCount), $"{Format.Dbm(_rsrp5)} · SINR {Format.Db(_sinr5)}"));
        SimpleRows.Add(new SimpleNetworkRow("4G", MiniBars(Math.Max(1, BarCount - 1)), $"{Format.Dbm(_rsrp4)} · SINR {Format.Db(_sinr4)}"));
    }

    private IReadOnlyList<BarItem> MiniBars(int filled)
    {
        var color = ColorUtil.ToHex(RatingColorFromVerdict());
        var result = new List<BarItem>();
        for (var i = 0; i < 5; i++)
            result.Add(new BarItem(8 + i * 4, i < filled ? color : "#3A3A40"));
        return result;
    }

    private void BuildMetrics()
    {
        MetricGroups.Clear();
        MetricGroups.Add(new MetricGroup("Signal strength", "RSRP", BuildMetricRows("rsrp", _rsrp5, _rsrp4, "dBm")));
        MetricGroups.Add(new MetricGroup("Signal quality", "SINR", BuildMetricRows("sinr", _sinr5, _sinr4, "dB")));
        MetricGroups.Add(new MetricGroup("Signal quality", "RSRQ", BuildMetricRows("rsrq", _rsrq5, _rsrq4, "dB")));
    }

    private static IReadOnlyList<MetricRow> BuildMetricRows(string kind, double fiveG, double fourG, string unit)
    {
        return
        [
            new MetricRow("5G", Value(kind, fiveG, unit), Rating(kind, fiveG),
                ColorUtil.ToHex(RatingColor(kind, fiveG)), Position(kind, fiveG)),
            new MetricRow("4G", Value(kind, fourG, unit), Rating(kind, fourG),
                ColorUtil.ToHex(RatingColor(kind, fourG)), Position(kind, fourG))
        ];
    }

    private static string Value(string kind, double value, string unit) => kind switch
    {
        "rsrp" => Format.Dbm(value),
        "sinr" => Format.Db(value),
        _ => Format.Db(value)
    };

    private void BuildLanes()
    {
        Lanes.Clear();
        Lanes.Add(new LaneData("RSRP · 5G", Format.Dbm(_rsrp5), ColorUtil.ToHex(ColorUtil.TextPrimary),
            _rsrpHistory.ToList(), 100));
        Lanes.Add(new LaneData("SINR · 5G", Format.Db(_sinr5), ColorUtil.ToHex(ColorUtil.Accent),
            _sinrHistory.ToList(), 100));
    }

    private void BuildNeighbors()
    {
        Neighbors.Clear();

    }

    private void BuildDevices(RouterState router)
    {
        Devices.Clear();
        Devices.Add(new DeviceRow("Device list unavailable", "Not supplied by this service protocol", ColorUtil.ToHex(ColorUtil.TextTertiary), false));
    }

    private void BuildPlaceholderRows()
    {
        if (SimpleRows.Count == 0) BuildSimpleRows();
        if (MetricGroups.Count == 0) BuildMetrics();
        if (Lanes.Count == 0) BuildLanes();
        if (Neighbors.Count == 0) BuildNeighbors();
        if (Devices.Count == 0) BuildDevices(State?.Router ?? new RouterState(false, null, null, null, null, null, null, null, null, null, null, null, null, null, null, null));
        if (Bars.Count == 0) BuildBars();
    }

    private void RefreshPing()
    {
        Pings.Clear();
        foreach (var value in _pingHistory)
        {
            var color = value < 35 ? ColorUtil.Good : value < 80 ? ColorUtil.Fair : ColorUtil.Poor;
            var height = Math.Clamp(8 + value / 90.0 * 36, 8, 44);
            Pings.Add(new PingBar(Math.Round(height), ColorUtil.ToHex(color)));
        }

        PingNowText = Format.MsValue(_ping);
        PingColor = ColorUtil.ToHex(_ping < 35 ? ColorUtil.Good : _ping < 80 ? ColorUtil.Fair : ColorUtil.Poor);
        var spikes = _pingHistory.Count(p => p > 40);
        SpikeCountText = spikes + (spikes == 1 ? " spike" : " spikes");

        Spikes.Clear();
        Spikes.Add(new SpikeItem(spikes == 0 ? "No observed spikes" : Format.Ms(_pingHistory.Where(p => p > 40).Max()),
            spikes == 0 ? "Recent samples" : "Elevated latency",
            "Ping samples cannot establish a cause. Check signal changes and traffic around the same time.", "#26F5A524", "#F5A524"));

        SpeedStats.Clear();
        SpeedStats.Add(new SpeedStat("Ping", "ms", Format.MsValue(_ping)));
        SpeedStats.Add(new SpeedStat("Download", "Mbit/s", Format.Dash));
        SpeedStats.Add(new SpeedStat("Upload", "Mbit/s", Format.Dash));
        SpeedStats.Add(new SpeedStat("Jitter", "ms", Format.Dash));
    }

    private void RefreshTrend()
    {
        if (_rsrpHistory.Count < 6)
        {
            TrendText = "Waiting for enough samples";
            TrendColor = ColorUtil.ToHex(ColorUtil.TextTertiary);
            return;
        }

        var delta = _rsrpHistory[^1] - _rsrpHistory[^6];
        if (delta > 2)
        {
            TrendText = "Signal is improving";
            TrendColor = ColorUtil.ToHex(ColorUtil.Good);
        }
        else if (delta < -2)
        {
            TrendText = "Signal is weakening";
            TrendColor = ColorUtil.ToHex(ColorUtil.Poor);
        }
        else
        {
            TrendText = "Signal is steady";
            TrendColor = ColorUtil.ToHex(ColorUtil.TextSecondary);
        }
    }

    private void CopyDiagnostics()
    {
        var text = $"Prism modem diagnostics{Environment.NewLine}" +
                   $"Network: {Network}{Environment.NewLine}Band: {Band}{Environment.NewLine}" +
                   $"Verdict: {Verdict} ({ScoreText}/100){Environment.NewLine}" +
                   $"RSRP 5G: {Format.Dbm(_rsrp5)} · SINR {Format.Db(_sinr5)} · RSRQ {Format.Db(_rsrq5)}{Environment.NewLine}" +
                   $"RSRP 4G: {Format.Dbm(_rsrp4)} · SINR {Format.Db(_sinr4)} · RSRQ {Format.Db(_rsrq4)}{Environment.NewLine}" +
                   $"Ping: {Format.Ms(_ping)} · loss {LossText}";
        try
        {
            if (System.Windows.Application.Current is not null) System.Windows.Clipboard.SetText(text);
        }
        catch
        {
            CopyLabel = "Clipboard unavailable";
            return;
        }
        CopyLabel = System.Windows.Application.Current is null ? "Clipboard unavailable" : "Copied";
    }

    private async Task RunSpeedTestAsync()
    {
        if (SpeedRunning) return;
        SpeedRunning = true;
        SpeedProgress = 0;
        SpeedStatus = "Starting the test through the service…";
        try
        {
              await RunAsync(async () =>
              {
                  var response = await Client.StartSpeedTestAsync();
                  if (response.Ok && response.Data is { } data)
                  {
                      static double Read(System.Text.Json.JsonElement data, string name) => data.TryGetProperty(name, out var value) && value.ValueKind == System.Text.Json.JsonValueKind.Number && value.TryGetDouble(out var reading) && double.IsFinite(reading) ? reading : double.NaN;
                      download = Read(data, "down"); upload = Read(data, "up");
                      OnPropertyChanged(nameof(Download)); OnPropertyChanged(nameof(UploadText));
                  }
                  return response;
              });
              SpeedStatus = HasError ? LastError! : double.IsFinite(download) ? "Test complete" : "No throughput result reported";
        }
        finally
        {
            SpeedRunning = false;
        }
    }

    private static void Push(List<double> history, double value)
    {
        history.Add(double.IsFinite(value) ? value : 0);
        while (history.Count > HistoryLength) history.RemoveAt(0);
    }

    private static double Normalize(double value, double min, double max)
    {
        if (!double.IsFinite(value)) return 0;
        return Math.Clamp((value - min) / (max - min) * 100, 0, 100);
    }

    private static double Position(string kind, double value) => !double.IsFinite(value) ? double.NaN : kind switch
    {
        "rsrp" => Normalize(value, -140, -44),
        "sinr" => Normalize(value, -10, 30),
        _ => Normalize(value, -20, -3)
    };

    private static string Rating(string kind, double value)
    {
        if (!double.IsFinite(value)) return Format.Dash;
        return kind switch
        {
            "rsrp" => value < -110 ? "Poor" : value < -90 ? "Fair" : "Good",
            "sinr" => value < 0 ? "Poor" : value < 13 ? "Fair" : "Good",
            _ => value < -18 ? "Poor" : value < -15 ? "Fair" : "Good"
        };
    }

    private static System.Windows.Media.Color RatingColor(string kind, double value)
    {
        var rating = Rating(kind, value);
        return rating == "Good" ? ColorUtil.Good : rating == "Fair" ? ColorUtil.Fair : rating == "Poor" ? ColorUtil.Poor : ColorUtil.TextTertiary;
    }
}
