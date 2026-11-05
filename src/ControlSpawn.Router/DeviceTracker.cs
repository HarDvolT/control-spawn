using System.Globalization;
using System.Net;
using System.Net.NetworkInformation;
using System.Text.RegularExpressions;

namespace ControlSpawn.Router;

/// <summary>
/// Devices on your network: seen from this PC (ARP table, optional ping sweep, reverse DNS) and,
/// when the widget is logged in, from the router's read-only connected-devices list
/// (MergeRouterList; the widget fetches it, this class never talks to the router).
///
/// It can list who is online, but not how much each one downloads: that would need the router to
/// report per-device traffic. RouterDeviceTraffic is the hook for that; RouterDeviceTrafficEnabled is off.
/// </summary>
public static class Devices
{
    /// <summary>Hook: per-device traffic from the router (not implemented, no router call).</summary>
    public const bool RouterDeviceTrafficEnabled = false;

    public const double RefreshSeconds = 30;
    public const double NewSeconds = 10 * 60;

    /// <summary>A small set of common vendor prefixes (first 3 bytes of the MAC).</summary>
    public static readonly IReadOnlyDictionary<string, string> Oui = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["00:1A:11"] = "Google", ["3C:5A:B4"] = "Google", ["F4:F5:D8"] = "Google", ["D8:6C:63"] = "Google",
        ["F0:D2:F1"] = "Amazon", ["74:C2:46"] = "Amazon", ["FC:65:DE"] = "Amazon", ["44:65:0D"] = "Amazon",
        ["00:1B:63"] = "Apple", ["28:CF:E9"] = "Apple", ["3C:07:54"] = "Apple", ["A4:83:E7"] = "Apple", ["F0:18:98"] = "Apple",
        ["BC:92:6B"] = "Apple", ["DC:A9:04"] = "Apple", ["8C:85:90"] = "Apple", ["AC:BC:32"] = "Apple", ["F8:FF:C2"] = "Apple",
        ["00:16:6C"] = "Samsung", ["5C:0A:5B"] = "Samsung", ["8C:77:12"] = "Samsung", ["A0:0B:BA"] = "Samsung",
        ["CC:07:AB"] = "Samsung", ["F4:7B:09"] = "Samsung", ["64:1C:AE"] = "Samsung", ["84:25:19"] = "Samsung",
        ["28:6C:07"] = "Xiaomi", ["64:09:80"] = "Xiaomi", ["9C:99:A0"] = "Xiaomi", ["F8:A4:5F"] = "Xiaomi", ["50:8F:4C"] = "Xiaomi",
        ["00:E0:FC"] = "Huawei", ["28:6E:D4"] = "Huawei", ["48:46:FB"] = "Huawei", ["E8:CD:2D"] = "Huawei", ["AC:E2:15"] = "Huawei",
        ["A4:3B:FA"] = "Oppo", ["2C:5B:B8"] = "Oppo", ["C8:F2:30"] = "Oppo",
        ["00:1E:75"] = "LG", ["10:68:3F"] = "LG", ["A8:23:FE"] = "LG", ["C4:36:6C"] = "LG",
        ["00:1D:BA"] = "Sony", ["FC:F1:52"] = "Sony", ["AC:9B:0A"] = "Sony", ["70:9E:29"] = "Sony",
        ["14:CC:20"] = "TP-Link", ["50:C7:BF"] = "TP-Link", ["C0:4A:00"] = "TP-Link", ["F4:F2:6D"] = "TP-Link", ["98:DA:C4"] = "TP-Link",
        ["00:1B:21"] = "Intel", ["3C:A9:F4"] = "Intel", ["8C:8D:28"] = "Intel", ["A4:4C:C8"] = "Intel", ["F8:63:3F"] = "Intel",
        ["00:E0:4C"] = "Realtek", ["52:54:00"] = "Virtual machine", ["00:15:5D"] = "Hyper-V",
        ["B8:27:EB"] = "Raspberry Pi", ["DC:A6:32"] = "Raspberry Pi", ["18:B4:30"] = "Nest",
        ["FC:3F:FC"] = "Router (ZLT)", ["8E:31:29"] = "Router (ZLT)",
    };

    private static readonly Regex ArpPattern = new(
        @"^\s*(\d+\.\d+\.\d+\.\d+)\s+([0-9a-fA-F]{2}(?:[-:][0-9a-fA-F]{2}){5})\s+(\w+)",
        RegexOptions.Multiline | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    public static string NormMac(string mac) => mac.Replace('-', ':').ToUpperInvariant();

    /// <summary>Phones and tablets use random "private" addresses: the locally-administered bit is set.</summary>
    public static bool IsPrivateMac(string mac)
    {
        var first = int.Parse(mac[..2], NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        return (first & 0x02) != 0;
    }

    public static string Vendor(string mac)
    {
        if (IsPrivateMac(mac))
        {
            return "Private address (usually a phone or tablet)";
        }

        return mac.Length >= 8 && Oui.TryGetValue(mac[..8], out var vendor) ? vendor : "";
    }

    /// <summary>[(ip, mac)] from <c>arp -a</c>, skipping broadcast/multicast and other subnets.</summary>
    public static List<(string Ip, string Mac)> ParseArp(string? text, Ipv4Subnet? subnet = null)
    {
        var result = new List<(string Ip, string Mac)>();
        foreach (Match match in ArpPattern.Matches(text ?? ""))
        {
            var ipText = match.Groups[1].Value;
            var mac = NormMac(match.Groups[2].Value);

            if (mac == "FF:FF:FF:FF:FF:FF" || mac.StartsWith("01:00:5E", StringComparison.Ordinal)
                || mac.StartsWith("33:33", StringComparison.Ordinal))
            {
                continue;
            }

            if (!IPAddress.TryParse(ipText, out var address))
            {
                continue;
            }

            var bytes = address.GetAddressBytes();
            if (bytes[0] is >= 224 and <= 239 || ipText.EndsWith(".255", StringComparison.Ordinal))
            {
                continue;
            }

            if (subnet is not null && !subnet.Contains(address))
            {
                continue;
            }

            result.Add((ipText, mac));
        }

        return result;
    }

    /// <summary>The /24 the router is on (the widget only cares about the router's LAN).</summary>
    public static Ipv4Subnet LocalSubnet(string routerIp = RouterClient.DefaultHost)
    {
        return IPAddress.TryParse(routerIp, out var address)
            ? new Ipv4Subnet(address, 24)
            : new Ipv4Subnet(IPAddress.Parse(RouterClient.DefaultHost), 24);
    }

    /// <summary>Merge the router's read-only connected-devices reply into the tracker.</summary>
    public static int MergeRouterList(
        DeviceTracker tracker,
        IReadOnlyDictionary<string, object?> resp,
        double? now = null)
    {
        var at = now ?? DeviceTracker.Now();
        var bands = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (key, label) in new[]
                 {
                     ("wlan24g_wifi_info", "Wi-Fi 2.4 GHz"),
                     ("wlan5g_wifi_info", "Wi-Fi 5 GHz"),
                 })
        {
            if (Raw.AsList(resp.GetValueOrDefault(key)) is not { } list)
            {
                continue;
            }

            foreach (var item in list)
            {
                if (Raw.AsMap(item) is not { } wifi || !Raw.Truthy(wifi.GetValueOrDefault("ip")))
                {
                    continue;
                }

                var rssi = Raw.Str(wifi.GetValueOrDefault("rssi")).Trim();
                var digits = rssi.TrimStart('-');
                var numeric = digits.Length > 0 && digits.All(char.IsDigit);
                bands[Raw.Str(wifi.GetValueOrDefault("ip"))] =
                    numeric ? $"{label} ({rssi} dBm)" : label;
            }
        }

        var count = 0;
        lock (tracker.SyncRoot)
        {
            if (Raw.AsList(resp.GetValueOrDefault("dhcp_list_info")) is not { } devices)
            {
                return 0;
            }

            foreach (var item in devices)
            {
                if (Raw.AsMap(item) is not { } device)
                {
                    continue;
                }

                var macText = Raw.Str(device.GetValueOrDefault("mac"));
                var ipText = Raw.Str(device.GetValueOrDefault("ip"));
                if (macText.Length == 0 || ipText.Length == 0)
                {
                    continue;
                }

                var mac = NormMac(macText);
                if (!tracker.Store.TryGetValue(mac, out var entry))
                {
                    entry = new DeviceInfo
                    {
                        Ip = ipText,
                        Name = "",
                        Vendor = Vendor(mac),
                        First = at,
                        Last = at,
                    };
                    tracker.Store[mac] = entry;
                }

                entry.Ip = ipText;
                entry.Last = at;

                var host = Raw.Str(device.GetValueOrDefault("hostname")).Trim();
                if (host.Length > 0 && host is not ("*" or "unknown"))
                {
                    entry.Name = host;
                }

                var iface = Raw.Str(device.GetValueOrDefault("interface"));
                entry.Link = bands.TryGetValue(ipText, out var band)
                    ? band
                    : iface == "wlan" ? "Wi-Fi" : iface == "eth" ? "Cable" : "";
                count++;
            }
        }

        return count;
    }

    /// <summary>
    /// Hook for a future per-device traffic source from the router. Off: returns null and makes
    /// no router call. The connected-devices list has a per-device "flow" field, but on this
    /// router it stays 0 for every device, so it cannot be used yet.
    /// </summary>
    public static double? RouterDeviceTraffic(IReadOnlyDictionary<string, object?> raw) => null;
}

