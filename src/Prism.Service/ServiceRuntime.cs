using System.Collections.Concurrent;
using System.Net.Sockets;
using Prism.Core;
using Prism.Core.Ipc;
using Prism.Hardware;
using Prism.Lighting;
using Prism.Router;

namespace Prism.Service;

/// <summary>Owns adapters; all I/O starts inside RunAsync, never in the composition constructor.</summary>
public sealed class ServiceRuntime : IServiceRuntime, IAsyncDisposable
{
    private readonly bool mock;
    private readonly IClock clock = new SystemClock();
    private readonly ISensorSource sensors;
    private readonly IFanControlPort controls;
    private readonly FanController fans;
    private readonly IHealthProbe healthProbe;
    private readonly ConcurrentDictionary<string, string> warnings = new();
    private readonly FakeInputs inputs = new();
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
    private ServiceSettings settings = new();
    private IRgbBackend rgb;
    private ILampClient? lamp;
    private LampState? lampState;
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
        IRgbBackend rgb, ILampClient? lamp, IRouterBackend? router, IAudioLevels? audioLevels = null, IScreenZones? screenZones = null)
    {
        this.mock = mock;
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

    public Task ApplyAsync(ServiceSettings value, CancellationToken cancellationToken)
    { cancellationToken.ThrowIfCancellationRequested(); Volatile.Write(ref settings, value); return Task.CompletedTask; }

    public StateMessage GetState(ServiceSettings value)
    {
        lock (stateGate)
        {
            var synced = devices.Select(d => d with { Synced = value.SyncedDevices.GetValueOrDefault(d.Id, true),
                CustomColors = value.LedLayers.TryGetValue(d.Id, out var layer) && layer.Custom is not null ? LedLayers.Resize(layer.Custom, d.LedCount, false) : null,
                OverlayColors = value.LedLayers.TryGetValue(d.Id, out var over) && over.Overlay is not null ? LedLayers.Resize(over.Overlay, d.LedCount, true) : null,
                CustomEnabled = value.LedLayers.GetValueOrDefault(d.Id)?.CustomEnabled == true }).ToArray();
            var health = new ServiceHealth(mock, elevated, warnings.Values.Order().ToArray(), failsafe);
            var state = StateAssembler.Assemble(snapshot, value, controls.Headers, synced, lampState, routerState, health);
            var frame = FrameInputs();
            static string Hex(RgbColor? color) { var c = color ?? RgbColor.Black; return $"#{c.R:X2}{c.G:X2}{c.B:X2}"; }
            return state with { Lighting = state.Lighting with { Inputs = new(frame.Audio?.ToArray() ?? [], frame.Beat,
                Hex(frame.ScreenLeft), Hex(frame.ScreenCentre), Hex(frame.ScreenRight)) } };
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
        var loops = new[] { SensorsAsync(linked.Token), LightingAsync(linked.Token), LampAsync(linked.Token), RouterAsync(linked.Token), HealthAsync(linked.Token), CaptureAsync(true, linked.Token), CaptureAsync(false, linked.Token) };
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
            if (next.Success) warnings.TryRemove("sensors", out _); else warnings["sensors"] = "Sensor read failed.";
            var value = Volatile.Read(ref settings);
            foreach (var group in value.Fans)
            {
                int header = group.Key == "Radiator" ? 2 : 6;
                if (controls.Headers.Contains(header)) fans.Configure(header, group.Value.ToSettings());
            }
            fans.Apply(next, clock.Elapsed - previous); previous = clock.Elapsed;
            var required = next.Temperature(SensorCategory.Cpu).HasValue && value.Fans.Values.All(f => FanCurveEngine.SelectTemperature(next, f.Source).HasValue);
            lock (stateGate)
            {
                snapshot = next;
                bool nextFailsafe = safety.Observe(next, required);
                if (nextFailsafe) TriggerLightingEvent(LightingEventKind.FanFailsafe);
                failsafe = nextFailsafe;
                inputs.Temperature = next.Temperature(SensorCategory.Cpu) ?? double.NaN;
                var system = StateAssembler.Assemble(next, value, controls.Headers, [], null, null, new(mock, false, [], failsafe)).System;
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
        TimeSpan reconnect = TimeSpan.Zero, nextRefresh = TimeSpan.Zero;
        var pacer = new DeviceFramePacer();
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
                    for (uint i = 0; i < client.Controllers.Count; i++) await client.SetDirectModeAsync(i, timeout.Token);
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
                    foreach (var added in found.Where(f => before.All(b => b.Id != f.Id)))
                        await live.SetDirectModeAsync(idx[added.Id], refreshTimeout.Token);
                    lock (stateGate) { devices = found; rgbIndices = idx; }
                    pacer.Reset();
                    continue;
                }
                RgbDeviceState[] current; lock (stateGate) current = devices;
                foreach (var device in current.Where(d => (failsafe && value.Events.FanFailsafe) || value.SyncedDevices.GetValueOrDefault(d.Id, true) && value.Lighting.Parameters?.Devices?.GetValueOrDefault(d.Id, true) != false))
                {
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
                System.Diagnostics.Trace.TraceWarning("RGB reconnect: " + e);
                Console.Error.WriteLine($"[rgb] reconnect after {e.GetType().Name}: {e.Message}");
                lock (stateGate) devices = devices.Select(d => d with { Connected = false }).ToArray();
                await rgb.DisposeAsync(); rgb = mock ? new FakeRgbBackend() : new OpenRgbClient(new TcpConnectionFactory());
                rgbConnected = mock;
            }
            finally { rgbGate.Release(); }
        } while (await timer.WaitForNextTickAsync(token));
    }

    private IReadOnlyList<RgbColor> RenderDevice(ServiceSettings value, RgbDeviceState device, IReadOnlyList<RgbDeviceState> current, double time)
    {
        var layer = value.LedLayers.GetValueOrDefault(device.Id);
        var positions = CaseMap.Positions(device, CaseMap.Placement(value.Map, device, current), value.Map);
        var rendered = SpatialEffects.Render(value.Lighting.ToScene(), value.Lighting.GetParameters(), time, positions, device.Id, device.Kind, FrameInputs());
        var colors = LedLayers.Composite(rendered, layer?.Custom, layer?.Overlay, layer?.CustomEnabled == true || value.Lighting.Effect == LightingEffect.Custom);
        return eventEffects.Composite(colors, positions, time, value.Events);
    }

    private async Task LampAsync(CancellationToken token)
    {
        if (Observe) await Task.Delay(Timeout.Infinite, token);
        int credentials = Volatile.Read(ref lampCredentials); string? connectedHost = null;
        LampConfiguration? applied = null; string transport = mock ? "mock" : "not configured";
        LampSync? sync = lamp is null ? null : new(lamp, clock);
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(500));
        var retry = TimeSpan.Zero;
        do
        {
            var value = Volatile.Read(ref settings); var config = value.Lamp;
            try
            {
                if (credentials != Volatile.Read(ref lampCredentials) || (applied is not null && config.Host != connectedHost))
                {
                    if (lamp is not null) await lamp.DisposeAsync();
                    lamp = null; applied = null; retry = TimeSpan.Zero;
                    credentials = Volatile.Read(ref lampCredentials);
                }
                if (lamp is null && clock.Elapsed >= retry)
                {
                    if (mock) { lamp = new FakeLampClient(); transport = "mock"; sync = new(lamp, clock); }
                    else
                    {
                        var factory = new LampFactory(new TcpConnectionFactory(), new(new UdpDiscoveryTransport()), clock);
                        var connection = await factory.CreateAsync(config.Host, new LampTokenStore().Load(), token);
                        lamp = connection.Client; transport = connection.Transport;
                        if (lamp is null) { warnings["lamp"] = "Lamp not configured."; retry = clock.Elapsed + TimeSpan.FromSeconds(5); continue; }
                        sync = new(lamp, clock);
                    }
                }
                if (lamp is null) continue;
                connectedHost = config.Host;
                if (config != applied)
                {
                    await lamp.SetPowerAsync(config.On, token);
                    if (lamp is MiioLampClient m)
                    { await m.SetBrightnessAsync(config.Brightness, token); if (config.Kelvin is { } k) await m.SetColorTemperatureAsync(k, token); }
                    if (lamp is YeelightClient y)
                    { await y.SetBrightnessAsync(config.Brightness, token); if (config.Kelvin is { } k) await y.SetColorTemperatureAsync(k, token); }
                    if (config.Follow == "own" && config.Kelvin is null) await lamp.SetColorAsync(LightingConfiguration.ParseColor(config.Color), token);
                    applied = config;
                }
                if (config.On && config.Follow != "own")
                {
                    RgbDeviceState[] current; lock (stateGate) current = devices;
                    double time = clock.Elapsed.TotalSeconds;
                    IReadOnlyList<RgbColor> colors;
                    if (config.Follow == "pc")
                    {
                        MapPoint[] point = [CaseMap.LampPoint(value.Map, current)];
                        var rendered = SpatialEffects.Render(value.Lighting.ToScene(), value.Lighting.GetParameters(), time, point, kind: "strip", input: FrameInputs());
                        colors = eventEffects.Composite(rendered, point, time, value.Events);
                    }
                    else colors = current.Where(d => value.SyncedDevices.GetValueOrDefault(d.Id, true) && value.Lighting.Parameters?.Devices?.GetValueOrDefault(d.Id, true) != false)
                        .SelectMany(d => RenderDevice(value, d, current, time)).ToArray();
                    await sync!.PushAsync(colors, LampColorSelection.Average, cancellationToken: token);
                }
                warnings.TryRemove("lamp", out _);
                lock (stateGate) lampState = new("yeelink.light.color5", true, config.On, config.Brightness, config.Color, config.Follow, transport);
            }
            catch (Exception e) when (!token.IsCancellationRequested && e is IOException or SocketException or OperationCanceledException or InvalidOperationException or System.Security.Cryptography.CryptographicException or System.Text.Json.JsonException or KeyNotFoundException)
            {
                warnings["lamp"] = "Lamp unavailable; check transport and token configuration.";
                lock (stateGate) if (lampState is not null) lampState = lampState with { Connected = false };
                if (lamp is not null) await lamp.DisposeAsync(); lamp = null; applied = null;
                retry = clock.Elapsed + TimeSpan.FromSeconds(5);
            }
        } while (await timer.WaitForNextTickAsync(token));
    }

    private async Task RouterAsync(CancellationToken token)
    {
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
        await rgb.DisposeAsync(); if (lamp is not null) await lamp.DisposeAsync();
        router?.Dispose(); rgbProcess.Dispose(); stop.Dispose();
    }
}
