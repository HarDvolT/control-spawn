using Prism.Core;
using Prism.Core.Ipc;
using Prism.Hardware;
namespace Prism.Service;
public sealed partial class ServiceRuntime
{
    private sealed class DiscoveryModule(ServiceRuntime runtime) : HostedModule("discovery", ok => runtime.ModuleHealth("discovery", ok))
    {
        protected override Task ExecuteAsync(CancellationToken token) => DiscoveryAsync(token);
        private async Task DiscoveryAsync(CancellationToken token)
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(5));
            var networkAt = TimeSpan.Zero;
            do
            {
                var result = new List<DeviceState>(); var completed = new List<string> { "openrgb", "lhm" }; var now = DateTimeOffset.UtcNow;
                try
                {
                    if (runtime.clock.Elapsed >= networkAt)
                    {
                        networkAt = runtime.clock.Elapsed + TimeSpan.FromSeconds(60);
                        result.AddRange(await runtime.ScanDevicesAsync(token, false)); completed.AddRange(["miio", "yeelight"]);
                    }
                    HardwareSnapshot snap; RgbDeviceState[] rgbDevices;
                    lock (runtime.stateGate) { snap = runtime.snapshot; rgbDevices = runtime.devices; }
                    result.AddRange(rgbDevices.Where(d => runtime.rgbConnected && d.Connected).Select(d => new DeviceState(d.Id, DeviceKind.RgbController, d.Name, "openrgb", now, now, DeviceStatus.Ready, ["sync", "case-map"])));
                    result.AddRange(FanMap.Discover(snap.Sensors, runtime.controls.Headers).Where(f => f.Rpm > 0 && double.IsFinite(f.Rpm)).Select(f => new DeviceState($"fan:{f.Header}", DeviceKind.FanHeader, f.Name, "lhm", now, now, DeviceStatus.Ready, f.IsPump ? ["rpm"] : ["rpm", "curve"])));
                    if (runtime.observationsHandler is not null) await runtime.observationsHandler(result, completed);
                    runtime.warnings.TryRemove("discovery", out _);
                }
                catch (Exception e) when (!token.IsCancellationRequested && e is IOException or System.Net.Sockets.SocketException or InvalidOperationException or OperationCanceledException)
                { runtime.warnings["discovery"] = "Device discovery temporarily unavailable."; }
            } while (await timer.WaitForNextTickAsync(token));
        }
    }
}