/// <summary>Remembers devices seen on the LAN. <c>store</c> is a MAC -> device dictionary.</summary>
public sealed class DeviceTracker
{
    private readonly IArpTableProvider _arp;
    private readonly IReverseDns _dns;

    public DeviceTracker(
        Dictionary<string, DeviceInfo> store,
        string routerIp = RouterClient.DefaultHost,
        string? ownIp = null,
        IArpTableProvider? arp = null,
        IReverseDns? dns = null)
    {
        Store = store;
        Subnet = Devices.LocalSubnet(routerIp);
        RouterIp = routerIp;
        OwnIp = ownIp;
        Started = Now();
        _arp = arp ?? new ArpCommandProvider();
        _dns = dns ?? new DnsReverseLookup();
    }

    public Dictionary<string, DeviceInfo> Store { get; }

    public Ipv4Subnet Subnet { get; }

    public string RouterIp { get; }

    public string? OwnIp { get; set; }

    public double Started { get; set; }

    public bool Scanning { get; private set; }

    public object SyncRoot { get; } = new();

    /// <summary>Refreshes from the ARP table (or from the given text in tests).</summary>
    public List<(string Ip, string Mac)> Refresh(double? now = null, string? arpText = null)
    {
        var at = now ?? Now();
        var text = arpText ?? _arp.Read();
        var found = Devices.ParseArp(text, Subnet);

        lock (SyncRoot)
        {
            foreach (var (ip, mac) in found)
            {
                if (!Store.TryGetValue(mac, out var device))
                {
                    device = new DeviceInfo
                    {
                        Ip = ip,
                        Name = "",
                        Vendor = Devices.Vendor(mac),
                        First = at,
                        Last = at,
                    };
                    Store[mac] = device;
                }

                device.Ip = ip;
                device.Last = at;
            }
        }

        return found;
    }

