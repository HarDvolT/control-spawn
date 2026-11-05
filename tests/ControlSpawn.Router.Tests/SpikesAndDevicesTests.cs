namespace ControlSpawn.Router.Tests;

public class DetectorTests
{
    private static (SpikeDetector Detector, List<SpikeEvent> Events) RunDetector(double?[] values, double t0 = 0)
    {
        var detector = new SpikeDetector();
        var events = new List<SpikeEvent>();
        for (var i = 0; i < values.Length; i++)
        {
            var found = detector.Update(t0 + i, values[i]);
            if (found is not null)
            {
                events.Add(found);
            }
        }

        return (detector, events);
    }

    [Fact]
    public void NeedsABaselineFirst()
    {
        var (_, events) = RunDetector(new double?[] { 30, 300, 30, 30 });
        Assert.Empty(events);
    }

    [Fact]
    public void OneSpikeMerged()
    {
        var values = new List<double?> { 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30 };
        values.AddRange(new double?[] { 200, 260, null, 220 });
        values.AddRange(new double?[] { 31, 30 });
        values.AddRange(Enumerable.Repeat<double?>(30, 5));

        var (_, events) = RunDetector(values.ToArray());
        Assert.Single(events);
        var spike = events[0];
        Assert.Equal(15, spike.Start);
        Assert.Equal(18, spike.End);
        Assert.Equal(260, spike.Peak);
        Assert.Equal(1, spike.Lost);
        Assert.Equal(4, spike.Count);
        Assert.Equal(30, spike.Baseline);
    }

    [Fact]
    public void ThresholdRules()
    {
        var (thirty, _) = RunDetector(Enumerable.Repeat<double?>(30, 15).ToArray());
        Assert.Equal(75, thirty.Threshold());          // 2.5 x 30

        var (hundred, _) = RunDetector(Enumerable.Repeat<double?>(100, 15).ToArray());
        Assert.Equal(250, hundred.Threshold());        // 2.5 x 100

        var (ten, _) = RunDetector(Enumerable.Repeat<double?>(10, 15).ToArray());
        Assert.Equal(50, ten.Threshold());             // 10 + 40 ms
    }

    [Fact]
    public void TwoSpikesSeparate()
    {
        var values = new List<double?>(Enumerable.Repeat<double?>(30, 12));
        values.Add(200);
        values.AddRange(new double?[] { 30, 30, 30 });
        values.AddRange(new double?[] { 180, 190 });
        values.AddRange(new double?[] { 30, 30 });

        var (_, events) = RunDetector(values.ToArray());
        Assert.Equal(2, events.Count);
    }

    [Fact]
    public void SpikeSamplesDoNotRaiseBaseline()
    {
        var values = new List<double?>(Enumerable.Repeat<double?>(30, 12));
        values.AddRange(new double?[] { 300, 300, 300 });

        var (detector, _) = RunDetector(values.ToArray());
        Assert.Equal(30, detector.Baseline());
    }
}

public class ClassifyTests
{
    private static readonly SpikeEvent Event = new()
    {
        Start = 100,
        End = 102,
        Peak = 240,
        Lost = 0,
        Count = 3,
        Baseline = 30,
    };

    private static Evidence MakeEvidence(
        (double Down, double Up)? pc = null,
        Dictionary<int, ProcActivity>? procs = null,
        List<SignalPoint>? signal = null,
        List<(double Time, double Down, double Up)>? router = null,
        double t = 100)
    {
        var evidence = new Evidence();
        var values = pc ?? (0.2, 0.1);
        for (var i = -10; i < 5; i++)
        {
            evidence.AddPc(
                t + i,
                i >= -5 ? values.Down : 0.1,
                i >= -5 ? values.Up : 0.05);
        }

        for (var i = -6; i < 3; i += 2)
        {
            evidence.AddProcs(t + i, procs ?? new Dictionary<int, ProcActivity>());
        }

        foreach (var point in signal ?? new List<SignalPoint>())
        {
            evidence.AddSignal(point);
        }

        foreach (var row in router ?? new List<(double Time, double Down, double Up)>())
        {
            evidence.AddRouter(row.Time, row.Down, row.Up);
        }

        return evidence;
    }

    private static ProcActivity Process(string exe, long ops, int conns = 0, int isNew = 0, params string[] services) =>
        new()
        {
            Exe = exe,
            Services = services,
            Ops = ops,
            Conns = conns,
            New = isNew,
        };

