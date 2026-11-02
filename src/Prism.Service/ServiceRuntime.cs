using System.Collections.Concurrent;
using System.Net.Sockets;
using Prism.Core;
using Prism.Core.Ipc;
using Prism.Hardware;
using Prism.Lighting;
using Prism.Router;

namespace Prism.Service;

/// <summary>Owns adapters; all I/O starts inside RunAsync, never in the composition constructor.</summary>
public sealed partial class ServiceRuntime : IServiceRuntime, IAsyncDisposable
{
    private readonly bool mock;
    private readonly IClock clock = new SystemClock();
    private readonly ISensorSource sensors;
    private readonly IFanControlPort controls;
    private readonly FanController fans;
    private readonly IHealthProbe healthProbe;
    private readonly ConcurrentDictionary<string, string> warnings = new();
    private readonly FakeInputs inputs = new();
    private readonly SensorLighting sensorLighting = new();
    private readonly EventEffects eventEffects = new();
    private readonly IAudioLevels? audioLevels;
    private readonly IScreenZones? screenZones;
    public void TriggerLightingEvent(LightingEventKind kind) => eventEffects.Trigger(kind, clock.Elapsed.TotalSeconds);
    private bool pingHigh, signalPoor;
    private ReactiveFrame reactive = new();
    private readonly ReactiveSmoother smoothing = new();
    private readonly OpenRgbProcess rgbProcess = new();
    private readonly object stateGate = new();
    private HardwareSnapshot snapshot = new(DateTimeOffset.UtcNow, []);
    private readonly Queue<TelemetrySample> sampleHistory = new();
    private readonly ProcessTelemetry processTelemetry = new();
    private IReadOnlyList<ProcessLoad>? processLoads;
    private bool? lastThermalThrottle;
    private int thermalEpisodes;
    private DateTimeOffset? thermalObservedSince;
    private ServiceSettings settings = new();
    private IRgbBackend rgb;
    private ILampClient? lamp;

    private readonly Dictionary<string, LampState> lampStates = new();
    private readonly MiioDiscovery miioDiscovery;
    private readonly bool useMockDiscovery;
    private readonly SemaphoreSlim discoveryGate = new(1, 1);
    private IReadOnlyList<MiioDevice> discoveredLamps = [];
    private TimeSpan nextDiscovery;
    private Func<string, MiioDevice, Task>? lampResolutionHandler;
    public void SetLampResolutionHandler(Func<string, MiioDevice, Task> handler) => lampResolutionHandler = handler;
    public async Task<IReadOnlyList<MiioDevice>> DiscoverLampsAsync(CancellationToken token)
    {
        if (useMockDiscovery) return [new("192.0.2.1", 470875761, 1), new("192.0.2.2", 470124183, 1)];
        await discoveryGate.WaitAsync(token);
        try
        {
            if (clock.Elapsed >= nextDiscovery)
            {
                nextDiscovery = clock.Elapsed + TimeSpan.FromSeconds(30);
                discoveredLamps = await miioDiscovery.DiscoverAsync(token, Volatile.Read(ref settings).ConfiguredLamps.Where(l => l.DeviceId is null && l.Host is not null).Select(l => l.Host!));
            }
            return discoveredLamps;
        }
        finally { discoveryGate.Release(); }
    }
    private RouterState? routerState;
    private RgbDeviceState[] devices = [];
    private Dictionary<string, uint> rgbIndices = new();
    private bool rgbConnected, elevated, failsafe;
    private readonly FanSafety safety = new();
    private int speedRunning;
    private Task<SpeedTestResult?>? speedTask;
    private IRouterBackend? router;
    private readonly CancellationTokenSource stop = new();
    private Task? running;
    private bool disposed;
    private readonly SemaphoreSlim rgbGate = new(1, 1);
    private int lampCredentials, routerCredentials;
    private readonly ConcurrentDictionary<string, int> lampRevisions = new();
    public void LampCredentialsChanged(string id) { lampRevisions.AddOrUpdate(id, 1, (_, revision) => revision + 1); nextDeviceDiscovery = TimeSpan.Zero; }
    public void CredentialsChanged(bool lamp) { if (lamp) Interlocked.Increment(ref lampCredentials); else Interlocked.Increment(ref routerCredentials); }

    public async Task<RgbDeviceState> ResizeZoneAsync(string device, int zone, int size, CancellationToken cancellationToken)
    {
        if (Observe) throw new InvalidOperationException("Observe mode cannot resize zones.");
        await rgbGate.WaitAsync(cancellationToken);
        try
        {
            RgbDeviceState current;
            lock (stateGate) current = devices.First(d => d.Id == device);
            if (!rgbConnected) throw new InvalidOperationException("OpenRGB is disconnected.");
            if (mock)
            {
                var zones = current.Zones!.ToArray(); int first = 0;
                zones[zone] = zones[zone] with { LedCount = size };
                for (int i = 0; i < zones.Length; i++) { zones[i] = zones[i] with { FirstLed = first }; first += zones[i].LedCount; }
                current = current with { LedCount = first, Zones = zones };
            }
            else
            {
                var client = (OpenRgbClient)rgb; var index = rgbIndices[device];
                await client.ResizeZoneAsync(index, zone, size, cancellationToken);
                await client.RefreshAsync(cancellationToken);
                await client.SetDirectModeAsync(index, cancellationToken);
                current = LedLayers.Project(device, client.Controllers[(int)index]);
            }
            lock (stateGate) devices = devices.Select(d => d.Id == device ? current : d).ToArray();
            return current;
        }
        finally { rgbGate.Release(); }
    }

