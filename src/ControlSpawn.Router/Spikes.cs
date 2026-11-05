using System.Globalization;
using System.Net;
using System.Net.Sockets;

namespace ControlSpawn.Router;

/// <summary>
/// Spike detective: finds ping spikes and explains what was using the network at that moment.
///
/// Evidence, all read-only and without admin rights:
///   - this PC's total traffic (network adapter byte counters, exact)
///   - which programs and Windows services own internet connections, and how busy each one's
///     socket I/O was (Windows does not report per-app bytes without admin, so per-app numbers
///     are shares of this PC's exact total: an estimate)
///   - the router's own traffic counters and signal, taken from the signal reply the widget
///     already reads (no extra router commands)
///
/// Detection and classification are pure functions so they can be tested with fake data.
/// </summary>
public static class Spikes
{
    public const double BaselineSeconds = 60;
    public const int MinBaseline = 10;      // replies needed before spikes are judged
    public const double SpikeFactor = 2.5;
    public const double SpikeExtraMs = 40;
    public const int EndAfter = 2;          // normal replies in a row that end a spike
    public const double LookbackSeconds = 5; // evidence window starts this long before the spike
    public const double KeepSeconds = 180;
    public const double PcBusyMbps = 2.0;
    public const double OtherDeviceMbps = 2.0;

    /// <summary>exe name -> friendly name, tip.</summary>
    public static readonly IReadOnlyDictionary<string, (string Name, string Tip)> Apps =
        new Dictionary<string, (string, string)>(StringComparer.Ordinal)
        {
            ["steam.exe"] = ("Steam", "Pause Steam downloads while gaming, or limit them in Steam > Downloads."),
            ["steamwebhelper.exe"] = ("Steam", "Pause Steam downloads while gaming, or limit them in Steam > Downloads."),
            ["steamservice.exe"] = ("Steam", "Pause Steam downloads while gaming, or limit them in Steam > Downloads."),
            ["epicgameslauncher.exe"] = ("Epic Games", "Pause Epic downloads or set a download limit in its settings."),
            ["battle.net.exe"] = ("Battle.net", "Pause Battle.net downloads or limit their speed in its settings."),
            ["agent.exe"] = ("Battle.net updater", "Pause Battle.net downloads or limit their speed in its settings."),
            ["eadesktop.exe"] = ("EA app", "Pause EA app downloads while gaming."),
            ["ubisoftconnect.exe"] = ("Ubisoft Connect", "Pause Ubisoft Connect downloads while gaming."),
            ["gamingservices.exe"] = ("Xbox / Microsoft Store games", "Pause downloads in the Xbox app or Microsoft Store."),
            ["xboxpcapp.exe"] = ("Xbox app", "Pause downloads in the Xbox app."),
            ["winstore.app.exe"] = ("Microsoft Store", "Pause app updates in the Microsoft Store."),
            ["onedrive.exe"] = ("OneDrive", "Pause OneDrive syncing (tray icon > Pause syncing) while gaming or on calls."),
            ["googledrivefs.exe"] = ("Google Drive", "Pause Google Drive syncing while gaming or on calls."),
            ["dropbox.exe"] = ("Dropbox", "Pause Dropbox syncing while gaming or on calls."),
            ["chrome.exe"] = ("Chrome", "Close tabs that stream video or download, or pause downloads."),
            ["msedge.exe"] = ("Edge", "Close tabs that stream video or download, or pause downloads."),
            ["firefox.exe"] = ("Firefox", "Close tabs that stream video or download, or pause downloads."),
            ["opera.exe"] = ("Opera", "Close tabs that stream video or download, or pause downloads."),
            ["brave.exe"] = ("Brave", "Close tabs that stream video or download, or pause downloads."),
            ["discord.exe"] = ("Discord", "Discord streams and updates use bandwidth; stop screen sharing if not needed."),
            ["teams.exe"] = ("Teams", "Video calls use a lot of upload; turn off video if the call lags."),
            ["ms-teams.exe"] = ("Teams", "Video calls use a lot of upload; turn off video if the call lags."),
            ["zoom.exe"] = ("Zoom", "Video calls use a lot of upload; turn off video if the call lags."),
            ["spotify.exe"] = ("Spotify", "Spotify downloads songs for offline use in the background."),
            ["qbittorrent.exe"] = ("qBittorrent", "Torrents fill the line; set upload/download limits while gaming."),
            ["utorrent.exe"] = ("uTorrent", "Torrents fill the line; set upload/download limits while gaming."),
            ["bittorrent.exe"] = ("BitTorrent", "Torrents fill the line; set upload/download limits while gaming."),
            ["msmpeng.exe"] = ("Microsoft Defender", "Defender was updating or scanning; it usually finishes quickly."),
            ["mpdefendercoreservice.exe"] = ("Microsoft Defender", "Defender was updating or scanning; it usually finishes quickly."),
            ["msedgewebview2.exe"] = ("An app's web view (WebView2)", "Some app's built-in web page was loading content."),
            ["nvcontainer.exe"] = ("NVIDIA app", "NVIDIA may be downloading a driver; let it finish or pause it."),
            ["adobe desktop service.exe"] = ("Adobe Creative Cloud", "Creative Cloud may be syncing or updating; pause it."),
        };

