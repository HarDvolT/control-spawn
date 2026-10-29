using System.Collections.ObjectModel;
using Prism.App.Common;
using Prism.App.Services;
using Prism.Core.Ipc;

namespace Prism.App.ViewModels;

public sealed class MonitorViewModel : PageViewModel
{
    private const int CpuHistoryLength = 60;
    private const int NetHistoryLength = 40;

    private readonly MetricHistory _cpuSamples = new();
    private readonly MetricHistory _downSamples = new();
    private readonly MetricHistory _upSamples = new();
    private readonly List<double> _diskHistory = new();
    private string _selectedRange = "5 min";
    private double _cpuLoad;
    private double _gpuLoad;
    private string _cpuPercentText = Format.Dash;
    private string _gpuPercentText = Format.Dash;
    private string _ramText = Format.Dash;
    private double _ramPercent;
    private string _vramText = Format.Dash;
    private double _vramPercent;
    private string _dimm1Text = Format.Dash;
    private string _dimm2Text = Format.Dash;
    private string _dimm1Color = ColorUtil.ToHex(ColorUtil.TextTertiary);
    private string _dimm2Color = ColorUtil.ToHex(ColorUtil.TextTertiary);
    private string _diskReadText = Format.Dash;
    private string _diskWriteText = Format.Dash;
    private string _diskTempText = Format.Dash;
    private string _netDownText = Format.Dash;
    private string _netUpText = Format.Dash;
    private string _peakText = Format.Dash;

    public MonitorViewModel(IPrismClient client) : base(client)
    {
        PageTitle = "Monitor";
        Refresh();
    }

    public ObservableCollection<double> CpuHistory { get; } = new();
    public ObservableCollection<double> NetDownHistory { get; } = new();
    public ObservableCollection<double> NetUpHistory { get; } = new();
    public ObservableCollection<StatRow> CpuStats { get; } = new();
    public ObservableCollection<ThreadItem> Threads { get; } = new();
    public ObservableCollection<StatRow> GpuStats { get; } = new();
    public ObservableCollection<TempRow> Temps { get; } = new();
    public ObservableCollection<FanRow> Fans { get; } = new();
    public ObservableCollection<BarItem> DiskBars { get; } = new();

    public IReadOnlyList<string> Ranges { get; } = ["1 min", "5 min", "1 hour"];

    public string SelectedRange
    {
        get => _selectedRange;
        set { if (Ranges.Contains(value) && Set(ref _selectedRange,value)) RefreshCharts(); }
    }

    public double CpuLoad { get => _cpuLoad; private set => Set(ref _cpuLoad, value); }
    public double GpuLoad { get => _gpuLoad; private set => Set(ref _gpuLoad, value); }
    public string CpuPercentText { get => _cpuPercentText; private set => Set(ref _cpuPercentText, value); }
    public string GpuPercentText { get => _gpuPercentText; private set => Set(ref _gpuPercentText, value); }
    public string RamText { get => _ramText; private set => Set(ref _ramText, value); }
    public double RamPercent { get => _ramPercent; private set => Set(ref _ramPercent, value); }
    public string VramText { get => _vramText; private set => Set(ref _vramText, value); }
    public double VramPercent { get => _vramPercent; private set => Set(ref _vramPercent, value); }
    public string Dimm1Text { get => _dimm1Text; private set => Set(ref _dimm1Text, value); }
    public string Dimm2Text { get => _dimm2Text; private set => Set(ref _dimm2Text, value); }
    public string Dimm1Color { get => _dimm1Color; private set => Set(ref _dimm1Color, value); }
    public string Dimm2Color { get => _dimm2Color; private set => Set(ref _dimm2Color, value); }
    public string DiskReadText { get => _diskReadText; private set => Set(ref _diskReadText, value); }
    public string DiskWriteText { get => _diskWriteText; private set => Set(ref _diskWriteText, value); }
    public string DiskTempText { get => _diskTempText; private set => Set(ref _diskTempText, value); }
    public string NetDownText { get => _netDownText; private set => Set(ref _netDownText, value); }
    public string NetUpText { get => _netUpText; private set => Set(ref _netUpText, value); }
    public string PeakText { get => _peakText; private set => Set(ref _peakText, value); }

