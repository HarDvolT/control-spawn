using System.Net.Sockets;
using ControlSpawn.Core;
using ControlSpawn.Core.Ipc;
using ControlSpawn.Hardware;
using ControlSpawn.Lighting;
using ControlSpawn.Router;

namespace ControlSpawn.Service;

public sealed partial class ServiceRuntime
{
    private sealed class RouterModule(ServiceRuntime runtime) : HostedModule("router", ok => runtime.ModuleHealth("router", ok))
    {
        protected override Task ExecuteAsync(CancellationToken token) => RouterAsync(token);
        private async Task RouterAsync(CancellationToken token)
        {
            var nextDeviceList = TimeSpan.Zero;
            string? host = null; int credentials = Volatile.Read(ref runtime.routerCredentials);
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(2));
            do
            {
                var config = Volatile.Read(ref runtime.settings);
                if (!runtime.mock && (!config.RouterEnabled || host != config.RouterHost || credentials != Volatile.Read(ref runtime.routerCredentials)))
                {
                    runtime.router?.Dispose(); runtime.router = null; host = config.RouterHost;
                    credentials = Volatile.Read(ref runtime.routerCredentials);
                    lock (runtime.stateGate) runtime.routerState = null;
                }
                if (!runtime.mock && !config.RouterEnabled) continue;
                try
                {
                    runtime.router ??= new RouterBackend(Volatile.Read(ref runtime.settings).RouterHost);
                    var state = await Task.Run(runtime.router.Poll, token);
                    lock (runtime.stateGate) runtime.routerState = state;
                    if (runtime.clock.Elapsed >= nextDeviceList)
                    {
                        nextDeviceList = runtime.clock.Elapsed + TimeSpan.FromSeconds(60);
                        var connected = await Task.Run(runtime.router.ConnectedDevices, token);
                        if (connected is not null && runtime.observationsHandler is not null)
                        {
                            var now = DateTimeOffset.UtcNow;
                            await runtime.observationsHandler(connected.Append(new DeviceState("router:" + config.RouterHost, DeviceKind.Router, "Router", "router", now, now, DeviceStatus.Ready, ["signal", "devices"], Ip: config.RouterHost, Configured: true)).ToArray(), ["router"]);
                        }
                    }
                    bool high = state.PingMs > 100, poor = state.Score < 40 || state.Bars < 2;
                    if (high && !runtime.pingHigh) runtime.TriggerLightingEvent(LightingEventKind.PingSpike);
                    if (poor && !runtime.signalPoor) runtime.TriggerLightingEvent(LightingEventKind.PoorSignal);
                    runtime.pingHigh = high; runtime.signalPoor = poor;
                    runtime.warnings.TryRemove("router", out _);
                }
                catch (Exception e) when (e is RouterError or IOException or InvalidOperationException or UnauthorizedAccessException)
                {
                    var message = e is AuthError { AlreadyLoggedIn: true } ? "Router login held elsewhere." : "Router unavailable or credentials not configured.";
                    runtime.warnings["router"] = message;
                    lock (runtime.stateGate) runtime.routerState = new(false, null, null, null, null, null, null, null, null, null, null, null, null, null, null, message);
                }
            } while (await timer.WaitForNextTickAsync(token));
        }

    }
}