    /// <summary>Windows service -> friendly name, tip.</summary>
    public static readonly IReadOnlyDictionary<string, (string Name, string Tip)> Services =
        new Dictionary<string, (string, string)>(StringComparer.Ordinal)
        {
            ["wuauserv"] = ("Windows Update", "Windows Update was downloading: set active hours or pause updates while gaming."),
            ["usosvc"] = ("Windows Update", "Windows Update was downloading: set active hours or pause updates while gaming."),
            ["dosvc"] = ("Delivery Optimization (update sharing)",
                "Windows was sharing updates: Settings > Windows Update > Delivery Optimization, turn it off."),
            ["bits"] = ("Background downloads (BITS)", "Windows or an app was downloading in the background; it eases off on its own."),
            ["wsearch"] = ("Windows Search", "Search indexing rarely uses the internet; probably harmless."),
            ["cryptsvc"] = ("Certificate updates", "Windows was checking certificates; short and harmless."),
            ["dnscache"] = ("DNS lookups", "Many lookups at once usually come from a browser or game starting."),
            ["installservice"] = ("Microsoft Store installs", "Pause app updates in the Microsoft Store."),
            ["clipsvc"] = ("Microsoft Store licensing", "Pause app updates in the Microsoft Store."),
            ["windefend"] = ("Microsoft Defender", "Defender was updating or scanning; it usually finishes quickly."),
            ["diagtrack"] = ("Windows telemetry", "Windows was sending diagnostic data; it is usually brief."),
        };

    public const string SignalTip =
        "The router's signal dropped at that moment: move the router or ask Ooredoo about the cell.";

    public const string OtherTip =
        "Another phone, TV or PC on your network was busy (updates, streaming, downloads).";

    public const string IspTip =
        "Nothing on your side was busy: likely congestion on the mobile network; it often happens at peak hours.";

    /// <summary>(name, detail, tip) for a process, resolving service hosts to their services.</summary>
    public static (string Name, string Detail, string Tip) Friendly(
        string? exe,
        IReadOnlyList<string>? services = null)
    {
        var exeText = string.IsNullOrEmpty(exe) ? "?" : exe;
        var exeLower = exeText.ToLowerInvariant();
        var serviceList = services ?? Array.Empty<string>();

        foreach (var service in serviceList)
        {
            if (Services.TryGetValue(service.ToLowerInvariant(), out var info))
            {
                return (info.Name, $"{exeText} ({service})", info.Tip);
            }
        }

        if (Apps.TryGetValue(exeLower, out var app))
        {
            return (app.Name, exeText, app.Tip);
        }

        if (serviceList.Count > 0)
        {
            return ($"Windows service: {string.Join(", ", serviceList.Take(2))}",
                exeText,
                "A Windows service was using the network.");
        }

        var baseName = exeLower.EndsWith(".exe", StringComparison.Ordinal) ? exeText[..^4] : exeText;
        return (baseName, exeText, $"{baseName} was using the network; close or pause it to test.");
    }

    /// <summary>True for a public internet address (the Python ipaddress flags, approximated).</summary>
    public static bool IsInternet(string? address)
    {
        if (!IPAddress.TryParse(address, out var ip))
        {
            return false;
        }

        if (ip.AddressFamily == AddressFamily.InterNetworkV6)
        {
            if (IPAddress.IPv6Loopback.Equals(ip) || IPAddress.IPv6Any.Equals(ip)
                || ip.IsIPv6LinkLocal || ip.IsIPv6SiteLocal || ip.IsIPv6Multicast)
            {
                return false;
            }

            var v6 = ip.GetAddressBytes();
            return (v6[0] & 0xFE) != 0xFC; // unique-local fc00::/7
        }

        var bytes = ip.GetAddressBytes();
        if (bytes[0] == 0 || bytes[0] == 10 || bytes[0] == 127 || bytes[0] >= 240)
        {
            return false;
        }

        if (bytes[0] == 100 && bytes[1] is >= 64 and <= 127)
        {
            return false; // carrier-grade NAT 100.64.0.0/10
        }

        if (bytes[0] == 169 && bytes[1] == 254)
        {
            return false; // link-local
        }

        if (bytes[0] == 172 && bytes[1] is >= 16 and <= 31)
        {
            return false;
        }

        if (bytes[0] == 192 && bytes[1] == 168)
        {
            return false;
        }

        if (bytes[0] == 192 && bytes[1] == 0 && bytes[2] == 0)
        {
            return false;
        }

        if (bytes[0] == 198 && bytes[1] is 18 or 19)
        {
            return false; // benchmarking range
        }

        return bytes[0] < 224; // multicast and reserved
    }