    [Fact]
    public void AppOnThisPC()
    {
        var procs = new Dictionary<int, ProcActivity>
        {
            [1] = Process("steam.exe", 900, 6, 2),
            [2] = Process("steamwebhelper.exe", 100, 2),
            [3] = Process("chrome.exe", 150, 9),
        };

        var result = Spikes.Classify(Event, MakeEvidence(pc: (30.0, 2.0), procs: procs));
        var top = result.Causes[0];
        Assert.Equal("app", top.Kind);
        Assert.Equal("Steam", top.Name); // helpers merged into one
        Assert.Equal("likely", top.Confidence);
        Assert.Equal(1000.0 / 1150.0, top.Share!.Value, 12);
        Assert.Equal(32 * 1000.0 / 1150.0, top.Mbps!.Value, 12);
        Assert.Equal("Chrome", result.Causes[1].Name);
        Assert.Contains("Steam", top.Tip);
    }

    [Fact]
    public void WindowsUpdateInsideSvchost()
    {
        var procs = new Dictionary<int, ProcActivity>
        {
            [7] = Process("svchost.exe", 500, 4, 1, "DoSvc"),
            [8] = Process("svchost.exe", 400, 2, 0, "wuauserv", "BITS"),
        };

        var result = Spikes.Classify(Event, MakeEvidence(pc: (20.0, 8.0), procs: procs));
        var names = result.Causes.Select(cause => cause.Name).ToList();
        Assert.Equal(
            new[] { "Delivery Optimization (update sharing)", "Windows Update" },
            names.Take(2));
        Assert.Equal("svchost.exe (wuauserv)", result.Causes[1].Detail);
    }

    [Fact]
    public void UnknownProgramIsNamed()
    {
        var (name, detail, _) = Spikes.Friendly("someupdater.exe");
        Assert.Equal("someupdater", name);
        Assert.Equal("someupdater.exe", detail);
    }

    [Fact]
    public void QuietPcFallsBackToIsp()
    {
        var procs = new Dictionary<int, ProcActivity> { [1] = Process("chrome.exe", 50, 3) };
        var result = Spikes.Classify(Event, MakeEvidence(pc: (0.3, 0.1), procs: procs));
        Assert.Equal(new[] { "isp" }, result.Causes.Select(cause => cause.Kind));
    }

    [Fact]
    public void OtherDeviceFromRouterCounters()
    {
        var router = new List<(double Time, double Down, double Up)>
        {
            (99, 25.0, 3.0),
            (101, 30.0, 2.0),
        };
        var result = Spikes.Classify(Event, MakeEvidence(pc: (0.5, 0.2), router: router));
        Assert.Equal("other_device", result.Causes[0].Kind);
        Assert.Equal(33.0 - 0.7, result.Causes[0].Mbps!.Value, 12);
    }

    [Fact]
    public void SignalDrop()
    {
        var signal = new List<SignalPoint>();
        for (var i = 0; i < 15; i++)
        {
            signal.Add(new SignalPoint(80 + i, 9.0, -92.0, "5G", "715"));
        }

        signal.Add(new SignalPoint(97, 2.0, -93.0, "5G", "715"));
        signal.Add(new SignalPoint(101, 1.0, -94.0, "5G", "715"));

        var result = Spikes.Classify(Event, MakeEvidence(signal: signal));
        Assert.Equal("signal", result.Causes[0].Kind);
        Assert.Contains("9 → 1 dB", result.Causes[0].Detail);
    }

    [Fact]
    public void FiveGToFourG()
    {
        var signal = new List<SignalPoint>();
        for (var i = 0; i < 15; i++)
        {
            signal.Add(new SignalPoint(80 + i, 9.0, -92.0, "5G", "715"));
        }

        signal.Add(new SignalPoint(101, 8.0, -91.0, "4G", "272"));

        var result = Spikes.Classify(Event, MakeEvidence(signal: signal));
        Assert.Contains("5G → 4G", result.Causes[0].Detail);
    }

    [Fact]
    public void Offenders()
    {
        var spikes = new[]
        {
            new ClassifyResult { Causes = new[] { new SpikeCause { Name = "Steam" } } },
            new ClassifyResult { Causes = new[] { new SpikeCause { Name = "Steam" } } },
            new ClassifyResult { Causes = new[] { new SpikeCause { Name = "OneDrive" } } },
        };

        Assert.Equal(new[] { ("Steam", 2), ("OneDrive", 1) }, Spikes.Offenders(spikes));
    }
}

public class EvidenceTests
{
    [Fact]
    public void RouterCountersToRates()
    {
        var evidence = new Evidence();
        Spikes.FeedSignal(evidence, 0, D.Map(
            ("SINR_5G", "8"),
            ("RSRP_5G", "-96"),
            ("PCI_5G", "715"),
            ("wan_rx_bytes", "1000000"),
            ("wan_tx_bytes", "0")));
        Spikes.FeedSignal(evidence, 2, D.Map(
            ("SINR_5G", "8"),
            ("RSRP_5G", "-96"),
            ("PCI_5G", "715"),
            ("wan_rx_bytes", "3500000"),
            ("wan_tx_bytes", "250000")));

        Assert.Single(evidence.Router);
        var (_, down, up) = evidence.Router[0];
        Assert.Equal(10.0, down, 12);
        Assert.Equal(1.0, up, 12);
        Assert.Equal("5G", evidence.Signal[^1].Network);
    }

