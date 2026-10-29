using System.Buffers.Binary;
using System.Text.Json;
using Prism.Core;
using Prism.Core.Ipc;
using Prism.Hardware;
using Prism.Lighting;
using Xunit;

namespace Prism.Service.Tests;

public sealed class DiscoverySourceTests
{
    private sealed class Clock : IClock
    { public TimeSpan Elapsed { get; set; } public Task DelayAsync(TimeSpan d, CancellationToken t) { t.ThrowIfCancellationRequested(); Elapsed += d; return Task.CompletedTask; } }
    private sealed class Miio : IMiioDiscoveryTransport
    {
        public int Calls;
        public Task<IReadOnlyList<(string Ip, byte[] Packet)>> ScanAsync(byte[] hello, IReadOnlyList<string> targets, TimeSpan duration, CancellationToken token)
        {
            token.ThrowIfCancellationRequested(); Calls++;
            byte[] Packet(uint id) { var p = MiioProtocol.Hello(); BinaryPrimitives.WriteUInt32BigEndian(p.AsSpan(8), id); BinaryPrimitives.WriteUInt32BigEndian(p.AsSpan(12), 100); return p; }
            return Task.FromResult<IReadOnlyList<(string, byte[])>>([("192.0.2.1", Packet(470481367)), ("192.0.2.2", Packet(470481368))]);
        }
    }
    private sealed class Lan : IDiscoveryTransport
    {
        public int Calls;
        public Task<IReadOnlyList<string>> SearchAsync(TimeSpan duration, CancellationToken token)
        { token.ThrowIfCancellationRequested(); Calls++; return Task.FromResult<IReadOnlyList<string>>(["HTTP/1.1 200 OK\r\nLocation: yeelight://192.0.2.1:55443\r\nid: 470481367\r\nmodel: color5\r\nsupport: set_power set_rgb\r\n"]); }
    }
    private sealed class Metadata : IMiotClient
    {
        public int Infos;
        public Task<JsonElement> InfoAsync(CancellationToken token) { Infos++; return Task.FromResult(JsonSerializer.SerializeToElement(new { model = "yeelink.light.color5", fw_ver = "fixture-1", mac = "28:6C:07:00:00:01" })); }
        public Task<JsonElement> GetAsync(IReadOnlyList<MiotPropertySpec> p, CancellationToken t) => throw new InvalidOperationException("No property request expected.");
        public Task SetAsync(IReadOnlyList<(MiotPropertySpec Property, JsonElement Value)> p, CancellationToken t) => throw new InvalidOperationException("No write expected.");
        public Task ActionAsync(MiotActionSpec a, IReadOnlyList<JsonElement> p, CancellationToken t) => throw new InvalidOperationException("No action expected.");
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
    [Fact]
    public async Task SourcesMergeAndMetadataIsReadOnceWithDemandAndBackgroundRateLimits()
    {
        var clock = new Clock(); var miio = new Miio(); var lan = new Lan(); var metadata = new Metadata();
        await using var runtime = new ServiceRuntime(true, new FakeSensorSource(), new FakeFanControlPort(), new FakeHealthProbe(), new FakeRgbBackend(), null, null,
            miioDiscovery: new(miio, () => ["192.0.2.100"]), clock: clock, yeelightDiscovery: new(lan), miotFactory: (_, _) => Task.FromResult<IMiotClient>(metadata));
        var now = DateTimeOffset.UtcNow;
        var known = new DeviceState("lamp:470481367", DeviceKind.Lamp, "Lamp", "miio", now, now, DeviceStatus.Ready, [], DeviceId: 470481367, Configured: true);
        await runtime.ApplyAsync(new() { Devices = [known] }, default);
        var found = await runtime.FindDevicesAsync(default);
        var merged = DeviceRegistry.Observe([known], found, now);
        Assert.Equal(2, merged.Count); Assert.Equal(1, metadata.Infos);
        var identified = merged.Single(d => d.DeviceId == 470481367); Assert.Equal("yeelink.light.color5", identified.Model); Assert.Equal("fixture-1", identified.Firmware); Assert.Equal("286C07000001", identified.Mac);
        await runtime.ApplyAsync(new() { Devices = merged }, default);
        await Task.WhenAll(runtime.FindDevicesAsync(default), runtime.FindDevicesAsync(default)); Assert.Equal(1, lan.Calls);
        clock.Elapsed += TimeSpan.FromSeconds(61); await runtime.FindDevicesAsync(default);
        Assert.Equal(2, lan.Calls); Assert.Equal(2, miio.Calls); Assert.Equal(1, metadata.Infos);
    }
}