    /// <summary>Observe mode: real sensors, but no fan, RGB or lamp writes (fans stay on the BIOS curves).</summary>
    public bool Observe { get; init; }

    public ServiceRuntime(bool mock, bool observe = false)
        : this(mock, HardwareComposition.Create(mock ? ["--mock"] : []), observe) { Observe = observe; }

    private ServiceRuntime(bool mock, (ISensorSource Sensors, IFanControlPort Controls, IServiceQuery Services) hardware, bool observe)
        : this(mock, hardware.Sensors, observe ? new ObservingFanControlPort(hardware.Controls) : hardware.Controls,
            mock ? new FakeHealthProbe() : new WindowsHealthProbe(),
            mock ? new FakeRgbBackend() : new OpenRgbClient(new TcpConnectionFactory()),
            mock ? new FakeLampClient() : null, mock ? new FakeRouterBackend() : null) { }

    public ServiceRuntime(bool mock, ISensorSource sensors, IFanControlPort controls, IHealthProbe healthProbe,
        IRgbBackend rgb, ILampClient? lamp, IRouterBackend? router, IAudioLevels? audioLevels = null, IScreenZones? screenZones = null, MiioDiscovery? miioDiscovery = null, IClock? clock = null,
        YeelightDiscovery? yeelightDiscovery = null, Func<DeviceState, CancellationToken, Task<IMiotClient>>? miotFactory = null)
    {
        this.mock = mock;
        this.miotFactory = miotFactory ?? (mock ? (_, _) => Task.FromException<IMiotClient>(new IOException("Mock appliance transport unavailable.")) : CreateMiotAsync);
        appliances = new(this.miotFactory, clock);
        this.yeelightDiscovery = yeelightDiscovery ?? new(new UdpDiscoveryTransport());
        useMockYeelight = mock && yeelightDiscovery is null; allowMetadata = !mock || miotFactory is not null;
        this.miioDiscovery = miioDiscovery ?? new(new UdpMiioDiscoveryTransport());
        useMockDiscovery = mock && miioDiscovery is null;
        if (clock is not null) this.clock = clock;
        this.audioLevels = audioLevels ?? (!mock && OperatingSystem.IsWindows() ? new WasapiLoopback() : null);
        this.screenZones = screenZones ?? (!mock && OperatingSystem.IsWindows() ? new DesktopDuplication() : null);
        this.sensors = sensors; this.controls = controls; fans = new(controls);
        this.healthProbe = healthProbe; this.rgb = rgb; this.lamp = lamp; this.router = router;
        if (mock)
        {
            rgbConnected = true;
            devices = [new("0", "Mock AURA", "Motherboard", true, true, 12), new("1", "Mock Vengeance", "RAM", true, true, 16)];
            rgbIndices = new() { ["0"] = 0, ["1"] = 1 };
        }
    }

    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, (int Zone, int Size, TimeSpan Until)> identification = new();
    public void EndIdentification(string device) => identification.TryRemove(device, out _);
    public void IdentifyZone(string device, int zone, int size) => identification[device] = (zone, size, clock.Elapsed + TimeSpan.FromSeconds(30));

    public Task ApplyAsync(ServiceSettings value, CancellationToken cancellationToken)
    { cancellationToken.ThrowIfCancellationRequested(); Volatile.Write(ref settings, value); return Task.CompletedTask; }

