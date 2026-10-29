using System.Net.Sockets;
using Prism.Core;
using Prism.Core.Ipc;
using Prism.Hardware;
using Prism.Lighting;
using Prism.Router;

namespace Prism.Service;

public sealed partial class ServiceRuntime
{
    private sealed class RgbModule(ServiceRuntime runtime) : HostedModule("rgb", ok => runtime.ModuleHealth("rgb", ok))
    {
        protected override Task ExecuteAsync(CancellationToken token) => LightingAsync(token);
        private async Task LightingAsync(CancellationToken token)
        {
            if (runtime.Observe) { runtime.warnings["observe"] = "Observe mode: fans, RGB and lamp are not controlled."; await Task.Delay(Timeout.Infinite, token); }
            TimeSpan reconnect = TimeSpan.Zero, nextRefresh = TimeSpan.Zero, nextOfflineLog = TimeSpan.Zero;
            var pacer = new DeviceFramePacer();
            var directModes = new HashSet<string>();
            static (RgbDeviceState[] Devices, Dictionary<string, uint> Indices) Identify(OpenRgbClient client)
            {
                var identities = new Dictionary<string, int>(); var indices = new Dictionary<string, uint>();
                var found = client.Controllers.Select((d, i) =>
                {
                    var metadata = string.Join("|", d.Type, d.Vendor, d.Name, d.Serial, d.Location);
                    var digest = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(metadata)))[..24];
                    int occurrence = identities.GetValueOrDefault(digest); identities[digest] = occurrence + 1;
                    string identity = $"rgb:{digest}:{occurrence}";
                    indices[identity] = (uint)i;
                    return LedLayers.Project(identity, d);
                }).ToArray();
                return (found, indices);
            }
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1d / 30));
            do
            {
                var value = Volatile.Read(ref runtime.settings);
                await runtime.rgbGate.WaitAsync(token);
                try
                {
                    if (!runtime.mock && !runtime.rgbConnected && runtime.clock.Elapsed >= reconnect)
                    {
                        if (value.LaunchOpenRgb && value.OpenRgbPath is not null) runtime.rgbProcess.Start(value.OpenRgbPath);
                        var client = (OpenRgbClient)runtime.rgb;
                        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token); timeout.CancelAfter(TimeSpan.FromSeconds(2));
                        await client.ConnectAsync(cancellationToken: timeout.Token);
                        directModes.Clear();
                        var (discovered, indices) = Identify(client);
                        foreach (var d in discovered)
                        {
                            if (!value.ZoneSizes.TryGetValue(d.Id, out var sizes)) continue;
                            foreach (var size in sizes)
                            {
                                if (d.Zones is null || size.Key >= d.Zones.Count) continue;
                                var z = d.Zones[size.Key];
                                // Only resize when OpenRGB forgot the size: every resize re-initialises the controller.
                                if (size.Value < z.MinLeds || size.Value > z.MaxLeds || z.LedCount == size.Value) continue;
                                await client.ResizeZoneAsync(indices[d.Id], size.Key, size.Value, timeout.Token);
                                await client.RefreshAsync(timeout.Token);
                            }
                        }
                        discovered = discovered.Select(d => LedLayers.Project(d.Id, client.Controllers[(int)indices[d.Id]])).ToArray();
                        foreach (var d in discovered.Where(d => value.SyncedDevices.GetValueOrDefault(d.Id, value.Discovery.AutoJoinNewRgb))) { await client.SetDirectModeAsync(indices[d.Id], timeout.Token); directModes.Add(d.Id); }
                        lock (runtime.stateGate) { runtime.devices = discovered; runtime.rgbIndices = indices; }
                        pacer.Reset();
                        runtime.rgbConnected = true;
                    }
                    if (!runtime.rgbConnected) continue;
                    // OpenRGB announces "device list updated" for many reasons. Re-read the list in place instead of
                    // dropping the connection: a full reconnect re-initialises every controller, which blanked a RAM
                    // stick and flashed the board's ROG logo every 5 s in the live test (2026-10-29).
                    if (!runtime.mock && runtime.rgb is OpenRgbClient live && live.DeviceListInvalidated)
                    {
                        if (runtime.clock.Elapsed < nextRefresh) continue;
                        nextRefresh = runtime.clock.Elapsed + TimeSpan.FromSeconds(1);
                        using var refreshTimeout = CancellationTokenSource.CreateLinkedTokenSource(token); refreshTimeout.CancelAfter(TimeSpan.FromSeconds(2));
                        await live.RefreshAsync(refreshTimeout.Token);
                        var (found, idx) = Identify(live);
                        RgbDeviceState[] before; lock (runtime.stateGate) before = runtime.devices;
                        foreach (var added in found.Where(f => before.All(b => b.Id != f.Id) && value.SyncedDevices.GetValueOrDefault(f.Id, value.Discovery.AutoJoinNewRgb)))
                        { await live.SetDirectModeAsync(idx[added.Id], refreshTimeout.Token); directModes.Add(added.Id); }
                        lock (runtime.stateGate) { runtime.devices = found; runtime.rgbIndices = idx; }
                        pacer.Reset();
                        continue;
                    }
                    RgbDeviceState[] current; lock (runtime.stateGate) current = runtime.devices;
                    foreach (var device in current.Where(d => (runtime.failsafe && value.Events.FanFailsafe) || value.Bindings.Keys.Any(k => k == d.Id || k.StartsWith(d.Id + "/zone/", StringComparison.Ordinal)) || value.SyncedDevices.GetValueOrDefault(d.Id, value.Discovery.AutoJoinNewRgb) && value.Lighting.Parameters?.Devices?.GetValueOrDefault(d.Id, true) != false))
                    {
                        if (!runtime.mock && !directModes.Contains(device.Id))
                        { using var directTimeout = CancellationTokenSource.CreateLinkedTokenSource(token); directTimeout.CancelAfter(TimeSpan.FromSeconds(2)); await ((OpenRgbClient)runtime.rgb).SetDirectModeAsync(runtime.rgbIndices[device.Id], directTimeout.Token); directModes.Add(device.Id); }
                        var now = runtime.clock.Elapsed;
                        var colors = runtime.RenderDevice(value, device, current, now.TotalSeconds);
                        if (!pacer.ShouldSend(device.Id, device.Kind, colors, now)) continue;
                        // GPU and RAM lighting share a bus with sensor reads: wait for our turn. Retried next tick.
                        if (!runtime.mock && BusTurns.ForDeviceKind(device.Kind) is { } bus && !bus.TryBeginWrite()) continue;
                        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token); timeout.CancelAfter(TimeSpan.FromSeconds(2));
                        await runtime.rgb.SetColorsAsync(runtime.rgbIndices[device.Id], colors, timeout.Token);
                        pacer.Sent(device.Id, colors, now);
                    }
                    runtime.warnings.TryRemove("rgb", out _);
                }
                catch (InvalidOperationException) when (!token.IsCancellationRequested && runtime.rgb is OpenRgbClient { DeviceListInvalidated: true })
                {
                    // The device list changed between our check and the write: refresh on the next tick, keep the connection.
                }
                catch (Exception e) when (!token.IsCancellationRequested && e is IOException or SocketException or OperationCanceledException or InvalidOperationException or ArgumentException or System.ComponentModel.Win32Exception)
                {
                    runtime.rgbConnected = false; reconnect = runtime.clock.Elapsed + TimeSpan.FromSeconds(5);
                    runtime.warnings["rgb"] = "OpenRGB connection lost; reconnecting in 5 s.";
                    if (e is SocketException { SocketErrorCode: SocketError.ConnectionRefused } || e is OperationCanceledException)
                    {
                        if (runtime.clock.Elapsed >= nextOfflineLog) { PrismLog.Failure("rgb", e); nextOfflineLog = runtime.clock.Elapsed + TimeSpan.FromMinutes(1); }
                    }
                    else { PrismLog.Failure("rgb", e); }
                    lock (runtime.stateGate) runtime.devices = runtime.devices.Select(d => d with { Connected = false }).ToArray();
                    await runtime.rgb.DisposeAsync(); runtime.rgb = runtime.mock ? new FakeRgbBackend() : new OpenRgbClient(new TcpConnectionFactory());
                    runtime.rgbConnected = runtime.mock;
                }
                finally { runtime.rgbGate.Release(); }
            } while (await timer.WaitForNextTickAsync(token));
        }

    }
}
