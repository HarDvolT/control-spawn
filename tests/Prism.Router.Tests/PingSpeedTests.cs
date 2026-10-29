namespace Prism.Router.Tests;

public class PingParseTests
{
    private static string Reply(string line) =>
        "\r\nPinging 1.1.1.1 with 32 bytes of data:\r\n" + line + "\r\n\r\nPing statistics for 1.1.1.1:\r\n";

    [Fact]
    public void English()
    {
        Assert.Equal(39.0, PingMonitor.ParsePing(Reply("Reply from 1.1.1.1: bytes=32 time=39ms TTL=57")));
    }

    [Fact]
    public void UnderOneMs()
    {
        Assert.Equal(0.5, PingMonitor.ParsePing(Reply("Reply from 192.168.0.1: bytes=32 time<1ms TTL=64")));
    }

    [Fact]
    public void Localized()
    {
        Assert.Equal(12.0, PingMonitor.ParsePing(Reply("Antwort von 1.1.1.1: Bytes=32 Zeit=12ms TTL=57")));
        Assert.Equal(23.0, PingMonitor.ParsePing(Reply("Réponse de 1.1.1.1 : octets=32 temps=23 ms TTL=57")));
        Assert.Equal(101.0, PingMonitor.ParsePing(Reply("Respuesta desde 1.1.1.1: bytes=32 tiempo=101ms TTL=57")));
        Assert.Equal(0.5, PingMonitor.ParsePing(Reply("Réponse de 8.8.8.8 : octets=32 temps<1ms TTL=118")));
    }

    [Fact]
    public void Failures()
    {
        Assert.Null(PingMonitor.ParsePing(Reply("Request timed out.")));
        Assert.Null(PingMonitor.ParsePing(Reply("Délai d'attente de la demande dépassé.")));
        Assert.Null(PingMonitor.ParsePing(Reply("Reply from 192.168.0.1: Destination host unreachable.")));
        Assert.Null(PingMonitor.ParsePing("Ping request could not find host nowhere.invalid."));
        Assert.Null(PingMonitor.ParsePing(""));
    }

    [Fact]
    public void StatisticsFooterIsNotAReply()
    {
        const string text = "Minimum = 22ms, Maximum = 39ms, Average = 30ms";
        Assert.Null(PingMonitor.ParsePing(text));
    }
}

public class PingStatsTests
{
    private static PingSample[] Samples(params (double Time, double? Ms)[] values) =>
        values.Select(value => new PingSample(value.Time, value.Ms)).ToArray();

    [Fact]
    public void LossAvgJitter()
    {
        var samples = Samples((0, 20.0), (1, null), (2, 30.0), (3, 26.0), (4, null));
        var stats = PingMonitor.Stats(samples, 4, 60);
        Assert.Equal(5, stats.Count);
        Assert.Equal(40.0, stats.Loss!.Value, 12);
        Assert.Equal(76.0 / 3.0, stats.Avg!.Value, 12);
        Assert.Equal(20.0, stats.Min!.Value);
        Assert.Equal(30.0, stats.Max!.Value);
        Assert.Equal((10 + 4) / 2.0, stats.Jitter!.Value, 12);
    }

    [Fact]
    public void Window()
    {
        var samples = Samples((0, 500.0), (100, 20.0), (101, 22.0));
        var stats = PingMonitor.Stats(samples, 101, 60);
        Assert.Equal(2, stats.Count);
        Assert.Equal(22.0, stats.Max!.Value);
    }

    [Fact]
    public void Empty()
    {
        var stats = PingMonitor.Stats(Array.Empty<PingSample>(), 0, 60);
        Assert.Null(stats.Loss);
        Assert.Null(stats.Avg);
        Assert.Null(stats.Jitter);
    }

    [Fact]
    public void Rating()
    {
        Assert.Equal(Ratings.Excellent, PingMonitor.PingRating(29.9));
        Assert.Equal(Ratings.Good, PingMonitor.PingRating(30));
        Assert.Equal(Ratings.Fair, PingMonitor.PingRating(99));
        Assert.Equal(Ratings.Poor, PingMonitor.PingRating(100));
        Assert.Null(PingMonitor.PingRating(null));
    }

    [Fact]
    public void MonitorRecordsAndRetargets()
    {
        using var monitor = new PingMonitor(target: "x", pinger: _ => 12.0);
        monitor.AddSample(0, 5.0);
        monitor.SetTarget("8.8.8.8");
        Assert.Equal("8.8.8.8", monitor.Target);
        Assert.Empty(monitor.Snapshot());
    }
}

public class SpeedMathTests
{
    [Fact]
    public void Mbps()
    {
        Assert.Equal(100.0, SpeedTest.Mbps(12_500_000, 1.0), 12);
        Assert.Equal(0.0, SpeedTest.Mbps(100, 0));
    }

    [Fact]
    public void PingStats()
    {
        var (median, jitter) = SpeedTest.PingStats(new[] { 20.0, 30.0, 25.0, 40.0 });
        Assert.Equal(27.5, median!.Value);
        Assert.Equal((10 + 5 + 15) / 3.0, jitter!.Value, 12);

        var single = SpeedTest.PingStats(new[] { 12.0 });
        Assert.Equal(12.0, single.Median!.Value);
        Assert.Equal(0.0, single.Jitter!.Value);

        var none = SpeedTest.PingStats(Array.Empty<double>());
        Assert.Null(none.Median);
        Assert.Null(none.Jitter);
    }

    [Fact]
    public void Verdicts()
    {
        Assert.Contains("Great", SpeedTest.SpeedVerdict(150));
        Assert.Contains("4K", SpeedTest.SpeedVerdict(25));
        Assert.Contains("OK", SpeedTest.SpeedVerdict(8));
        Assert.Contains("Slow", SpeedTest.SpeedVerdict(3));
        Assert.Equal("", SpeedTest.SpeedVerdict(null));
        Assert.Contains("great", SpeedTest.PingVerdict(10));
        Assert.Contains("lag", SpeedTest.PingVerdict(150));
    }

    [Fact]
    public void CancelBeforeStart()
    {
        using var cancel = new CancellationTokenSource();
        cancel.Cancel();
        var transport = new CountingTransport();

        var result = SpeedTest.Run((_, _, _) => { }, cancel.Token, transport);

        Assert.Null(result);
        Assert.Single(transport.Requests); // only the warm-up request, no measuring
    }

    private sealed class CountingTransport : ISpeedTestTransport
    {
        public List<string> Requests { get; } = new();

        public ISpeedTestConnection Connect() => new Connection(this);

        private sealed class Connection : ISpeedTestConnection
        {
            private readonly CountingTransport _owner;

            public Connection(CountingTransport owner) => _owner = owner;

            public Stream Get(string path)
            {
                _owner.Requests.Add(path);
                return Stream.Null;
            }

            public void Post(string path, long byteCount, Action<int> onSent, CancellationToken cancel) =>
                throw new NotSupportedException();

            public void Dispose()
            {
            }
        }
    }
}