    /// <summary>Reverse DNS for devices without a name (the router often knows DHCP names).</summary>
    public void ResolveNames()
    {
        List<(string Mac, string Ip)> todo;
        lock (SyncRoot)
        {
            todo = Store
                .Where(pair => pair.Value.Name.Length == 0)
                .Select(pair => (Mac: pair.Key, Ip: pair.Value.Ip))
                .ToList();
        }

        var options = new ParallelOptions { MaxDegreeOfParallelism = 8 };
        Parallel.ForEach(todo, options, item =>
        {
            var name = _dns.Lookup(item.Ip);
            if (name.Length == 0)
            {
                return;
            }

            lock (SyncRoot)
            {
                if (Store.TryGetValue(item.Mac, out var device) && name != device.Ip)
                {
                    device.Name = name;
                }
            }
        });
    }

    /// <summary>One gentle ping sweep of the /24 (on request only) so quiet devices show up.</summary>
    public void Sweep()
    {
        if (Scanning)
        {
            return;
        }

        Scanning = true;
        try
        {
            var hosts = Subnet.Hosts().Select(host => host.ToString()).ToList();
            var options = new ParallelOptions { MaxDegreeOfParallelism = 16 };
            Parallel.ForEach(hosts, options, host =>
            {
                try
                {
                    using var ping = new Ping();
                    ping.Send(host, 300);
                }
                catch (Exception e) when (e is PingException or NetworkInformationException
                                              or ArgumentException or InvalidOperationException)
                {
                    // Hosts that answer nothing are the normal case.
                }
            });

            Refresh();
            ResolveNames();
        }
        finally
        {
            Scanning = false;
        }
    }

    public Dictionary<string, DeviceInfo> Online(double? at = null, double window = Devices.RefreshSeconds * 3)
    {
        var now = at ?? Now();
        lock (SyncRoot)
        {
            return Store
                .Where(pair => Math.Abs(pair.Value.Last - now) <= window
                               || (pair.Value.First <= now && now <= pair.Value.Last))
                .ToDictionary(pair => pair.Key, pair => pair.Value.Clone());
        }
    }

    public string Label(string mac, DeviceInfo device)
    {
        if (device.Ip == RouterIp)
        {
            return "Router";
        }

        if (device.Ip == OwnIp)
        {
            return "This PC";
        }

        return !string.IsNullOrEmpty(device.Name) ? device.Name
            : !string.IsNullOrEmpty(device.Vendor) ? device.Vendor
            : "Unknown device";
    }

    public bool IsNew(DeviceInfo device, double? now = null)
    {
        var at = now ?? Now();
        return device.First > Started + 5 && at - device.First < Devices.NewSeconds;
    }

    internal static double Now() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0;
}
