using System.Net.Sockets;
using Prism.Core;
using Prism.Core.Ipc;
using Prism.Hardware;
using Prism.Lighting;
using Prism.Router;

namespace Prism.Service;

public sealed partial class ServiceRuntime
{
    private sealed class LampModule(ServiceRuntime runtime) : HostedModule("lamps", ok => runtime.ModuleHealth("lamps", ok))
    {
        private sealed class Worker(string name, Func<CancellationToken, Task> run, Action<bool> health) : HostedModule(name, health)
        {
            protected override Task ExecuteAsync(CancellationToken token) => run(token);
        }
        protected override Task ExecuteAsync(CancellationToken token) => LampAsync(token);
        private async Task LampAsync(CancellationToken token)
        {
            var workers = new Dictionary<string, (CancellationTokenSource Stop, Task Task)>();
            using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(500));
            try
            {
                do
                {
                    var configs = Volatile.Read(ref runtime.settings).ConfiguredLamps;
                    foreach (var id in workers.Keys.Where(id => configs.All(c => c.Id != id)).ToArray())
                    {
                        var worker = workers[id]; worker.Stop.Cancel();
                        try { await worker.Task; } catch (OperationCanceledException) { }
                        worker.Stop.Dispose(); workers.Remove(id);
                        lock (runtime.stateGate) runtime.lampStates.Remove(id);
                        runtime.warnings.TryRemove(id, out _);
                    }
                    foreach (var config in configs.Where(c => !workers.ContainsKey(c.Id)))
                    {
                        var source = CancellationTokenSource.CreateLinkedTokenSource(token);
                        var initial = workers.Count == 0 ? runtime.lamp : null; runtime.lamp = null;
                        var worker = new Worker("lamps", token =>
                        {
                            var first = initial; initial = null;
                            return LampWorkerAsync(config.Id, first, token);
                        }, ok => runtime.ModuleHealth("lamp:" + config.Id, ok));
                        workers[config.Id] = (source, worker.RunAsync(source.Token));
                    }
                } while (await timer.WaitForNextTickAsync(token));
            }
            finally
            {
                foreach (var worker in workers.Values) worker.Stop.Cancel();
                foreach (var worker in workers.Values) { try { await worker.Task; } catch (OperationCanceledException) { } finally { worker.Stop.Dispose(); } }
            }
        }

