using ControlSpawn.Core;
using Xunit;

namespace ControlSpawn.Lighting.Tests;

public sealed class MiioDiscoveryTests
{
    private sealed class Transport : IMiioDiscoveryTransport
    {
        public IReadOnlyList<string> Targets { get; private set; } = [];
        public Task<IReadOnlyList<(string Ip, byte[] Packet)>> ScanAsync(byte[] hello, IReadOnlyList<string> targets, TimeSpan duration, CancellationToken token)
        {
            Assert.Equal(MiioProtocol.Hello(), hello); Assert.Equal(TimeSpan.FromMilliseconds(1500), duration);
            Targets = targets;
            return Task.FromResult<IReadOnlyList<(string, byte[])>>([
                ("192.0.2.175", Convert.FromHexString("21310020000000001c10fe7101020304ffffffffffffffffffffffffffffffff")),
                ("192.0.2.246", Convert.FromHexString("21310020000000001c05869701020305ffffffffffffffffffffffffffffffff")),
                ("192.0.2.246", Convert.FromHexString("21310020000000001c05869701020305ffffffffffffffffffffffffffffffff")),
                ("192.0.2.3", new byte[32]), ("192.0.2.4", MiioProtocol.Hello()), ("invalid", MiioProtocol.Hello())]);
        }
    }
    [Fact]
    public async Task HelloFixturesFindTwoStableIdsAndIgnoreDuplicatesAndMalformedPackets()
    {
        var transport = new Transport(); var discovery = new MiioDiscovery(transport, () => ["192.0.2.5", "192.0.2.6", "127.0.0.1", "::1"]);
        var found = await discovery.DiscoverAsync();
        Assert.Equal(2, found.Count);
        Assert.Equal("192.0.2.175", found.Single(d => d.DeviceId == 0x1c10fe71).Ip);
        Assert.Equal(0x01020304u, found.Single(d => d.DeviceId == 0x1c10fe71).Stamp);
        Assert.Equal(new[] { "255.255.255.255", "192.0.2.255" }, transport.Targets);
    }
    [Fact]
    public async Task LegacyHostReceivesHelloAlongsideBroadcastsAndCancellationDoesNotScan()
    {
        var transport = new Transport(); var discovery = new MiioDiscovery(transport, () => ["192.0.2.5"]);
        await discovery.DiscoverAsync(legacyHosts: ["192.0.2.8"]);
        Assert.Contains("192.0.2.8", transport.Targets);
        using var stop = new CancellationTokenSource(); stop.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => discovery.DiscoverAsync(stop.Token));
    }
    private sealed class Protector : ILampSecretProtector
    {
        public byte[] Protect(byte[] value) => value.Select(b => (byte)(b ^ 0x55)).ToArray();
        public byte[] Unprotect(byte[] value) => Protect(value);
    }
    [Fact]
    public void TokensAreIndependentPerIdAndLegacyRemainsReadableUntilMigrated()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "lamp-" + Guid.NewGuid());
        var store = new LampTokenStore(path, new Protector());
        try
        {
            store.Save(new byte[16]); store.Save(1, Enumerable.Repeat((byte)1, 16).ToArray()); store.Save(2, Enumerable.Repeat((byte)2, 16).ToArray());
            Assert.Equal(new byte[16], store.Load()); Assert.All(store.Load(1)!, b => Assert.Equal(1, b)); Assert.All(store.Load(2)!, b => Assert.Equal(2, b));
            store.Remove(1); Assert.Null(store.Load(1)); Assert.NotNull(store.Load(2));
            store.DeleteLegacy(); Assert.Null(store.Load()); Assert.NotNull(store.Load(2));
        }
        finally { foreach (var file in new[] { path, path + ".1", path + ".2" }) if (File.Exists(file)) File.Delete(file); }
    }
    private sealed class WrongDevice : IMiioTransport
    {
        public int Calls;
        public Task<byte[]> ExchangeAsync(byte[] packet, CancellationToken token)
        { Calls++; return Task.FromResult(Convert.FromHexString("21310020000000001c05869701020305ffffffffffffffffffffffffffffffff")); }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
    [Fact]
    public async Task ChangedDhcpHostCannotSendAuthenticatedCommandToDifferentDevice()
    {
        var transport = new WrongDevice(); await using var lamp = new MiioLampClient(transport, new byte[16], new ManualClock(), 0x1c10fe71);
        await Assert.ThrowsAsync<InvalidOperationException>(() => lamp.SetPowerAsync(true)); Assert.Equal(1, transport.Calls);
    }
}