    /// <summary>Take what the detective needs from the router's signal reply.</summary>
    public static void FeedSignal(Evidence evidence, double time, IReadOnlyDictionary<string, object?> raw)
    {
        var sinr5 = Raw.ToFloat(raw.GetValueOrDefault("SINR_5G"));
        var sinr4 = Raw.ToFloat(raw.GetValueOrDefault("SINR"));
        var rsrp5 = Raw.ToFloat(raw.GetValueOrDefault("RSRP_5G"));
        var rsrp4 = Raw.ToFloat(raw.GetValueOrDefault("RSRP"));
        var on5G = rsrp5 is not null || sinr5 is not null;

        evidence.AddSignal(new SignalPoint(
            Time: time,
            Sinr: on5G ? sinr5 : sinr4,
            Rsrp: on5G ? rsrp5 : rsrp4,
            Network: on5G ? "5G" : "4G",
            Pci: Raw.StrOf(on5G ? raw.GetValueOrDefault("PCI_5G") : raw.GetValueOrDefault("PCI"))));

        evidence.AddRouterCounters(
            time,
            Raw.ToInt(raw.GetValueOrDefault("wan_rx_bytes")),
            Raw.ToInt(raw.GetValueOrDefault("wan_tx_bytes")));
    }

    /// <summary>Ranked causes for a spike, plus the totals they were based on.</summary>
    public static ClassifyResult Classify(SpikeEvent spike, Evidence evidence)
    {
        var t0 = spike.Start - LookbackSeconds;
        var t1 = spike.End + 1;
        var pc = evidence.PcWindow(t0, t1);
        var pcDown = Peak(pc.Select(row => row.Down));
        var pcUp = Peak(pc.Select(row => row.Up));
        var pcTotal = pcDown + pcUp;
        var causes = new List<SpikeCause>();

        // 1. programs / services on this PC
        var aggregated = new Dictionary<int, AggregatedProcess>();
        foreach (var (_, snapshot) in evidence.ProcWindow(t0, t1))
        {
            foreach (var (pid, info) in snapshot)
            {
                if (!aggregated.TryGetValue(pid, out var entry))
                {
                    entry = new AggregatedProcess { Exe = info.Exe, Services = info.Services };
                    aggregated[pid] = entry;
                }

                entry.Ops += info.Ops;
                entry.New += info.New;
            }
        }

        var totalOps = aggregated.Values.Sum(entry => entry.Ops);

        // group by friendly name so Steam's helpers count as one
        var groups = new Dictionary<string, AggregatedGroup>(StringComparer.Ordinal);
        foreach (var entry in aggregated.Values)
        {
            var (name, detail, tip) = Friendly(entry.Exe, entry.Services);
            if (!groups.TryGetValue(name, out var group))
            {
                group = new AggregatedGroup { Detail = detail, Tip = tip };
                groups[name] = group;
            }

            group.Ops += entry.Ops;
            group.New += entry.New;
        }

        if (totalOps > 0 && pcTotal >= PcBusyMbps)
        {
            var ranked = groups.OrderByDescending(pair => pair.Value.Ops).Take(3);
            foreach (var (name, group) in ranked)
            {
                var share = (double)group.Ops / totalOps;
                if (share < 0.1)
                {
                    continue;
                }

                var likely = share >= 0.4 && pcTotal >= 5;
                causes.Add(new SpikeCause
                {
                    Kind = "app",
                    Name = name,
                    Detail = group.Detail,
                    Tip = group.Tip,
                    Confidence = likely ? "likely" : "possibly",
                    Share = share,
                    Mbps = share * pcTotal,
                    New = group.New,
                });
            }
        }

        // 2. the router's signal
        var sigBefore = evidence.SignalWindow(spike.Start - 30, spike.Start - LookbackSeconds);
        var sigDuring = evidence.SignalWindow(t0, t1);
        if (sigBefore.Count > 0 && sigDuring.Count > 0)
        {
            var sinrBefore = sigBefore.Where(p => p.Sinr is not null).Select(p => p.Sinr!.Value).ToList();
            var rsrpBefore = sigBefore.Where(p => p.Rsrp is not null).Select(p => p.Rsrp!.Value).ToList();
            var sinrDuring = sigDuring.Where(p => p.Sinr is not null).Select(p => p.Sinr!.Value).ToList();
            var rsrpDuring = sigDuring.Where(p => p.Rsrp is not null).Select(p => p.Rsrp!.Value).ToList();
            var reasons = new List<string>();

            if (sinrBefore.Count > 0 && sinrDuring.Count > 0
                && sinrBefore.Average() - sinrDuring.Min() >= 3)
            {
                reasons.Add(
                    $"signal vs. noise fell {Fixed(sinrBefore.Average(), 0)} → {Fixed(sinrDuring.Min(), 0)} dB");
            }

            if (rsrpBefore.Count > 0 && rsrpDuring.Count > 0
                && rsrpBefore.Average() - rsrpDuring.Min() >= 5)
            {
                reasons.Add(
                    $"strength fell {Fixed(rsrpBefore.Average(), 0)} → {Fixed(rsrpDuring.Min(), 0)} dBm");
            }

            if (sigBefore[^1].Network == "5G" && sigDuring.Any(p => p.Network == "4G"))
            {
                reasons.Add("switched 5G → 4G");
            }
            else if (sigBefore.TakeLast(1).Concat(sigDuring)
                         .Where(p => p.Pci.Length > 0)
                         .Select(p => p.Pci)
                         .Distinct(StringComparer.Ordinal)
                         .Count() > 1)
            {
                reasons.Add("changed cell");
            }

            if (reasons.Count > 0)
            {
                causes.Add(new SpikeCause
                {
                    Kind = "signal",
                    Name = "Weak signal",
                    Detail = string.Join(", ", reasons),
                    Tip = SignalTip,
                    Confidence = causes.Count == 0 ? "likely" : "possibly",
                    Share = null,
                    Mbps = null,
                });
            }
        }

        // 3. other devices: router traffic well above this PC's
        var router = evidence.RouterWindow(t0, t1 + 5);
        double? routerTotal = router.Count > 0
            ? Peak(router.Select(row => row.Down)) + Peak(router.Select(row => row.Up))
            : null;
        if (routerTotal is not null && routerTotal.Value - pcTotal >= OtherDeviceMbps)
        {
            causes.Add(new SpikeCause
            {
                Kind = "other_device",
                Name = "Another device on your network",
                Detail = $"router {Fixed(routerTotal.Value, 1)} Mbps, this PC {Fixed(pcTotal, 1)} Mbps",
                Tip = OtherTip,
                Confidence = causes.Count == 0 ? "likely" : "possibly",
                Share = null,
                Mbps = routerTotal.Value - pcTotal,
            });
        }

        // 4. nothing found on our side
        if (causes.Count == 0)
        {
            var detail = $"this PC {Fixed(pcTotal, 1)} Mbps";
            if (routerTotal is not null)
            {
                detail += $", router {Fixed(routerTotal.Value, 1)} Mbps";
            }

            causes.Add(new SpikeCause
            {
                Kind = "isp",
                Name = "Internet / Ooredoo side",
                Detail = detail,
                Tip = IspTip,
                Confidence = "possibly",
                Share = null,
                Mbps = null,
            });
        }

        return new ClassifyResult
        {
            Causes = causes,
            PcDown = pcDown,
            PcUp = pcUp,
            RouterTotal = routerTotal,
            HasPcData = pc.Count > 0,
            HasProcData = totalOps > 0,
        };
    }