    public StateMessage GetState(ServiceSettings value)
    {
        lock (stateGate)
        {
            var synced = devices.Select(d => d with { Synced = value.SyncedDevices.GetValueOrDefault(d.Id, value.Discovery.AutoJoinNewRgb),
                CustomColors = value.LedLayers.TryGetValue(d.Id, out var layer) && layer.Custom is not null ? LedLayers.Resize(layer.Custom, d.LedCount, false) : null,
                OverlayColors = value.LedLayers.TryGetValue(d.Id, out var over) && over.Overlay is not null ? LedLayers.Resize(over.Overlay, d.LedCount, true) : null,
                CustomEnabled = value.LedLayers.GetValueOrDefault(d.Id)?.CustomEnabled == true }).ToArray();
            var health = new ServiceHealth(mock, elevated, warnings.Values.Order().ToArray(), failsafe,
                typeof(ServiceRuntime).Assembly.GetName().Version?.ToString());
            var lamps = value.ConfiguredLamps.Select(c => (lampStates.TryGetValue(c.Id, out var liveLamp) ? liveLamp with { On = c.On, Brightness = c.Brightness, Follow = c.Follow, Color = c.Color, Name = c.Name } : null) ?? new LampState("yeelink.light.color5", false, c.On, c.Brightness, c.Color, c.Follow, "not configured", c.Id, c.Name, c.DeviceId, c.LastHost ?? c.Host)).ToArray();
            var state = StateAssembler.Assemble(snapshot, value, controls.Headers, synced, lamps.FirstOrDefault(), routerState, health) with { Lamps = lamps, History = sampleHistory.ToArray() };
            state = state with { System = state.System with { TopProcesses = processLoads, UptimeSeconds = mock ? null : Environment.TickCount64 / 1000d,
                ThrottlingEvents = thermalObservedSince is null ? null : thermalEpisodes, ThrottlingObservedSince = thermalObservedSince } };
            var frame = FrameInputs();
            static string Hex(RgbColor? color) { var c = color ?? RgbColor.Black; return $"#{c.R:X2}{c.G:X2}{c.B:X2}"; }
            return DeviceNames.Apply(state with { Appliances = appliances.States, Lighting = state.Lighting with { IdentifyingZones = identification.Where(p => clock.Elapsed < p.Value.Until).Select(p => new ZoneIdentification(p.Key, p.Value.Zone, p.Value.Size, p.Value.Size == 0 ? 0 : (int)(clock.Elapsed.TotalSeconds * 6) % p.Value.Size + 1)).ToArray(), BindingReadings = sensorLighting.Readings(value.Bindings), Inputs = new(frame.Audio?.ToArray() ?? [], frame.Beat,
                Hex(frame.ScreenLeft), Hex(frame.ScreenCentre), Hex(frame.ScreenRight)) } }, value.Devices, value.Discovery);
        }
    }

