using System.Net;
using System.Text;
using System.Text.Json;
using Prism.Core;
using Xunit;

namespace Prism.Lighting.Tests;

public sealed class YeelightTests
{
    [Fact]
    public void JsonRpcUsesCrlfAndPackedRgb()
    {
        string text = Encoding.UTF8.GetString(YeelightMessages.Encode(7, "set_rgb", YeelightMessages.Color(new(1, 2, 3))));
        Assert.Equal("{\"id\":7,\"method\":\"set_rgb\",\"params\":[66051,\"smooth\",100]}\r\n", text);
        Assert.Equal(new object[] { "off", "smooth", 100 }, YeelightMessages.Power(false));
        Assert.Throws<ArgumentOutOfRangeException>(() => YeelightMessages.Brightness(0));
        Assert.Throws<ArgumentOutOfRangeException>(() => YeelightMessages.ColorTemperature(1000));
        Assert.Equal("500,1,255,100", new YeelightFlowStep(500, 1, 255, 100).Format());
        Assert.Throws<ArgumentOutOfRangeException>(() => new YeelightFlowStep(10, 1, 255, 100).Format());
    }
    [Fact]
    public async Task SlidingWindowAndSpacingKeepNormalCommandsWithinQuota()
    {
        var clock = new ManualClock(); var limiter = new YeelightRateLimiter(clock); var times = new List<TimeSpan>();
        for (int i = 0; i < 121; i++) { await limiter.WaitAsync(default); times.Add(clock.Elapsed); }
        Assert.Equal(TimeSpan.FromSeconds(120), times[^1]);
        Assert.All(times, time => Assert.True(times.Count(t => t > time - TimeSpan.FromMinutes(1) && t <= time) <= 60));
        using var cancel = new CancellationTokenSource(); cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => limiter.WaitAsync(cancel.Token));
    }
    [Fact]
    public async Task DiscoveryParsesCaseInsensitiveHeadersAndDeduplicatesWithoutNetwork()
    {
        const string response = "HTTP/1.1 200 OK\r\nLOCATION: yeelight://192.0.2.1:55443\r\nid: abc\r\nmodel: color5\r\nsupport: set_rgb set_music\r\n\r\n";
        var transport = new FakeDiscoveryTransport { Responses = new[] { response, "garbage", response } };
        var device = Assert.Single(await new YeelightDiscovery(transport).DiscoverAsync(TimeSpan.FromSeconds(1)));
        Assert.Equal(55443, device.Location.Port); Assert.Equal("color5", device.Model);
        Assert.Contains("set_music", device.SupportedMethods);
        Assert.Contains("MAN: \"ssdp:discover\"\r\nST: wifi_bulb\r\n\r\n", YeelightDiscovery.SearchMessage);
    }
    [Fact]
    public async Task NormalTransportConsumesResponsesAndNotificationsAndFormatsCommands()
    {
        byte[] replies = Encoding.UTF8.GetBytes("{\"method\":\"props\",\"params\":{}}\r\n" + string.Concat(
            Enumerable.Range(1, 5).Select(i => $"{{\"id\":{i},\"result\":[\"ok\"]}}\r\n")));
        var wire = new DuplexFixtureStream(replies); var factory = new FakeConnectionFactory(); factory.Streams.Enqueue(wire);
        var clock = new ManualClock(); await using var client = new YeelightClient(factory, clock, new FakeMusicListener());
        await client.ConnectAsync("fake"); await client.SetPowerAsync(true); await client.SetBrightnessAsync(50);
        await client.SetColorAsync(new(255, 0, 0)); await client.SetColorTemperatureAsync(4000);
        await client.StartColorFlowAsync(0, 1, new[] { new YeelightFlowStep(500, 1, 255, 100) });
        string[] messages = Encoding.UTF8.GetString(wire.Output.ToArray()).Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        string[] methods = messages.Select(s => { using var doc = JsonDocument.Parse(s); return doc.RootElement.GetProperty("method").GetString()!; }).ToArray();
        Assert.Equal(new[] { "set_power", "set_bright", "set_rgb", "set_ct_abx", "start_cf" }, methods);
        Assert.Equal(TimeSpan.FromSeconds(4), clock.Elapsed);
    }
    [Fact]
    public async Task MusicModeUsesCallbackStreamAndNormalModeResumesRateLimiting()
    {
        var normal = new DuplexFixtureStream(Encoding.UTF8.GetBytes("{\"id\":1,\"result\":[\"ok\"]}\r\n{\"id\":4,\"result\":[\"ok\"]}\r\n"));
        var callback = new MemoryStream(); var factory = new FakeConnectionFactory(); factory.Streams.Enqueue(normal);
        var clock = new ManualClock(); await using var client = new YeelightClient(factory, clock, new FakeMusicListener { Connection = callback });
        await client.ConnectAsync("fake");
        await client.StartMusicModeAsync(IPAddress.Parse("192.0.2.2"), IPAddress.Parse("192.0.2.1"));
        Assert.True(client.IsMusicMode);
        await client.SetColorAsync(new(1, 2, 3)); await client.SetColorAsync(new(4, 5, 6));
        Assert.Equal(TimeSpan.Zero, clock.Elapsed);
        Assert.Equal(2, Encoding.UTF8.GetString(callback.ToArray()).Split("\r\n", StringSplitOptions.RemoveEmptyEntries).Length);
        await client.StopMusicModeAsync(); Assert.False(client.IsMusicMode);
        Assert.Equal(TimeSpan.FromSeconds(1), clock.Elapsed);
    }
    [Fact]
    public async Task BulbErrorsSurfaceAndInvalidateTheConnection()
    {
        var wire = new DuplexFixtureStream(Encoding.UTF8.GetBytes("{\"id\":1,\"error\":{\"code\":-1,\"message\":\"fake\"}}\r\n"));
        var factory = new FakeConnectionFactory(); factory.Streams.Enqueue(wire);
        await using var client = new YeelightClient(factory, new ManualClock(), new FakeMusicListener());
        await client.ConnectAsync("fake");
        await Assert.ThrowsAsync<IOException>(() => client.SetPowerAsync(true));
        await Assert.ThrowsAsync<InvalidOperationException>(() => client.SetPowerAsync(true));
    }
    [Fact]
    public async Task SyncAveragesSmoothsAndCapsMusicUpdatesToTenPerSecond()
    {
        var clock = new ManualClock(); var lamp = new FakeLampClient { IsMusicMode = true }; var sync = new LampSync(lamp, clock);
        Assert.True(await sync.PushAsync(new[] { new RgbColor(100, 0, 0), new RgbColor(0, 100, 0) }));
        Assert.Equal(new RgbColor(50, 50, 0), lamp.Colors[0]);
        for (int i = 1; i < 100; i++) { clock.Elapsed = TimeSpan.FromMilliseconds(i * 10); await sync.PushAsync(new[] { new RgbColor(255, 255, 255) }); }
        Assert.Equal(10, lamp.Colors.Count); Assert.InRange(lamp.Colors[1].R, (byte)51, (byte)254);
        lamp.IsMusicMode = false;
        clock.Elapsed += TimeSpan.FromMilliseconds(500); Assert.False(await sync.PushAsync(new[] { RgbColor.Black }));
        clock.Elapsed += TimeSpan.FromSeconds(1); Assert.True(await sync.PushAsync(new[] { RgbColor.Black }));
    }
    [Fact]
    public async Task MockCompositionOnlyCreatesFakeAdapters()
    {
        var parts = LightingComposition.Create(new[] { "--mock" }, new ManualClock());
        Assert.IsType<FakeRgbBackend>(parts.Rgb); Assert.IsType<FakeLampClient>(parts.Lamp);
        Assert.Empty(await parts.Discovery.DiscoverAsync(TimeSpan.FromSeconds(1)));
    }
}
