using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text.RegularExpressions;

namespace ControlSpawn.Router;

/// <summary>IPv4 subnet membership (the widget only cares about the router's /24 LAN).</summary>
public sealed class Ipv4Subnet
{
    private readonly uint _network;
    private readonly uint _mask;

    public Ipv4Subnet(IPAddress network, uint prefixLength)
    {
        var address = ToUInt32(network);
        _mask = prefixLength == 0 ? 0u : uint.MaxValue << (int)(32 - prefixLength);
        _network = address & _mask;
        PrefixLength = prefixLength;
    }

    public uint PrefixLength { get; }

    public bool Contains(IPAddress address)
    {
        if (address.AddressFamily != AddressFamily.InterNetwork)
        {
            return false;
        }

        return (ToUInt32(address) & _mask) == _network;
    }

    /// <summary>Every usable host address in the subnet, in ascending order.</summary>
    public IEnumerable<IPAddress> Hosts()
    {
        var broadcast = _network | ~_mask;
        for (var value = _network + 1; value < broadcast; value++)
        {
            yield return FromUInt32(value);
        }
    }

    private static uint ToUInt32(IPAddress address)
    {
        var bytes = address.GetAddressBytes();
        return ((uint)bytes[0] << 24) | ((uint)bytes[1] << 16) | ((uint)bytes[2] << 8) | bytes[3];
    }

    private static IPAddress FromUInt32(uint value) => new(new[]
    {
        (byte)(value >> 24), (byte)(value >> 16), (byte)(value >> 8), (byte)value,
    });
}

/// <summary>What the tracker remembers about one device, keyed by MAC address.</summary>
public sealed class DeviceInfo
{
    public string Ip { get; set; } = "";

    public string Name { get; set; } = "";

    public string Vendor { get; set; } = "";

    public double First { get; set; }

    public double Last { get; set; }

    /// <summary>"Wi-Fi 2.4 GHz (-54 dBm)", "Wi-Fi", "Cable" or empty.</summary>
    public string Link { get; set; } = "";

    public DeviceInfo Clone() => (DeviceInfo)MemberwiseClone();
}

/// <summary>Reads the ARP table as text. Injectable so tests never run <c>arp</c>.</summary>
public interface IArpTableProvider
{
    string Read();
}

/// <summary>Reverse DNS for the LAN devices that have no name yet.</summary>
public interface IReverseDns
{
    string Lookup(string ip);
}

/// <summary>Runs <c>arp -a</c> (no console window, read-only).</summary>
public sealed class ArpCommandProvider : IArpTableProvider
{
    public string Read()
    {
        try
        {
            var startInfo = new ProcessStartInfo("arp", "-a")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };

            using var process = Process.Start(startInfo);
            if (process is null)
            {
                return "";
            }

            var output = process.StandardOutput.ReadToEnd();
            process.WaitForExit(10_000);
            return output;
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException
                                      or IOException or PlatformNotSupportedException)
        {
            return "";
        }
    }
}

/// <summary>DNS reverse lookup with a short timeout, first label only.</summary>
public sealed class DnsReverseLookup : IReverseDns
{
    public string Lookup(string ip)
    {
        try
        {
            var task = Dns.GetHostEntryAsync(ip);
            if (!task.Wait(TimeSpan.FromSeconds(1.5)))
            {
                return "";
            }

            return task.Result.HostName.Split('.')[0];
        }
        catch (Exception e) when (e is SocketException or ArgumentException or AggregateException)
        {
            return "";
        }
    }
}