    public Task RunAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (running is not null) throw new InvalidOperationException("Runtime already started.");
        return running = RunCoreAsync(cancellationToken);
    }

    private async Task RunCoreAsync(CancellationToken cancellationToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, stop.Token);
        EventHandler exit = (_, _) => RestoreDefaults();
        UnhandledExceptionEventHandler crash = (_, _) => RestoreDefaults();
        ConsoleCancelEventHandler cancel = (_, e) => { e.Cancel = true; RestoreDefaults(); linked.Cancel(); };
        AppDomain.CurrentDomain.ProcessExit += exit;
        AppDomain.CurrentDomain.UnhandledException += crash;
        Console.CancelKeyPress += cancel;
        var loops = new[] { SensorsAsync(linked.Token), LightingAsync(linked.Token), LampAsync(linked.Token), RouterAsync(linked.Token), HealthAsync(linked.Token), CaptureAsync(true, linked.Token), CaptureAsync(false, linked.Token), DiscoveryAsync(linked.Token) };
        try
        {
            var first = await Task.WhenAny(loops);
            await first;
        }
        catch (OperationCanceledException) when (linked.IsCancellationRequested) { }
        finally
        {
            linked.Cancel(); stop.Cancel(); RestoreDefaults();
            try
            {
                try { await Task.WhenAll(loops); } catch (OperationCanceledException) when (linked.IsCancellationRequested) { }
            }
            finally
            {
                AppDomain.CurrentDomain.ProcessExit -= exit;
                AppDomain.CurrentDomain.UnhandledException -= crash;
                Console.CancelKeyPress -= cancel;
            }
        }
    }

    private async Task SensorsAsync(CancellationToken token)
    {
        await using var monitor = new LhmHardwareMonitor(sensors);
        var previous = clock.Elapsed;
        void OnSnapshot(object? sender, HardwareSnapshot next)
        {
            var nextProcesses = mock ? null : processTelemetry.Sample();
            if (next.Success) warnings.TryRemove("sensors", out _); else warnings["sensors"] = "Sensor read failed.";
            var value = Volatile.Read(ref settings);
            foreach (var group in value.Fans)
            {
                int header = group.Key == "Radiator" ? 2 : 6;
                if (controls.Headers.Contains(header)) fans.Configure(header, group.Value.ToSettings());
            }
            foreach (var group in value.HeaderFans)
                if (controls.Headers.Contains(group.Key) && group.Key != 7) fans.Configure(group.Key, group.Value.ToSettings());
            fans.Apply(next, clock.Elapsed - previous); previous = clock.Elapsed;
            var required = next.Temperature(SensorCategory.Cpu).HasValue && value.Fans.Values.All(f => FanCurveEngine.SelectTemperature(next, f.Source).HasValue);
            lock (stateGate)
            {
                snapshot = next;
                processLoads = nextProcesses;
                bool nextFailsafe = safety.Observe(next, required);
                if (nextFailsafe) TriggerLightingEvent(LightingEventKind.FanFailsafe);
                failsafe = nextFailsafe;
                inputs.Temperature = next.Temperature(SensorCategory.Cpu) ?? double.NaN;
                var system = StateAssembler.Assemble(next, value, controls.Headers, [], null, null, new(mock, false, [], failsafe)).System;
                if (next.Success && system.ThermalThrottling is { } throttled)
                {
                    thermalObservedSince ??= next.Timestamp;
                    if (throttled && lastThermalThrottle != true) thermalEpisodes++;
                    lastThermalThrottle = throttled;
                }
                if (next.Success && (sampleHistory.Count == 0 || sampleHistory.Last().Timestamp < next.Timestamp))
                {
                    sampleHistory.Enqueue(new(next.Timestamp, system.CpuLoad, system.CpuTemp, system.GpuLoad,
                        system.GpuTemp, routerState?.PingMs, system.NetDownMbps, system.NetUpMbps,
                        FanMap.Discover(next.Sensors, controls.Headers).ToDictionary(f => f.Header,
                            f => next.Sensors.FirstOrDefault(s => s.Category == SensorCategory.Motherboard && s.Kind == "Fan" && s.Id.EndsWith($"/fan/{f.Header - 1}"))?.Value),
                        system.Disks.FirstOrDefault()?.ReadMBps, routerState?.Rsrp5g, routerState?.Sinr5g));
                    while (sampleHistory.Count > 3600) sampleHistory.Dequeue();
                }
                Volatile.Write(ref reactive, new(system.CpuLoad ?? 0, system.GpuLoad ?? 0,
                    system.RamTotalGb > 0 ? (system.RamUsedGb ?? 0) / system.RamTotalGb.Value : 0,
                    system.CpuTemp ?? double.NaN, system.GpuTemp ?? double.NaN));
            }
        }
        monitor.Snapshot += OnSnapshot;
        try
        {
            await monitor.RunAsync(token);
        }
        finally { monitor.Snapshot -= OnSnapshot; RestoreDefaults(); }
    }

    private async Task LightingAsync(CancellationToken token)
    {
        if (Observe) { warnings["observe"] = "Observe mode: fans, RGB and lamp are not controlled."; await Task.Delay(Timeout.Infinite, token); }
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
            var value = Volatile.Read(ref settings);
            await rgbGate.WaitAsync(token);
            try
            {
                if (!mock && !rgbConnected && clock.Elapsed >= reconnect)
                {
                    if (value.LaunchOpenRgb && value.OpenRgbPath is not null) rgbProcess.Start(value.OpenRgbPath);
                    var client = (OpenRgbClient)rgb;
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
                    lock (stateGate) { devices = discovered; rgbIndices = indices; }
                    pacer.Reset();
                    rgbConnected = true;
                }
                if (!rgbConnected) continue;
                // OpenRGB announces "device list updated" for many reasons. Re-read the list in place instead of
                // dropping the connection: a full reconnect re-initialises every controller, which blanked a RAM
                // stick and flashed the board's ROG logo every 5 s in the live test (2026-10-29).
                if (!mock && rgb is OpenRgbClient live && live.DeviceListInvalidated)
                {
                    if (clock.Elapsed < nextRefresh) continue;
                    nextRefresh = clock.Elapsed + TimeSpan.FromSeconds(1);
                    using var refreshTimeout = CancellationTokenSource.CreateLinkedTokenSource(token); refreshTimeout.CancelAfter(TimeSpan.FromSeconds(2));
                    await live.RefreshAsync(refreshTimeout.Token);
                    var (found, idx) = Identify(live);
                    RgbDeviceState[] before; lock (stateGate) before = devices;
                    foreach (var added in found.Where(f => before.All(b => b.Id != f.Id) && value.SyncedDevices.GetValueOrDefault(f.Id, value.Discovery.AutoJoinNewRgb)))
                    { await live.SetDirectModeAsync(idx[added.Id], refreshTimeout.Token); directModes.Add(added.Id); }
                    lock (stateGate) { devices = found; rgbIndices = idx; }
                    pacer.Reset();
                    continue;
                }
                RgbDeviceState[] current; lock (stateGate) current = devices;
                foreach (var device in current.Where(d => identification.TryGetValue(d.Id, out var identifying) && clock.Elapsed < identifying.Until || (failsafe && value.Events.FanFailsafe) || value.Bindings.Keys.Any(k => k == d.Id || k.StartsWith(d.Id + "/zone/", StringComparison.Ordinal)) || value.SyncedDevices.GetValueOrDefault(d.Id, value.Discovery.AutoJoinNewRgb) && value.Lighting.Parameters?.Devices?.GetValueOrDefault(d.Id, true) != false))
                {
                    if (!mock && !directModes.Contains(device.Id))
                    { using var directTimeout = CancellationTokenSource.CreateLinkedTokenSource(token); directTimeout.CancelAfter(TimeSpan.FromSeconds(2)); await ((OpenRgbClient)rgb).SetDirectModeAsync(rgbIndices[device.Id], directTimeout.Token); directModes.Add(device.Id); }
                    var now = clock.Elapsed;
                    var colors = RenderDevice(value, device, current, now.TotalSeconds);
                    if (!pacer.ShouldSend(device.Id, device.Kind, colors, now)) continue;
                    // GPU and RAM lighting share a bus with sensor reads: wait for our turn. Retried next tick.
                    if (!mock && BusTurns.ForDeviceKind(device.Kind) is { } bus && !bus.TryBeginWrite()) continue;
                    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token); timeout.CancelAfter(TimeSpan.FromSeconds(2));
                    await rgb.SetColorsAsync(rgbIndices[device.Id], colors, timeout.Token);
                    pacer.Sent(device.Id, colors, now);
                }
                warnings.TryRemove("rgb", out _);
            }
            catch (InvalidOperationException) when (!token.IsCancellationRequested && rgb is OpenRgbClient { DeviceListInvalidated: true })
            {
                // The device list changed between our check and the write: refresh on the next tick, keep the connection.
            }
            catch (Exception e) when (!token.IsCancellationRequested && e is IOException or SocketException or OperationCanceledException or InvalidOperationException or ArgumentException or System.ComponentModel.Win32Exception)
            {
                rgbConnected = false; reconnect = clock.Elapsed + TimeSpan.FromSeconds(5);
                warnings["rgb"] = $"OpenRGB connection lost ({e.GetType().Name}: {e.Message}); reconnecting in 5 s.";
                if (e is SocketException { SocketErrorCode: SocketError.ConnectionRefused } || e is OperationCanceledException)
                {
                    if (clock.Elapsed >= nextOfflineLog) { Console.Error.WriteLine("[rgb] OpenRGB not reachable"); nextOfflineLog = clock.Elapsed + TimeSpan.FromMinutes(1); }
                }
                else { System.Diagnostics.Trace.TraceWarning("RGB reconnect: " + e); Console.Error.WriteLine($"[rgb] reconnect after {e.GetType().Name}: {e.Message}"); }
                lock (stateGate) devices = devices.Select(d => d with { Connected = false }).ToArray();
                await rgb.DisposeAsync(); rgb = mock ? new FakeRgbBackend() : new OpenRgbClient(new TcpConnectionFactory());
                rgbConnected = mock;
            }
            finally { rgbGate.Release(); }
        } while (await timer.WaitForNextTickAsync(token));
    }

    private IReadOnlyList<RgbColor> RenderDevice(ServiceSettings value, RgbDeviceState device, IReadOnlyList<RgbDeviceState> current, double time)
    {
        if (identification.TryGetValue(device.Id, out var identify) && clock.Elapsed < identify.Until && device.Zones is { } zones && identify.Zone < zones.Count)
        {
            var zone = zones[identify.Zone];
            var preview = Enumerable.Repeat(RgbColor.Black, device.LedCount).ToArray();
            int index = zone.LedCount == 0 ? -1 : (int)(time * 6) % zone.LedCount;
            for (int i = 0; i < zone.LedCount; i++) preview[zone.FirstLed + i] = i == index ? new RgbColor(255, 255, 255) : new RgbColor(24, 24, 24);
            return preview;
        }
        var layer = value.LedLayers.GetValueOrDefault(device.Id);
        var positions = CaseMap.Positions(device, CaseMap.Placement(value.Map, device, current), value.Map);
        var rendered = SpatialEffects.Render(value.Lighting.ToScene(), value.Lighting.GetParameters(), time, positions, device.Id, device.Kind, FrameInputs());
        var colors = LedLayers.Composite(rendered, layer?.Custom, layer?.Overlay, layer?.CustomEnabled == true || value.Lighting.Effect == LightingEffect.Custom);
        var sensorState = GetState(value);
        colors = sensorLighting.Composite(device, value.Bindings, sensorState, time, colors, value.Lighting.Brightness);
        return eventEffects.Composite(colors, positions, time, value.Events, SensorLighting.AlertLeds(device, value.Bindings, sensorState));
    }

    private async Task LampAsync(CancellationToken token)
    {
        var workers = new Dictionary<string, (CancellationTokenSource Stop, Task Task)>();
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(500));
        try
        {
            do
            {
                var configs = Volatile.Read(ref settings).ConfiguredLamps;
                foreach (var id in workers.Keys.Where(id => configs.All(c => c.Id != id)).ToArray())
                {
                    var worker = workers[id]; worker.Stop.Cancel();
                    try { await worker.Task; } catch (OperationCanceledException) { }
                    worker.Stop.Dispose(); workers.Remove(id);
                    lock (stateGate) lampStates.Remove(id);
                    warnings.TryRemove(id, out _);
                }
                foreach (var config in configs.Where(c => !workers.ContainsKey(c.Id)))
                {
                    var source = CancellationTokenSource.CreateLinkedTokenSource(token);
                    var initial = workers.Count == 0 ? lamp : null; lamp = null;
                    workers[config.Id] = (source, LampWorkerAsync(config.Id, initial, source.Token));
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
        if (Observe) await Task.Delay(Timeout.Infinite, token);
        ILampClient? lamp = initial;
        int credentials = Volatile.Read(ref lampCredentials), revision = lampRevisions.GetValueOrDefault(lampId); string? connectedHost = null;
        SensorBinding? appliedSensorBinding = null;
        bool sensorDriving = false;
        LampConfiguration? applied = null; string transport = mock ? "mock" : "not configured";
        LampSync? sync = lamp is null ? null : new(lamp, clock);
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(50));
        var nextConfigWrite = TimeSpan.Zero;
        var retry = TimeSpan.Zero;
        var nextPoll = TimeSpan.Zero;
        try
        {
            do
            {
                var value = Volatile.Read(ref settings); var config = value.ConfiguredLamps.FirstOrDefault(c => c.Id == lampId);
                if (config is null) return;
                try
                {
                    if (credentials != Volatile.Read(ref lampCredentials) || revision != lampRevisions.GetValueOrDefault(lampId) || (applied is not null && config.DeviceId != applied.DeviceId))
                    {
                        if (lamp is not null) await lamp.DisposeAsync();
                        lamp = null; applied = null; retry = TimeSpan.Zero;
                        credentials = Volatile.Read(ref lampCredentials); revision = lampRevisions.GetValueOrDefault(lampId);
                    }
                    if (lamp is null && clock.Elapsed >= retry)
                    {
                        if (mock) { lamp = new FakeLampClient(); transport = "mock"; sync = new(lamp, clock); }
                        else if (config.Transport == "yeelight" && config.Host is { } lanHost)
                        {
                            var client = new YeelightClient(new TcpConnectionFactory(), clock, new TcpMusicListener());
                            try { await client.ConnectAsync(lanHost, 55443, token); lamp = client; }
                            catch { await client.DisposeAsync(); throw; }
                            transport = "yeelight"; connectedHost = lanHost; sync = new(lamp, clock);
                        }
                        else
                        {
                            var found = await DiscoverLampsAsync(token);
                            var resolved = config.DeviceId is { } did ? found.FirstOrDefault(d => d.DeviceId == did) : found.FirstOrDefault(d => d.Ip == config.Host);
                            if (resolved is null) throw new IOException("Lamp not discovered.");
                            if (lampResolutionHandler is not null && (config.DeviceId is null || config.LastHost != resolved.Ip))
                            {
                                await lampResolutionHandler(config.Id, resolved);
                                if (config.DeviceId is null) return;
                            }
                            var store = new LampTokenStore();
                            var secret = config.DeviceId is { } key ? store.Load(key) : store.Load();
                            if (secret is null) throw new IOException("Lamp token missing.");
                            try { lamp = new MiioLampClient(new UdpMiioTransport(resolved.Ip), secret, clock, resolved.DeviceId); }
                            finally { System.Security.Cryptography.CryptographicOperations.ZeroMemory(secret); }
                            connectedHost = resolved.Ip; transport = "miio";
                            if (lamp is MiioLampClient identified)
                            {
                                var info = await identified.CallAsync("miIO.info", Array.Empty<object>(), token);
                                string model = info.TryGetProperty("model", out var m) ? m.GetString() ?? "" : "";
                                if (!model.StartsWith("yeelink.light.", StringComparison.Ordinal))
                                {
                                    if (observationsHandler is not null)
                                    {
                                        var now = DateTimeOffset.UtcNow; var spec = MiotSpec.Load(model);
                                        await observationsHandler([new(config.Id, model.Contains(".fryer.", StringComparison.Ordinal) ? DeviceKind.Appliance : DeviceKind.NetworkDevice,
                                            model.Length > 0 ? model : config.Name, "miio", now, now, spec is null ? DeviceStatus.Unsupported : DeviceStatus.Ready, [],
                                            Model: model, DeviceId: resolved.DeviceId, Ip: resolved.Ip, Configured: true)], []);
                                    }
                                    throw new IOException("Discovered device is not a supported lamp.");
                                }
                            }
                            sync = new(lamp, clock);
                        }
                    }
                    if (lamp is null) continue;
                    if (mock) connectedHost = config.Host;
                    var currentSensorBinding = value.Bindings.GetValueOrDefault(lampId);
                    bool driveSensor = currentSensorBinding is not null && SensorSources.Read(GetState(value), currentSensorBinding.Source).HasValue;
                    if ((config != applied || appliedSensorBinding != currentSensorBinding || sensorDriving != driveSensor) && clock.Elapsed >= nextConfigWrite)
                    {
                        nextConfigWrite = clock.Elapsed + TimeSpan.FromMilliseconds(lamp is MiioLampClient ? 500 : 100);
                        if (applied?.On != config.On) await lamp.SetPowerAsync(config.On, token);
                        if (lamp is MiioLampClient m)
                        { if (applied?.Brightness != config.Brightness) await m.SetBrightnessAsync(config.Brightness, token); if (config.Kelvin is { } k && applied?.Kelvin != k) await m.SetColorTemperatureAsync(k, token); }
                        if (lamp is YeelightClient y)
                        { if (applied?.Brightness != config.Brightness) await y.SetBrightnessAsync(config.Brightness, token); if (config.Kelvin is { } k && applied?.Kelvin != k) await y.SetColorTemperatureAsync(k, token); }
                        if (config.Follow == "own" && config.Kelvin is null && (applied?.Color != config.Color || applied?.Follow != "own" || applied?.Kelvin is not null)) await lamp.SetColorAsync(LightingConfiguration.ParseColor(config.Color), token);
                        applied = config; appliedSensorBinding = currentSensorBinding; sensorDriving = driveSensor;
                    }
                    if (config.On && (config.Follow != "own" || driveSensor))
                    {
                        RgbDeviceState[] current; lock (stateGate) current = devices;
                        double time = clock.Elapsed.TotalSeconds;
                        IReadOnlyList<RgbColor> colors;
                        if (config.Follow == "own") colors = [LightingConfiguration.ParseColor(config.Color)];
                        else if (config.Follow is "pc" or "map")
                        {
                            MapPoint[] point = [value.Map.Lamps?.GetValueOrDefault(lampId) ?? config.MapPoint ?? CaseMap.LampPoint(value.Map, current, lampId, value.ConfiguredLamps.ToList().FindIndex(l => l.Id == lampId))];
                            var rendered = SpatialEffects.Render(value.Lighting.ToScene(), value.Lighting.GetParameters(), time, point, kind: "strip", input: FrameInputs());
                            colors = eventEffects.Composite(rendered, point, time, value.Events);
                        }
                        else colors = current.Where(d => value.SyncedDevices.GetValueOrDefault(d.Id, value.Discovery.AutoJoinNewRgb) && value.Lighting.Parameters?.Devices?.GetValueOrDefault(d.Id, true) != false)
                            .SelectMany(d => RenderDevice(value, d, current, time)).ToArray();
                        if (value.Bindings.ContainsKey(lampId))
                        {
                            var sensorDevice = new RgbDeviceState(lampId, config.Name, "Lamp", true, true, 1);
                            var sensorState = GetState(value);
                            IReadOnlyList<RgbColor> normal = [colors.Count > 0 ? new RgbColor((byte)colors.Average(c => c.R), (byte)colors.Average(c => c.G), (byte)colors.Average(c => c.B)) : RgbColor.Black];
                            colors = sensorLighting.Composite(sensorDevice, value.Bindings, sensorState, time, normal, value.Lighting.Brightness);
                            colors = eventEffects.Composite(colors, [new(0.5, 0.5)], time, value.Events, SensorLighting.AlertLeds(sensorDevice, value.Bindings, sensorState));
                        }
                        await sync!.PushAsync(colors, LampColorSelection.Average, cancellationToken: token);
                    }
                    if (lamp is MiioLampClient poll && clock.Elapsed >= nextPoll)
                    {
                        await poll.GetPropertiesAsync(token); nextPoll = clock.Elapsed + TimeSpan.FromSeconds(30);
                    }
                    warnings.TryRemove(lampId, out _);
                    lock (stateGate) lampStates[lampId] = new("yeelink.light.color5", true, config.On, config.Brightness, config.Follow != "own" && sync?.LastColor is { } live ? $"#{live.R:X2}{live.G:X2}{live.B:X2}" : config.Color, config.Follow, transport, config.Id, config.Name, config.DeviceId, connectedHost);
                }
                catch (Exception e) when (!token.IsCancellationRequested && e is IOException or SocketException or OperationCanceledException or InvalidOperationException or System.Security.Cryptography.CryptographicException or System.Text.Json.JsonException or KeyNotFoundException or UnauthorizedAccessException)
                {
                    // A lamp switched off at the wall is normal and only shows as offline; a token problem needs the user, so it stays a warning.
                    bool tokenProblem = e is System.Security.Cryptography.CryptographicException or UnauthorizedAccessException or KeyNotFoundException;
                    if (tokenProblem) warnings[lampId] = $"{config.Name}: the lamp rejected its token. Import it again on the Devices page.";
                    else warnings.TryRemove(lampId, out _);
                    lock (stateGate) lampStates[lampId] = new("yeelink.light.color5", false, config.On, config.Brightness, config.Color, config.Follow, transport, config.Id, config.Name, config.DeviceId, connectedHost,
                        tokenProblem ? "Token rejected" : "Switched off or out of Wi-Fi range");
                    if (lamp is not null) await lamp.DisposeAsync(); lamp = null; applied = null;
                    retry = clock.Elapsed + TimeSpan.FromSeconds(30);
                }
            } while (await timer.WaitForNextTickAsync(token));
        }
        finally { if (lamp is not null) await lamp.DisposeAsync(); }
    }

    private async Task RouterAsync(CancellationToken token)
    {
        var nextDeviceList = TimeSpan.Zero;
        string? host = null; int credentials = Volatile.Read(ref routerCredentials);
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(2));
        do
        {
            var config = Volatile.Read(ref settings);
            if (!mock && (!config.RouterEnabled || host != config.RouterHost || credentials != Volatile.Read(ref routerCredentials)))
            {
                router?.Dispose(); router = null; host = config.RouterHost;
                credentials = Volatile.Read(ref routerCredentials);
                lock (stateGate) routerState = null;
            }
            if (!mock && !config.RouterEnabled) continue;
            try
            {
                router ??= new RouterBackend(Volatile.Read(ref settings).RouterHost);
                var state = await Task.Run(router.Poll, token);
                lock (stateGate) routerState = state;
                if (clock.Elapsed >= nextDeviceList)
                {
                    nextDeviceList = clock.Elapsed + TimeSpan.FromSeconds(60);
                    var connected = await Task.Run(router.ConnectedDevices, token);
                    if (connected is not null && observationsHandler is not null)
                    {
                        var now = DateTimeOffset.UtcNow;
                        await observationsHandler(connected.Append(new DeviceState("router:" + config.RouterHost, DeviceKind.Router, "Router", "router", now, now, DeviceStatus.Ready, ["signal", "devices"], Ip: config.RouterHost, Configured: true)).ToArray(), ["router"]);
                    }
                }
                bool high = state.PingMs > 100, poor = state.Score < 40 || state.Bars < 2;
                if (high && !pingHigh) TriggerLightingEvent(LightingEventKind.PingSpike);
                if (poor && !signalPoor) TriggerLightingEvent(LightingEventKind.PoorSignal);
                pingHigh = high; signalPoor = poor;
                warnings.TryRemove("router", out _);
            }
            catch (Exception e) when (e is RouterError or IOException or InvalidOperationException or UnauthorizedAccessException)
            {
                var message = e is AuthError { AlreadyLoggedIn: true } ? "Router login held elsewhere." : "Router unavailable or credentials not configured.";
                warnings["router"] = message;
                lock (stateGate) routerState = new(false, null, null, null, null, null, null, null, null, null, null, null, null, null, null, message);
            }
        } while (await timer.WaitForNextTickAsync(token));
    }

    private async Task CaptureAsync(bool audio, CancellationToken token)
    {
        if (mock || Observe || !OperatingSystem.IsWindows()) { await Task.Delay(Timeout.Infinite, token); return; }
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(500));
        do
        {
            bool Needed() => Volatile.Read(ref settings).Lighting.Effect is var effect && (audio
                ? effect is LightingEffect.Music or LightingEffect.AudioSpectrum
                : effect is LightingEffect.Adaptive or LightingEffect.ScreenAmbient);
            if (!Needed() || (audio ? audioLevels is not WasapiLoopback : screenZones is not DesktopDuplication)) continue;
            using var captureStop = CancellationTokenSource.CreateLinkedTokenSource(token);
            var task = Task.Factory.StartNew(() => {
                if (audio) ((WasapiLoopback)audioLevels!).Run(captureStop.Token);
                else ((DesktopDuplication)screenZones!).Run(captureStop.Token);
            }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
            try
            {
                while (Needed() && !task.IsCompleted && !token.IsCancellationRequested) await Task.Delay(250, token);
            }
            finally
            {
                captureStop.Cancel();
                try { await task; warnings.TryRemove(audio ? "audio" : "screen", out _); }
                catch (Exception e) when (e is System.Runtime.InteropServices.COMException or NotSupportedException or InvalidOperationException)
                { warnings[audio ? "audio" : "screen"] = "Capture unavailable in this session; using silent/black input. Retrying."; }
            }
        } while (await timer.WaitForNextTickAsync(token));
    }

    private ReactiveFrame FrameInputs() => smoothing.Sample(Volatile.Read(ref reactive), clock.Elapsed.TotalSeconds) with {
        Audio = audioLevels?.Bands ?? new[] { inputs.Level }, Beat = audioLevels?.Beat ?? 0,
        ScreenLeft = screenZones?.Left ?? inputs.Average, ScreenCentre = screenZones?.Centre ?? inputs.Average,
        ScreenRight = screenZones?.Right ?? inputs.Average };

    private async Task HealthAsync(CancellationToken token)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(10));
        do
        {
            try
            {
                var health = healthProbe.Read(); lock (stateGate) elevated = health.Elevated;
                foreach (var key in warnings.Keys.Where(k => k.StartsWith("health:"))) warnings.TryRemove(key, out _);
                foreach (var warning in health.Warnings) warnings["health:" + warning] = warning;
            }
            catch (Exception) { warnings["health:query"] = "Cannot query service health."; }
        } while (await timer.WaitForNextTickAsync(token));
    }

    public Task<SpeedTestResult?> StartSpeedTestAsync(CancellationToken cancellationToken)
    {
        if (router is null) throw new InvalidOperationException("Router monitoring is not configured.");
        if (Interlocked.CompareExchange(ref speedRunning, 1, 0) != 0) throw new InvalidOperationException("Speed test already running.");
        // Owned by the service lifetime, not by an individual pipe client.
        speedTask = RunSpeedAsync(router, stop.Token);
        return speedTask.WaitAsync(cancellationToken);
    }
    private async Task<SpeedTestResult?> RunSpeedAsync(IRouterBackend backend, CancellationToken token)
    {
        try
        {
            var result = await backend.TestSpeedAsync(token);
            warnings.TryRemove("speed", out _);
            return result;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { return null; }
        catch (Exception e) when (e is SpeedTestError or IOException)
        { warnings["speed"] = "Speed test failed."; throw; }
        finally { Interlocked.Exchange(ref speedRunning, 0); }
    }
    public void RestoreDefaults() => fans.RestoreDefaults();
    public async ValueTask DisposeAsync()
    {
        if (disposed) return;
        disposed = true;
        stop.Cancel(); RestoreDefaults();
        if (running is not null)
        {
            try { await running; }
            catch (Exception e) { System.Diagnostics.Trace.TraceError(e.ToString()); }
        }
        if (speedTask is not null)
        {
            try { await speedTask; } catch (Exception e) { System.Diagnostics.Trace.TraceError(e.ToString()); }
        }
        fans.Dispose(); sensors.Dispose();
        await appliances.DisposeAsync();
        await rgb.DisposeAsync(); if (lamp is not null) await lamp.DisposeAsync();
        router?.Dispose(); rgbProcess.Dispose(); stop.Dispose();
    }
}
