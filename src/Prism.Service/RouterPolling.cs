using Prism.Core.Ipc;
using Prism.Router;

namespace Prism.Service;

public interface IRouterBackend : IDisposable
{
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
            Nr(Metric.Rsrp), Nr(Metric.Sinr), Nr(Metric.Rsrq), Lte(Metric.Rsrp), Lte(Metric.Sinr), Lte(Metric.Rsrq), stats.Avg, stats.Loss, null);
    }
    public Task<SpeedTestResult?> TestSpeedAsync(CancellationToken token) => Task.Run(() => SpeedTest.Run((_, _, _) => { }, token), token);
    public void Dispose() { }
}