    protected override void Refresh()
    {
        var state = State;
        if (state is null)
        {
            if (CpuStats.Count == 0)
            {
                CpuStats.Add(new StatRow("Load", Format.Dash, ColorUtil.ToHex(ColorUtil.TextPrimary)));
                CpuStats.Add(new StatRow("Clock", Format.Dash, ColorUtil.ToHex(ColorUtil.TextPrimary)));
                CpuStats.Add(new StatRow("Temp", Format.Dash, ColorUtil.ToHex(ColorUtil.TextTertiary)));
                CpuStats.Add(new StatRow("Power", Format.Dash, ColorUtil.ToHex(ColorUtil.TextPrimary)));
            }
            NotifyAll();
            return;
        }

        var system = state.System;
        CpuLoad = system.CpuLoad ?? double.NaN;
        GpuLoad = system.GpuLoad ?? 0;
        CpuPercentText = Format.Percent(system.CpuLoad);
        GpuPercentText = Format.Percent(system.GpuLoad);
        _cpuSamples.Add(state.Timestamp,system.CpuLoad);
        _downSamples.Add(state.Timestamp,system.NetDownMbps);
        _upSamples.Add(state.Timestamp,system.NetUpMbps);
        RefreshCharts();

        CpuStats.Clear();
        CpuStats.Add(new StatRow("Load", Format.Percent(system.CpuLoad), ColorUtil.ToHex(ColorUtil.TextPrimary)));
        CpuStats.Add(new StatRow("Clock", Format.Ghz(system.CpuClockMhz), ColorUtil.ToHex(ColorUtil.TextPrimary)));
        CpuStats.Add(new StatRow("Temp", Format.Temp(system.CpuTemp), ColorUtil.ToHex(ColorUtil.Heat(system.CpuTemp))));
        CpuStats.Add(new StatRow("Power", Format.Watts(system.CpuPowerW), ColorUtil.ToHex(ColorUtil.TextPrimary)));

        Threads.Clear();
        var cores = system.CpuCoreLoads ?? Array.Empty<double?>();
        for (var i = 0; i < cores.Count; i++)
        {
            var load = Math.Clamp(cores[i] ?? 0, 0, 100);
            Threads.Add(new ThreadItem(load,
                ColorUtil.ToHex(load > 75 ? ColorUtil.Accent : load > 45 ? ColorUtil.Mix(ColorUtil.Accent, ColorUtil.TextPrimary, 0.5) : ColorUtil.TextTertiary)));
        }

        GpuStats.Clear();
        GpuStats.Add(new StatRow("Load", Format.Percent(system.GpuLoad), ColorUtil.ToHex(ColorUtil.TextPrimary)));
        GpuStats.Add(new StatRow("Temp", Format.Temp(system.GpuTemp), ColorUtil.ToHex(ColorUtil.Heat(system.GpuTemp))));
        GpuStats.Add(new StatRow("Hotspot", Format.Temp(system.GpuHotspot), ColorUtil.ToHex(ColorUtil.Heat(system.GpuHotspot))));
        GpuStats.Add(new StatRow("Clock", Format.Mhz(system.GpuClockMhz), ColorUtil.ToHex(ColorUtil.TextPrimary)));
        GpuStats.Add(new StatRow("Power", Format.Watts(system.GpuPowerW), ColorUtil.ToHex(ColorUtil.TextPrimary)));
        GpuStats.Add(new StatRow("Fan", Format.Rpm(system.GpuFanRpm), ColorUtil.ToHex(ColorUtil.TextSecondary)));

        RamText = Format.GbPair(system.RamUsedGb, system.RamTotalGb);
        RamPercent = system is { RamUsedGb: { } used, RamTotalGb: { } total } && total > 0 ? used / total * 100 : 0;
        VramText = Format.GbPair(system.VramUsedGb, system.VramTotalGb);
        VramPercent = system is { VramUsedGb: { } vused, VramTotalGb: { } vtotal } && vtotal > 0 ? vused / vtotal * 100 : 0;

        Dimm1Text = Format.Temp(system.DimmTemps.Count > 0 ? system.DimmTemps[0] : null);
        Dimm2Text = Format.Temp(system.DimmTemps.Count > 1 ? system.DimmTemps[1] : null);
        Dimm1Color = ColorUtil.ToHex(ColorUtil.Heat(system.DimmTemps.Count > 0 ? system.DimmTemps[0] : null));
        Dimm2Color = ColorUtil.ToHex(ColorUtil.Heat(system.DimmTemps.Count > 1 ? system.DimmTemps[1] : null));

        Temps.Clear();
        Temps.Add(new TempRow("CPU", Format.Temp(system.CpuTemp), Math.Clamp(system.CpuTemp ?? 0, 0, 100), ColorUtil.ToHex(ColorUtil.Heat(system.CpuTemp))));
        Temps.Add(new TempRow("GPU", Format.Temp(system.GpuTemp), Math.Clamp(system.GpuTemp ?? 0, 0, 100), ColorUtil.ToHex(ColorUtil.Heat(system.GpuTemp))));
        Temps.Add(new TempRow("GPU hotspot", Format.Temp(system.GpuHotspot), Math.Clamp(system.GpuHotspot ?? 0, 0, 100), ColorUtil.ToHex(ColorUtil.Heat(system.GpuHotspot))));
        Temps.Add(new TempRow("DIMM 1", Dimm1Text, Math.Clamp(system.DimmTemps.Count > 0 ? system.DimmTemps[0] ?? 0 : 0, 0, 100), Dimm1Color));
        Temps.Add(new TempRow("DIMM 2", Dimm2Text, Math.Clamp(system.DimmTemps.Count > 1 ? system.DimmTemps[1] ?? 0 : 0, 0, 100), Dimm2Color));
        Temps.Add(new TempRow("Board", Format.Temp(system.BoardTemp), Math.Clamp(system.BoardTemp ?? 0, 0, 100), ColorUtil.ToHex(ColorUtil.Heat(system.BoardTemp))));
        Temps.Add(new TempRow("NVMe", Format.Temp(system.Disks.Count > 0 ? system.Disks[0].TempC : null),
            Math.Clamp(system.Disks.Count > 0 ? system.Disks[0].TempC ?? 0 : 0, 0, 100),
            ColorUtil.ToHex(ColorUtil.Heat(system.Disks.Count > 0 ? system.Disks[0].TempC : null))));

        Fans.Clear();
        foreach (var fan in state.Fans.Where(f => (f.Controllable || f.IsPump) && (f.Rpm is > 0 || f.IsPump)))
            Fans.Add(new FanRow(fan.Name, fan.IsPump ? "Fixed at 100%" : "Curve · " + fan.Curve,
                Format.Rpm(fan.Rpm), fan.IsPump ? 100 : Math.Clamp(fan.Percent ?? 0, 0, 100), fan.IsPump));

        var disk = system.Disks.Count > 0 ? system.Disks[0] : null;
        DiskReadText = disk is null ? Format.Dash : Format.Number(disk.ReadMBps) + " MB/s";
        DiskWriteText = disk is null ? Format.Dash : Format.Number(disk.WriteMBps) + " MB/s";
        DiskTempText = disk is null ? Format.Dash : Format.Temp(disk.TempC);
        Push(_diskHistory, disk?.ReadMBps ?? double.NaN, 14);
        DiskBars.Clear();
        foreach (var value in _diskHistory)
            DiskBars.Add(new BarItem(Math.Clamp(8 + value / 400 * 82, 8, 90), ColorUtil.ToHex(ColorUtil.Accent)));

        NetDownText = Format.Mbps(system.NetDownMbps);
        NetUpText = Format.Mbps(system.NetUpMbps);
        PeakText = CpuHistory.Count == 0 ? Format.Dash : "peak " + Format.Percent(CpuHistory.Max());

        NotifyAll();
    }

    private void RefreshCharts()
    {
        if (State is null) return;
        var range = SelectedRange switch { "1 min" => TimeSpan.FromMinutes(1), "1 hour" => TimeSpan.FromHours(1), _ => TimeSpan.FromMinutes(5) };
        Replace(CpuHistory,_cpuSamples.Read(State.Timestamp,range));
        Replace(NetDownHistory,_downSamples.Read(State.Timestamp,range));
        Replace(NetUpHistory,_upSamples.Read(State.Timestamp,range));
        PeakText = CpuHistory.Count == 0 ? Format.Dash : "peak " + Format.Percent(CpuHistory.Max());
    }
    private static void Replace(ObservableCollection<double> target,IReadOnlyList<double> values)
    { target.Clear(); foreach (var value in values) target.Add(value); }

    private static void Push(ObservableCollection<double> history, double value, int limit)
    {
        if (!double.IsFinite(value)) return;
        history.Add(Math.Round(value, 1));
        while (history.Count > limit) history.RemoveAt(0);
    }

    private static void Push(List<double> history, double value, int limit)
    {
        if (!double.IsFinite(value)) return;
        history.Add(Math.Round(value, 1));
        while (history.Count > limit) history.RemoveAt(0);
    }
}
