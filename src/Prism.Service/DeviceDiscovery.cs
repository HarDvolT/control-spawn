using System.Security.Cryptography;
using Prism.Core;
using Prism.Core.Ipc;
using Prism.Lighting;
using Prism.Hardware;

namespace Prism.Service;

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
    public Task SetApplianceAsync(DeviceState device, System.Text.Json.JsonElement values, CancellationToken token) => Observe ? Task.FromException(new InvalidOperationException("Observe mode.")) : appliances.SetAsync(device, values, token);
    public Task ActApplianceAsync(DeviceState device, int siid, int aiid, string? nonce, CancellationToken token) => Observe ? Task.FromException(new InvalidOperationException("Observe mode.")) : appliances.ActAsync(device, siid, aiid, nonce, token);
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
    private async Task DiscoveryAsync(CancellationToken token)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(5));
        var networkAt = TimeSpan.Zero;
        do
        {
            var result = new List<DeviceState>(); var completed = new List<string> { "openrgb", "lhm" }; var now = DateTimeOffset.UtcNow;
            try
            {
                if (clock.Elapsed >= networkAt)
                {
                    networkAt = clock.Elapsed + TimeSpan.FromSeconds(60);
                    result.AddRange(await ScanDevicesAsync(token, false)); completed.AddRange(["miio", "yeelight"]);
                }
                HardwareSnapshot snap; RgbDeviceState[] rgbDevices;
                lock (stateGate) { snap = snapshot; rgbDevices = devices; }
                result.AddRange(rgbDevices.Where(d => rgbConnected && d.Connected).Select(d => new DeviceState(d.Id, DeviceKind.RgbController, d.Name, "openrgb", now, now, DeviceStatus.Ready, ["sync", "case-map"])));
                result.AddRange(FanMap.Discover(snap.Sensors, controls.Headers).Where(f => f.Rpm > 0 && double.IsFinite(f.Rpm)).Select(f => new DeviceState($"fan:{f.Header}", DeviceKind.FanHeader, f.Name, "lhm", now, now, DeviceStatus.Ready, f.IsPump ? ["rpm"] : ["rpm", "curve"])));
                if (observationsHandler is not null) await observationsHandler(result, completed);
                if (!mock && !Observe)
                {
                    var before = appliances.States;
                    await appliances.PollAsync(Volatile.Read(ref settings).Devices, token);
                    if (Volatile.Read(ref settings).Discovery.CookingLightFlash && appliances.States.Any(a => a.Status == "done" && before.Any(b => b.Id == a.Id && b.Status is "cooking" or "preheating"))) TriggerLightingEvent(LightingEventKind.Notification);
                }
                warnings.TryRemove("discovery", out _);
            }
            // A settings file that can't be written (no rights, or another program holding it) must not stop the whole service.
            catch (Exception e) when (!token.IsCancellationRequested && e is IOException or UnauthorizedAccessException or System.Net.Sockets.SocketException or InvalidOperationException or OperationCanceledException)
            { warnings["discovery"] = "Device discovery temporarily unavailable."; }
        } while (await timer.WaitForNextTickAsync(token));
    }
}
