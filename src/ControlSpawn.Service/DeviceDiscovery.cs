using System.Security.Cryptography;
using ControlSpawn.Core;
using ControlSpawn.Core.Ipc;
using ControlSpawn.Lighting;
using ControlSpawn.Hardware;

namespace ControlSpawn.Service;

public sealed partial class ServiceRuntime
{
    private Func<IReadOnlyList<DeviceState>, IReadOnlyList<string>, Task>? observationsHandler;
    private readonly SemaphoreSlim deviceDiscoveryGate = new(1, 1);
    private TimeSpan nextDeviceDiscovery;
    private TimeSpan lastDemand = TimeSpan.FromMinutes(-1);
    private IReadOnlyList<DeviceState> deviceObservations = [];
    private readonly Dictionary<uint, int> metadataRevisions = new();
    private readonly YeelightDiscovery yeelightDiscovery;
    private readonly bool useMockYeelight, allowMetadata;
    private readonly Func<DeviceState, CancellationToken, Task<IMiotClient>> miotFactory;
    private readonly ApplianceController appliances;
    public void SetDeviceObservationHandler(Func<IReadOnlyList<DeviceState>, IReadOnlyList<string>, Task> handler) => observationsHandler = handler;
    private static Task<IMiotClient> CreateMiotAsync(DeviceState device, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (device.DeviceId is not { } id || device.Ip is not { } ip) throw new IOException("Device not resolved.");
        var secret = new LampTokenStore().Load(id) ?? throw new IOException("Token missing.");
        try { return Task.FromResult<IMiotClient>(new MiotClient(new MiioLampClient(new UdpMiioTransport(ip), secret, new SystemClock(), id), id)); }
        finally { CryptographicOperations.ZeroMemory(secret); }
    }
    public Task<string> PrepareApplianceAsync(DeviceState device, int siid, int aiid, CancellationToken token) => Task.FromResult(appliances.Prepare(device, siid, aiid));
    public async Task SetApplianceAsync(DeviceState device, System.Text.Json.JsonElement values, CancellationToken token)
    {
        if (Observe) throw new InvalidOperationException("Observe mode.");
        await appliances.SetAsync(device, values, token); RefreshAppliancesSoon();
    }
    public async Task ActApplianceAsync(DeviceState device, int siid, int aiid, string? nonce, CancellationToken token)
    {
        if (Observe) throw new InvalidOperationException("Observe mode.");
        await appliances.ActAsync(device, siid, aiid, nonce, token); RefreshAppliancesSoon();
    }
    // The regular poll runs every 5 seconds; read the fryer again right after a command so pause or a new time shows at once.
    // The fryer can take a moment to switch state, so a few quick reads follow.
    // Quick taps restart the follow-up instead of stacking reads that would hold the fryer while the next tap waits.
    private CancellationTokenSource? applianceRefresh;
    private void RefreshAppliancesSoon()
    {
        var next = new CancellationTokenSource();
        Interlocked.Exchange(ref applianceRefresh, next)?.Cancel();
        _ = Task.Run(async () =>
        {
            try { foreach (var wait in new[] { 250, 750, 1500 }) { await Task.Delay(wait, next.Token); await appliances.PollAsync(Volatile.Read(ref settings).Devices, next.Token, quick: true); } }
            catch (Exception e) when (e is IOException or InvalidOperationException or ObjectDisposedException or OperationCanceledException) { }
            finally { Interlocked.CompareExchange(ref applianceRefresh, null, next); next.Dispose(); }
        });
    }
    public Task<IReadOnlyList<DeviceState>> FindDevicesAsync(CancellationToken token) => ScanDevicesAsync(token, true);
    private async Task<IReadOnlyList<DeviceState>> ScanDevicesAsync(CancellationToken token, bool demand)
    {
        await deviceDiscoveryGate.WaitAsync(token);
        try
        {
            if (clock.Elapsed < nextDeviceDiscovery && (!demand || clock.Elapsed - lastDemand < TimeSpan.FromSeconds(10))) return deviceObservations;
            if (demand) lastDemand = clock.Elapsed;
            nextDeviceDiscovery = clock.Elapsed + TimeSpan.FromSeconds(60);
            var result = new List<DeviceState>(); var now = DateTimeOffset.UtcNow;
            foreach (var d in await DiscoverLampsAsync(token))
                result.Add(new($"lamp:{d.DeviceId}", DeviceKind.Lamp, $"Mi Home {d.DeviceId}", "miio", now, now, DeviceStatus.NeedsToken, [], DeviceId: d.DeviceId, Ip: d.Ip));
            if (!useMockYeelight)
            {
                foreach (var d in await yeelightDiscovery.DiscoverAsync(TimeSpan.FromSeconds(1.5), token))
                {
                    uint? did = uint.TryParse(d.Id.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? d.Id[2..] : d.Id,
                        d.Id.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? System.Globalization.NumberStyles.HexNumber : System.Globalization.NumberStyles.None,
                        System.Globalization.CultureInfo.InvariantCulture, out var parsed) && parsed is > 0 and < uint.MaxValue ? parsed : null;
                    result.Add(new(did is { } id ? $"lamp:{id}" : $"yeelight:{d.Id}", DeviceKind.Lamp, $"Yeelight {d.Model}", "yeelight", now, now, DeviceStatus.Ready, d.SupportedMethods, Model: d.Model, DeviceId: did, Ip: d.Location.Host));
                }
            }
            if (allowMetadata)
            {
                // Only authenticated read-only metadata probes; never guess models from the hello packet.
                foreach (var item in result.Where(d => d.Source == "miio").ToArray())
                {
                    var old = Volatile.Read(ref settings).Devices.FirstOrDefault(d => d.DeviceId == item.DeviceId);
                    if (old?.Configured != true) continue;
                    int revision = lampRevisions.GetValueOrDefault(old.Id);
                    if (old.DeviceId is { } cached && metadataRevisions.GetValueOrDefault(cached, -1) == revision)
                    { result[result.IndexOf(item)] = old with { Ip = item.Ip, Status = old.OnlineStatus ?? DeviceStatus.Ready }; continue; }
                    try
                    {
                        await using var client = await miotFactory(item, token); var info = await client.InfoAsync(token);
                        string? model = info.TryGetProperty("model", out var m) ? m.GetString() : old.Model;
                        var knownLamp = model?.StartsWith("yeelink.light.", StringComparison.Ordinal) == true;
                        var spec = MiotSpec.Load(model ?? "");
                        result[result.IndexOf(item)] = item with { Name = model ?? item.Name, Model = model,
                            Kind = spec is not null || model?.Contains(".fryer.", StringComparison.Ordinal) == true ? DeviceKind.Appliance : knownLamp ? DeviceKind.Lamp : DeviceKind.NetworkDevice,
                            Status = spec is not null || knownLamp ? DeviceStatus.Ready : DeviceStatus.Unsupported,
                            Capabilities = knownLamp ? ["power", "color", "brightness"] : spec is not null ? ["properties", "cancel", "confirmed-start"] : ["info"],
                            Configured = true, Mac = info.TryGetProperty("mac", out var mac) ? mac.GetString() : null,
                            Firmware = info.TryGetProperty("fw_ver", out var fw) ? fw.GetString() : null };
                        if (item.DeviceId is { } deviceId) metadataRevisions[deviceId] = revision;
                    }
                    catch (Exception e) when (!token.IsCancellationRequested && e is IOException or InvalidOperationException or System.Net.Sockets.SocketException or OperationCanceledException or System.Text.Json.JsonException) { }
                }
            }
            deviceObservations = result; return result;
        }
        finally { deviceDiscoveryGate.Release(); }
    }
}