    /// <summary>How many spikes each cause was the top cause of, most first.</summary>
    public static IReadOnlyList<(string Name, int Count)> Offenders(IEnumerable<ClassifyResult> spikes)
    {
        return spikes
            .Where(spike => spike.Causes.Count > 0)
            .Select(spike => spike.Causes[0].Name)
            .GroupBy(name => name, StringComparer.Ordinal)
            .Select(group => (Name: group.Key, Count: group.Count()))
            .OrderByDescending(pair => pair.Count)
            .ToList();
    }

    public static bool IsBig(SpikeEvent spike) => (spike.Peak ?? 0) >= 150 || spike.Lost >= 2;

    private static double Peak(IEnumerable<double> values)
    {
        var list = values.ToList();
        return list.Count > 0 ? list.Max() : 0.0;
    }

    private static string Fixed(double value, int decimals) =>
        value.ToString("F" + decimals.ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture);

    private sealed class AggregatedProcess
    {
        public string Exe { get; init; } = "";

        public IReadOnlyList<string> Services { get; init; } = Array.Empty<string>();

        public long Ops { get; set; }

        public int New { get; set; }
    }

    private sealed class AggregatedGroup
    {
        public string Detail { get; init; } = "";

        public string Tip { get; init; } = "";

        public long Ops { get; set; }

        public int New { get; set; }
    }
}
