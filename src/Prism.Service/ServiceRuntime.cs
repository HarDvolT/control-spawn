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
        IRgbBackend rgb, ILampClient? lamp, IRouterBackend? router)
    {
        this.mock = mock;
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
            var synced = devices.Select(d => d with { Synced = value.SyncedDevices.GetValueOrDefault(d.Id, true) }).ToArray();
            var health = new ServiceHealth(mock, elevated, warnings.Values.Order().ToArray(), failsafe);
            return StateAssembler.Assemble(snapshot, value, controls.Headers, synced, lampState, routerState, health);
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
        var loops = new[] { SensorsAsync(linked.Token), LightingAsync(linked.Token), LampAsync(linked.Token), RouterAsync(linked.Token), HealthAsync(linked.Token) };
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
            lock (stateGate) { snapshot = next; failsafe = safety.Observe(next, required); inputs.Temperature = next.Temperature(SensorCategory.Cpu) ?? double.NaN; }
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
        var engine = new EffectEngine(clock, inputs, inputs, inputs);
        TimeSpan reconnect = TimeSpan.Zero;
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1d / 30));
        do
        {
            var value = Volatile.Read(ref settings);
            try
            {
                if (!mock && !rgbConnected && clock.Elapsed >= reconnect)
                {
                    if (value.LaunchOpenRgb && value.OpenRgbPath is not null) rgbProcess.Start(value.OpenRgbPath);
                    var client = (OpenRgbClient)rgb;
                    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token); timeout.CancelAfter(TimeSpan.FromSeconds(2));
                    await client.ConnectAsync(cancellationToken: timeout.Token);
                    for (uint i = 0; i < client.Controllers.Count; i++) await client.SetDirectModeAsync(i, timeout.Token);
                    var identities = new Dictionary<string, int>(); var indices = new Dictionary<string, uint>();
                    var discovered = client.Controllers.Select((d, i) =>
                    {
                        var metadata = string.Join("|", d.Type, d.Vendor, d.Name, d.Serial, d.Location);
                        var digest = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(metadata)))[..24];
                        int occurrence = identities.GetValueOrDefault(digest); identities[digest] = occurrence + 1;
                        string identity = $"rgb:{digest}:{occurrence}";
                        indices[identity] = (uint)i;
                        return new RgbDeviceState(identity, d.Name, d.Type.ToString(), true, true, d.Colors.Count);
                    }).ToArray();
                    lock (stateGate) { devices = discovered; rgbIndices = indices; }
                    rgbConnected = true;
                }
                if (!rgbConnected) continue;
                RgbDeviceState[] current; lock (stateGate) current = devices;
                foreach (var device in current.Where(d => value.SyncedDevices.GetValueOrDefault(d.Id, true)))
                {
                    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token); timeout.CancelAfter(TimeSpan.FromSeconds(2));
                    await rgb.SetColorsAsync(rgbIndices[device.Id], engine.Render(value.Lighting.ToScene(), device.LedCount), timeout.Token);
                }
                warnings.TryRemove("rgb", out _);
            }
            catch (Exception e) when (!token.IsCancellationRequested && e is IOException or SocketException or OperationCanceledException or InvalidOperationException or ArgumentException or System.ComponentModel.Win32Exception)
            {
                rgbConnected = false; reconnect = clock.Elapsed + TimeSpan.FromSeconds(5);
                warnings["rgb"] = "OpenRGB server not reachable on 127.0.0.1:6742.";
                lock (stateGate) devices = devices.Select(d => d with { Connected = false }).ToArray();
                await rgb.DisposeAsync(); rgb = mock ? new FakeRgbBackend() : new OpenRgbClient(new TcpConnectionFactory());
                rgbConnected = mock;
            }
        } while (await timer.WaitForNextTickAsync(token));
    }

    private async Task LampAsync(CancellationToken token)
    {
        if (Observe) await Task.Delay(Timeout.Infinite, token);
        LampConfiguration? applied = null; string transport = mock ? "mock" : "not configured";
        LampSync? sync = lamp is null ? null : new(lamp, clock);
        var engine = new EffectEngine(clock, inputs, inputs, inputs);
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(500));
        var retry = TimeSpan.Zero;
        do
        {
            var value = Volatile.Read(ref settings); var config = value.Lamp;
            try
            {
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
                    await sync!.PushAsync(engine.Render(value.Lighting.ToScene(), 30), config.Follow == "pc" ? LampColorSelection.Main : LampColorSelection.Average, cancellationToken: token);
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
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(2));
        do
        {
            if (!mock && !Volatile.Read(ref settings).RouterEnabled) continue;
            try
            {
                router ??= new RouterBackend(Volatile.Read(ref settings).RouterHost);
                var state = await Task.Run(router.Poll, token);
                lock (stateGate) routerState = state;
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
            if (!mock) { warnings["capture"] = "Audio and screen capture inputs are not configured; Music and Adaptive use fallback inputs."; }
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