        private async Task LampWorkerAsync(string lampId, ILampClient? initial, CancellationToken token)
        {
            if (runtime.Observe) await Task.Delay(Timeout.Infinite, token);
            ILampClient? lamp = initial;
            int credentials = Volatile.Read(ref runtime.lampCredentials), revision = runtime.lampRevisions.GetValueOrDefault(lampId); string? connectedHost = null;
            SensorBinding? appliedSensorBinding = null;
            bool sensorDriving = false;
            LampConfiguration? applied = null; string transport = runtime.mock ? "mock" : "not configured";
            LampSync? sync = lamp is null ? null : new(lamp, runtime.clock);
            using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(500));
            var retry = TimeSpan.Zero;
            var nextPoll = TimeSpan.Zero;
            try
            {
                do
                {
                    var value = Volatile.Read(ref runtime.settings); var config = value.ConfiguredLamps.FirstOrDefault(c => c.Id == lampId);
                    if (config is null) return;
                    try
                    {
                        if (credentials != Volatile.Read(ref runtime.lampCredentials) || revision != runtime.lampRevisions.GetValueOrDefault(lampId) || (applied is not null && config.DeviceId != applied.DeviceId))
                        {
                            if (lamp is not null) await lamp.DisposeAsync();
                            lamp = null; applied = null; retry = TimeSpan.Zero;
                            credentials = Volatile.Read(ref runtime.lampCredentials); revision = runtime.lampRevisions.GetValueOrDefault(lampId);
                        }
                        if (lamp is null && runtime.clock.Elapsed >= retry)
                        {
                            if (runtime.mock) { lamp = new FakeLampClient(); transport = "mock"; sync = new(lamp, runtime.clock); }
                            else if (config.Transport == "yeelight" && config.Host is { } lanHost)
                            {
                                var client = new YeelightClient(new TcpConnectionFactory(), runtime.clock, new TcpMusicListener());
                                try { await client.ConnectAsync(lanHost, 55443, token); lamp = client; }
                                catch { await client.DisposeAsync(); throw; }
                                transport = "yeelight"; connectedHost = lanHost; sync = new(lamp, runtime.clock);
                            }
                            else
                            {
                                var found = await runtime.DiscoverLampsAsync(token);
                                var resolved = config.DeviceId is { } did ? found.FirstOrDefault(d => d.DeviceId == did) : found.FirstOrDefault(d => d.Ip == config.Host);
                                if (resolved is null) throw new IOException("Lamp not discovered.");
                                if (runtime.lampResolutionHandler is not null && (config.DeviceId is null || config.LastHost != resolved.Ip))
                                {
                                    await runtime.lampResolutionHandler(config.Id, resolved);
                                    if (config.DeviceId is null) return;
                                }
                                var store = new LampTokenStore();
                                var secret = config.DeviceId is { } key ? store.Load(key) : store.Load();
                                if (secret is null) throw new IOException("Lamp token missing.");
                                try { lamp = new MiioLampClient(new UdpMiioTransport(resolved.Ip), secret, runtime.clock, resolved.DeviceId); }
                                finally { System.Security.Cryptography.CryptographicOperations.ZeroMemory(secret); }
                                connectedHost = resolved.Ip; transport = "miio";
                                if (lamp is MiioLampClient identified)
                                {
                                    var info = await identified.CallAsync("miIO.info", Array.Empty<object>(), token);
                                    string model = info.TryGetProperty("model", out var m) ? m.GetString() ?? "" : "";
                                    if (!model.StartsWith("yeelink.light.", StringComparison.Ordinal))
                                    {
                                        if (runtime.observationsHandler is not null)
                                        {
                                            var now = DateTimeOffset.UtcNow; var spec = MiotSpec.Load(model);
                                            await runtime.observationsHandler([new(config.Id, model.Contains(".fryer.", StringComparison.Ordinal) ? DeviceKind.Appliance : DeviceKind.NetworkDevice,
                                                model.Length > 0 ? model : config.Name, "miio", now, now, spec is null ? DeviceStatus.Unsupported : DeviceStatus.Ready, [],
                                                Model: model, DeviceId: resolved.DeviceId, Ip: resolved.Ip, Configured: true)], []);
                                        }
                                        throw new IOException("Discovered device is not a supported lamp.");
                                    }
                                }
                                sync = new(lamp, runtime.clock);
                            }
                        }
                        if (lamp is null) continue;
                        if (runtime.mock) connectedHost = config.Host;
                        var currentSensorBinding = value.Bindings.GetValueOrDefault(lampId);
                        bool driveSensor = currentSensorBinding is not null && SensorSources.Read(runtime.GetState(value), currentSensorBinding.Source).HasValue;
                        if (config != applied || appliedSensorBinding != currentSensorBinding || sensorDriving != driveSensor)
                        {
                            await lamp.SetPowerAsync(config.On, token);
                            if (lamp is MiioLampClient m)
                            { await m.SetBrightnessAsync(config.Brightness, token); if (config.Kelvin is { } k) await m.SetColorTemperatureAsync(k, token); }
                            if (lamp is YeelightClient y)
                            { await y.SetBrightnessAsync(config.Brightness, token); if (config.Kelvin is { } k) await y.SetColorTemperatureAsync(k, token); }
                            if (config.Follow == "own" && config.Kelvin is null) await lamp.SetColorAsync(LightingConfiguration.ParseColor(config.Color), token);
                            applied = config; appliedSensorBinding = currentSensorBinding; sensorDriving = driveSensor;
                        }
                        if (config.On && (config.Follow != "own" || driveSensor))
                        {
                            RgbDeviceState[] current; lock (runtime.stateGate) current = runtime.devices;
                            double time = runtime.clock.Elapsed.TotalSeconds;
                            IReadOnlyList<RgbColor> colors;
                            if (config.Follow == "own") colors = [LightingConfiguration.ParseColor(config.Color)];
                            else if (config.Follow is "pc" or "map")
                            {
                                MapPoint[] point = [value.Map.Lamps?.GetValueOrDefault(lampId) ?? config.MapPoint ?? CaseMap.LampPoint(value.Map, current, lampId, value.ConfiguredLamps.ToList().FindIndex(l => l.Id == lampId))];
                                var rendered = SpatialEffects.Render(value.Lighting.ToScene(), value.Lighting.GetParameters(), time, point, kind: "strip", input: runtime.FrameInputs());
                                colors = runtime.eventEffects.Composite(rendered, point, time, value.Events);
                            }
                            else colors = current.Where(d => value.SyncedDevices.GetValueOrDefault(d.Id, value.Discovery.AutoJoinNewRgb) && value.Lighting.Parameters?.Devices?.GetValueOrDefault(d.Id, true) != false)
                                .SelectMany(d => runtime.RenderDevice(value, d, current, time)).ToArray();
                            if (value.Bindings.ContainsKey(lampId))
                            {
                                var sensorDevice = new RgbDeviceState(lampId, config.Name, "Lamp", true, true, 1);
                                var sensorState = runtime.GetState(value);
                                IReadOnlyList<RgbColor> normal = [colors.Count > 0 ? new RgbColor((byte)colors.Average(c => c.R), (byte)colors.Average(c => c.G), (byte)colors.Average(c => c.B)) : RgbColor.Black];
                                colors = runtime.sensorLighting.Composite(sensorDevice, value.Bindings, sensorState, time, normal, value.Lighting.Brightness);
                                colors = runtime.eventEffects.Composite(colors, [new(0.5, 0.5)], time, value.Events, SensorLighting.AlertLeds(sensorDevice, value.Bindings, sensorState));
                            }
                            await sync!.PushAsync(colors, LampColorSelection.Average, cancellationToken: token);
                        }
                        if (lamp is MiioLampClient poll && runtime.clock.Elapsed >= nextPoll)
                        {
                            await poll.GetPropertiesAsync(token); nextPoll = runtime.clock.Elapsed + TimeSpan.FromSeconds(30);
                        }
                        runtime.warnings.TryRemove(lampId, out _);
                        lock (runtime.stateGate) runtime.lampStates[lampId] = new("yeelink.light.color5", true, config.On, config.Brightness, config.Follow != "own" && sync?.LastColor is { } live ? $"#{live.R:X2}{live.G:X2}{live.B:X2}" : config.Color, config.Follow, transport, config.Id, config.Name, config.DeviceId, connectedHost);
                    }
                    catch (Exception e) when (!token.IsCancellationRequested && e is IOException or SocketException or OperationCanceledException or InvalidOperationException or System.Security.Cryptography.CryptographicException or System.Text.Json.JsonException or KeyNotFoundException or UnauthorizedAccessException)
                    {
                        runtime.warnings[lampId] = $"{config.Name}: lamp unavailable; check transport and token configuration.";
                        lock (runtime.stateGate) runtime.lampStates[lampId] = new("yeelink.light.color5", false, config.On, config.Brightness, config.Color, config.Follow, transport, config.Id, config.Name, config.DeviceId, connectedHost, "Lamp unavailable; check token and discovery.");
                        if (lamp is not null) await lamp.DisposeAsync(); lamp = null; applied = null;
                        retry = runtime.clock.Elapsed + TimeSpan.FromSeconds(30);
                    }
                } while (await timer.WaitForNextTickAsync(token));
            }
            finally { if (lamp is not null) await lamp.DisposeAsync(); }
        }

    }
}
