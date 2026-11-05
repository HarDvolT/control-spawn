using ControlSpawn.Core.Ipc;
using ControlSpawn.Router;

namespace ControlSpawn.Service;

public interface IRouterBackend : IDisposable
{
    IReadOnlyList<DeviceState>? ConnectedDevices() => null;
    RouterState Poll();
    Task<SpeedTestResult?> TestSpeedAsync(CancellationToken token);
}
public sealed class FakeRouterBackend : IRouterBackend
{
    public RouterState Poll() => new(true, "5G", "n78", 86, 4, "Excellent", "Mock router", -85, 23, -9, -91, 18, -10, 14, 0, null);
    public Task<SpeedTestResult?> TestSpeedAsync(CancellationToken token) => Task.FromResult<SpeedTestResult?>(new(14, 2, 250, 35, DateTimeOffset.UtcNow.ToUnixTimeSeconds()));
    public void Dispose() { }
}
/// <summary>One shared router client and one saved session; called by a single polling loop.</summary>
public sealed class RouterBackend : IRouterBackend
{
    public IReadOnlyList<DeviceState>? ConnectedDevices()
    {
        var response = client.GetDevices(); if (response is null) return null;
        var store = new Dictionary<string, DeviceInfo>(); var tracker = new DeviceTracker(store);
        Devices.MergeRouterList(tracker, response); var now = DateTimeOffset.UtcNow;
        return store.Where(p => ControlSpawn.Core.DeviceRegistry.NormalizeMac(p.Key) is not null).Select(p => new DeviceState(
            "network:" + ControlSpawn.Core.DeviceRegistry.NormalizeMac(p.Key), DeviceKind.NetworkDevice,
            string.IsNullOrWhiteSpace(p.Value.Name) ? p.Value.Ip : p.Value.Name, "router", now, now,
            DeviceStatus.Ready, ["info"], Ip: p.Value.Ip, Mac: p.Key, Vendor: p.Value.Vendor)).ToArray();
    }
    private readonly CredentialStore credentials;
    private readonly RouterClient client;
    private readonly Queue<PingSample> pingHistory = new();
    public RouterBackend(string host, CredentialStore? credentials = null)
    {
        this.credentials = credentials ?? new();
        var password = this.credentials.LoadPassword() ?? throw new InvalidOperationException("Router credentials not configured for the service identity.");
        client = new(password, host, sessionId: this.credentials.LoadSession() ?? "");
    }
    public RouterState Poll()
    {
        var data = client.GetSignal(); credentials.SaveSession(client.SessionId);
        var summary = Ratings.Summarize(data);
        double now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000d;
        pingHistory.Enqueue(new(now, PingMonitor.PingOnce(PingMonitor.DefaultTarget)));
        while (pingHistory.TryPeek(out var old) && old.Time < now - 300) pingHistory.Dequeue();
        var stats = PingMonitor.Stats(pingHistory, now, 60);
        double? Nr(Metric m) => summary.Nr.Values.GetValueOrDefault(m);
        double? Lte(Metric m) => summary.Lte.Values.GetValueOrDefault(m);
        return new(true, summary.Network, summary.Band, summary.Score is { } score ? (int)Math.Round(score) : null,
            summary.Bars, Ratings.Verdict(summary.Overall), Ratings.PlainText(summary.Overall),
            Nr(Metric.Rsrp), Nr(Metric.Sinr), Nr(Metric.Rsrq), Lte(Metric.Rsrp), Lte(Metric.Sinr), Lte(Metric.Rsrq), stats.Avg, stats.Loss, null, RouterProjection.Details(data, client.Host));
    }
    public Task<SpeedTestResult?> TestSpeedAsync(CancellationToken token) => Task.Run(() => SpeedTest.Run((_, _, _) => { }, token), token);
    public void Dispose() { }
}

/// <summary>Projects only values returned by the read-only signal endpoint. No inferred tower location or traffic.</summary>
public static class RouterProjection
{
    public static RouterDetails Details(IReadOnlyDictionary<string, object?> data, string host)
    {
        string? Text(params string[] keys) => keys.Select(k => Raw.Str(data.GetValueOrDefault(k)).Trim()).FirstOrDefault(v => v.Length > 0);
        double? Number(params string[] keys) => keys.Select(k => Raw.ToFloat(data.GetValueOrDefault(k))).FirstOrDefault(v => v is { } n && double.IsFinite(n));
        var scan = Scanner.Analyze(data);
        var nr = scan.Nr;
        var lte = scan.Lte;
        return new(
            nr is null ? null : new(nr.Band, nr.Pci, Text("CELL_ID_5G"), nr.Earfcn, Text("bandwidth_5g", "BANDWIDTH_5G"), Number("RSSI_5G")),
            new(lte.Band, lte.Pci, Text("CELL_ID"), lte.Earfcn, Text("bandwidth", "BANDWIDTH"), Number("RSSI")),
            Text("carrier_aggregation"), Number("tower_distance_km"),
            Text("model", "device_model"), host, Number("uptime", "uptime_seconds"),
            Number("monthly_bytes"),
            scan.Neighbors.Select(n => new RouterNeighbor(n.Type, n.Earfcn, n.Pci, n.Rsrp, n.Sinr)).ToArray());
    }
}