    [Fact]
    public void CounterResetIsIgnored()
    {
        var evidence = new Evidence();
        evidence.AddRouterCounters(0, 5000, 5000);
        evidence.AddRouterCounters(1, 100, 100);
        Assert.Empty(evidence.Router);
    }

    [Fact]
    public void IsInternet()
    {
        Assert.True(Spikes.IsInternet("1.1.1.1"));
        Assert.False(Spikes.IsInternet("192.168.0.1"));
        Assert.False(Spikes.IsInternet("127.0.0.1"));
        Assert.False(Spikes.IsInternet("fe80::1"));
    }
}

public class DeviceTests
{
    private const string Arp = """
        Interface: 192.168.0.59 --- 0x9
          Internet Address      Physical Address      Type
          192.168.0.1           fc-3f-fc-e1-d6-06     dynamic
          192.168.0.250         f6-eb-6f-11-22-33     dynamic
          192.168.0.40          a4-83-e7-00-00-01     dynamic
          192.168.0.255         ff-ff-ff-ff-ff-ff     static
          224.0.0.22            01-00-5e-00-00-16     static

        Interface: 172.20.0.1 --- 0x12
          172.20.0.5            00-15-5d-00-00-01     dynamic
        """;

    [Fact]
    public void ParseArp()
    {
        var found = Devices.ParseArp(Arp, Devices.LocalSubnet());
        Assert.Equal(
            new[] { "192.168.0.1", "192.168.0.250", "192.168.0.40" },
            found.Select(device => device.Ip));
        Assert.Equal("FC:3F:FC:E1:D6:06", found[0].Mac);
    }

    [Fact]
    public void VendorAndPrivateMac()
    {
        Assert.Equal("Apple", Devices.Vendor("A4:83:E7:00:00:01"));
        Assert.True(Devices.IsPrivateMac("F6:EB:6F:11:22:33"));
        Assert.Contains("Private address", Devices.Vendor("F6:EB:6F:11:22:33"));
        Assert.Equal("", Devices.Vendor("00:00:01:00:00:00"));
    }

    [Fact]
    public void TrackerLabelsAndNew()
    {
        var store = new Dictionary<string, DeviceInfo>();
        var tracker = new DeviceTracker(store, ownIp: "192.168.0.59") { Started = 0 };
        tracker.Refresh(now: 100, arpText: Arp);
        Assert.Equal(3, store.Count);

        var (routerMac, router) = store.First(pair => pair.Value.Ip == "192.168.0.1");
        Assert.Equal("Router", tracker.Label(routerMac, router));

        var apple = store.Values.First(device => device.Ip == "192.168.0.40");
        Assert.True(tracker.IsNew(apple, now: 200));
        Assert.False(tracker.IsNew(apple, now: 100 + Devices.NewSeconds + 1));

        tracker.Refresh(now: 500, arpText: Arp);
        Assert.Equal(100, apple.First);
        Assert.Equal(500, apple.Last);
    }

    [Fact]
    public void MergeRouterList()
    {
        var store = new Dictionary<string, DeviceInfo>();
        var tracker = new DeviceTracker(store, ownIp: "192.168.0.59");
        var resp = D.Map(
            ("dhcp_list_info", new List<object?>
            {
                D.Map(("hostname", "Galaxy-S21"), ("ip", "192.168.0.250"), ("mac", "f6:eb:6f:11:22:33"), ("interface", "wlan")),
                D.Map(("hostname", "*"), ("ip", "192.168.0.59"), ("mac", "a4-4c-c8-11-22-33"), ("interface", "eth")),
                D.Map(("hostname", "bad")),
            }),
            ("wlan5g_wifi_info", new List<object?>
            {
                D.Map(("ip", "192.168.0.250"), ("rssi", "-54")),
            }));

        Assert.Equal(2, Devices.MergeRouterList(tracker, resp, now: 50));

        var phone = store["F6:EB:6F:11:22:33"];
        Assert.Equal("Galaxy-S21", phone.Name);
        Assert.Equal("Wi-Fi 5 GHz (-54 dBm)", phone.Link);

        var pc = store["A4:4C:C8:11:22:33"];
        Assert.Equal("", pc.Name);
        Assert.Equal("Cable", pc.Link);
        Assert.Equal("This PC", tracker.Label("A4:4C:C8:11:22:33", pc));
    }

    [Fact]
    public void RouterTrafficHookIsOff()
    {
        Assert.False(Devices.RouterDeviceTrafficEnabled);
        Assert.Null(Devices.RouterDeviceTraffic(D.Map(("anything", 1))));
    }
}
