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
    private FanGuardProcess? fanGuard;
    private TimeSpan sensorCycle = TimeSpan.FromSeconds(-1);
    private readonly bool enableFanGuard;
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
    public void ReportSecretMigrationFailure() => warnings["secrets"] = "Legacy credentials could not be migrated; run an elevated console under the original user or provision credentials through the installed app.";
    private bool pingHigh, signalPoor;
    private ReactiveFrame reactive = new();
    private readonly ReactiveSmoother smoothing = new();
    private readonly OpenRgbProcess rgbProcess = new();
    private readonly object stateGate = new();
    private HardwareSnapshot snapshot = new(DateTimeOffset.UtcNow, []);
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
        : this(mock, HardwareComposition.Create(mock ? ["--mock"] : []), observe) { Observe = observe; enableFanGuard = !mock && !observe; }

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

    private void ModuleHealth(string name, bool healthy)
    {
        if (healthy) warnings.TryRemove("module:" + name, out _);
        else warnings["module:" + name] = name + " module failed; retrying.";
    }

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
            var health = new ServiceHealth(mock, elevated, warnings.Values.Order().ToArray(), failsafe);
            var lamps = value.ConfiguredLamps.Select(c => lampStates.GetValueOrDefault(c.Id) ?? new LampState("yeelink.light.color5", false, c.On, c.Brightness, c.Color, c.Follow, "not configured", c.Id, c.Name, c.DeviceId, c.LastHost ?? c.Host)).ToArray();
            var state = StateAssembler.Assemble(snapshot, value, controls.Headers, synced, lamps.FirstOrDefault(), routerState, health) with { Lamps = lamps };
            var frame = FrameInputs();
            static string Hex(RgbColor? color) { var c = color ?? RgbColor.Black; return $"#{c.R:X2}{c.G:X2}{c.B:X2}"; }
            return DeviceNames.Apply(state with { Appliances = appliances.States, Lighting = state.Lighting with { BindingReadings = sensorLighting.Readings(value.Bindings), Inputs = new(frame.Audio?.ToArray() ?? [], frame.Beat,
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
        IPrismModule[] modules = [new SensorModule(this), new FanModule(this), new RgbModule(this), new LampModule(this), new RouterModule(this),
            new HealthModule(this), new CaptureModule(this, true), new CaptureModule(this, false), new DiscoveryModule(this), new ApplianceModule(this)];
        // Sensor reads may block in native code. Start each module independently of the others.
        foreach (var module in modules) module.Start(linked.Token, background: !mock);
        var loops = modules.Select(module => module.Completion).ToArray();
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
                try { await Task.WhenAll(modules.Select(module => module.StopAsync())); } catch (OperationCanceledException) when (linked.IsCancellationRequested) { }
            }
            finally
            {
                RestoreDefaults(); fanGuard?.Dispose(); fanGuard = null;
                AppDomain.CurrentDomain.ProcessExit -= exit;
                AppDomain.CurrentDomain.UnhandledException -= crash;
                Console.CancelKeyPress -= cancel;
            }
        }
    }

    private IReadOnlyList<RgbColor> RenderDevice(ServiceSettings value, RgbDeviceState device, IReadOnlyList<RgbDeviceState> current, double time)
    {
        var layer = value.LedLayers.GetValueOrDefault(device.Id);
        var positions = CaseMap.Positions(device, CaseMap.Placement(value.Map, device, current), value.Map);
        var rendered = SpatialEffects.Render(value.Lighting.ToScene(), value.Lighting.GetParameters(), time, positions, device.Id, device.Kind, FrameInputs());
        var colors = LedLayers.Composite(rendered, layer?.Custom, layer?.Overlay, layer?.CustomEnabled == true || value.Lighting.Effect == LightingEffect.Custom);
        var sensorState = GetState(value);
        colors = sensorLighting.Composite(device, value.Bindings, sensorState, time, colors, value.Lighting.Brightness);
        return eventEffects.Composite(colors, positions, time, value.Events, SensorLighting.AlertLeds(device, value.Bindings, sensorState));
    }

    private ReactiveFrame FrameInputs() => smoothing.Sample(Volatile.Read(ref reactive), clock.Elapsed.TotalSeconds) with {
        Audio = audioLevels?.Bands ?? new[] { inputs.Level }, Beat = audioLevels?.Beat ?? 0,
        ScreenLeft = screenZones?.Left ?? inputs.Average, ScreenCentre = screenZones?.Centre ?? inputs.Average,
        ScreenRight = screenZones?.Right ?? inputs.Average };

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
            catch (Exception e) { PrismLog.Failure("runtime", e); }
        }
        if (speedTask is not null)
        {
            try { await speedTask; } catch (Exception e) { PrismLog.Failure("runtime", e); }
        }
        fans.Dispose(); sensors.Dispose();
        await appliances.DisposeAsync();
        await rgb.DisposeAsync(); if (lamp is not null) await lamp.DisposeAsync();
        router?.Dispose(); rgbProcess.Dispose(); stop.Dispose();
    }
}
