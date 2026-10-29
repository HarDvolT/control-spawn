using System.Text.Json;
using System.Diagnostics;
using Prism.Core;
using Prism.Core.Ipc;
using Prism.Lighting;
using Prism.Hardware;
using Prism.Router;
using Prism.Service;
using Xunit;

namespace Prism.Service.Tests;

public sealed class LedCommandTests
{
    [Theory]
    [InlineData(false, LightingEffect.Static, 133, 10, 15)]
    [InlineData(true, LightingEffect.Static, 128, 0, 50)]
    [InlineData(false, LightingEffect.Custom, 128, 0, 50)]
    public async Task RuntimeRendersStoredDeviceLayersThroughFakeBackend(bool deviceCustom, LightingEffect effect, byte r, byte g, byte b)
    {
        var rgb = new FakeRgbBackend();
        await using var runtime = new ServiceRuntime(true, new FakeSensorSource(), new FakeFanControlPort(),
            new FakeHealthProbe(), rgb, new FakeLampClient(), new FakeRouterBackend());
        var settings = new ServiceSettings { Lighting = new(effect, "#0A141E"), LedLayers = new()
            { ["0"] = new(Enumerable.Repeat("#000064", 12).ToArray(), Enumerable.Repeat("#FF000080", 12).ToArray(), deviceCustom) } };
        await runtime.ApplyAsync(settings, default);
        using var stop = new CancellationTokenSource(); var run = runtime.RunAsync(stop.Token);
        Assert.Equal(new RgbColor(r, g, b), rgb.Colors[0][0]);
        var state = runtime.GetState(settings).Lighting.Devices.Single(d => d.Id == "0");
        Assert.Equal(12, state.CustomColors!.Count); Assert.Equal("#FF000080", state.OverlayColors![0]);
        stop.Cancel(); await run;
    }
    private sealed class Runtime : IServiceRuntime
    {
        public RgbDeviceState Device { get; private set; } = new("fixture", "Board", "Motherboard", true, true, 2,
            [new("Board", "Linear", 0, 2, 2, 2), new("ARGB", "Linear", 2, 0, 0, 120)]);
        public int CredentialChanges { get; private set; }
        public StateMessage GetState(ServiceSettings s) => StateAssembler.Assemble(new(DateTimeOffset.UnixEpoch, []), s, [], [Device], null, null, new(true, false, [], false));
        public Task ApplyAsync(ServiceSettings s, CancellationToken token) => Task.CompletedTask;
        public Task<SpeedTestResult?> StartSpeedTestAsync(CancellationToken token) => Task.FromResult<SpeedTestResult?>(null);
        public void CredentialsChanged(bool lamp) => CredentialChanges++;
        public Task<RgbDeviceState> ResizeZoneAsync(string device, int zone, int size, CancellationToken token)
        {
            var zones = Device.Zones!.ToArray(); zones[zone] = zones[zone] with { LedCount = size };
            Device = Device with { LedCount = 2 + size, Zones = zones }; return Task.FromResult(Device);
        }
    }
    private static Request Command(string command, object args) => new(1, command, JsonSerializer.SerializeToElement(args, PipeContract.Json));
    [Fact]
    public async Task LayersPersistAndResizePadsBothThenClearRemovesThem()
    {
        var runtime = new Runtime(); var store = new MemorySettingsStore(); var processor = new CommandProcessor(store, runtime);
        Assert.True((await processor.HandleAsync(Command(Commands.SetLedColors, new { device = "fixture", layer = "custom", colors = new[] { "#112233", "#445566" } }))).Ok);
        Assert.True(processor.Settings.LedLayers["fixture"].CustomEnabled);
        Assert.True((await processor.HandleAsync(Command(Commands.SetLedColors, new { device = "fixture", layer = "overlay", colors = new[] { "#11223344", "#00000000" } }))).Ok);
        Assert.False(processor.Settings.LedLayers["fixture"].CustomEnabled);
        Assert.True((await processor.HandleAsync(Command(Commands.ResizeZone, new { device = "fixture", zone = 1, size = 30 }))).Ok);
        var layer = store.Load().LedLayers["fixture"];
        Assert.Equal(32, layer.Custom!.Length); Assert.Equal("#112233", layer.Custom[0]); Assert.Equal("#000000", layer.Custom[^1]);
        Assert.Equal(32, layer.Overlay!.Length); Assert.Equal("#00000000", layer.Overlay[^1]);
        Assert.Equal(30, new CommandProcessor(store, runtime).Settings.ZoneSizes["fixture"][1]);
        Assert.True((await processor.HandleAsync(Command(Commands.ClearLedColors, new { device = "fixture" }))).Ok);
        Assert.Empty(processor.Settings.LedLayers);
    }
    public static IEnumerable<object[]> Invalid()
    {
        yield return [Commands.SetLedColors, new { device = "missing", layer = "custom", colors = new[] { "#112233", "#112233" } }];
        yield return [Commands.SetLedColors, new { device = "fixture", layer = "invalid", colors = new[] { "#112233", "#112233" } }];
        yield return [Commands.SetLedColors, new { device = "fixture", layer = "custom", colors = new[] { "#112233" } }];
        yield return [Commands.SetLedColors, new { device = "fixture", layer = "custom", colors = new[] { "#11223344", "#112233" } }];
        yield return [Commands.SetLedColors, new { device = "fixture", layer = "overlay", colors = new[] { "#GG223344", "#11223344" } }];
        yield return [Commands.ResizeZone, new { device = "fixture", zone = 1, size = 121 }];
        yield return [Commands.ResizeZone, new { device = "fixture", zone = 1, size = -1 }];
        yield return [Commands.ResizeZone, new { device = "fixture", zone = 0, size = 2 }];
        yield return [Commands.ResizeZone, new { device = "fixture", zone = 2, size = 30 }];
        yield return [Commands.ClearLedColors, new { device = "missing" }];
        yield return [Commands.SetLampToken, new { host = "invalid/host", token = new string('A', 32) }];
        yield return [Commands.SetLampToken, new { host = "192.168.0.2", token = "secret" }];
        yield return [Commands.SetRouterPassword, new { password = "" }];
        yield return [Commands.SetServiceOptions, new { openRgbPath = "relative.exe", launchOpenRgb = true, routerEnabled = true, routerHost = "192.168.0.1" }];
        yield return [Commands.SetServiceOptions, new { openRgbPath = "", launchOpenRgb = false, routerEnabled = true, routerHost = "https://router/" }];
    }
    [Theory, MemberData(nameof(Invalid))]
    public async Task InvalidArgumentsNeverPersistOrWriteSecrets(string command, object args)
    {
        var store = new MemorySettingsStore(); var processor = new CommandProcessor(store, new Runtime(), _ => throw new Exception("Unexpected secret write"), _ => throw new Exception("Unexpected secret write"));
        var before = processor.Settings;
        Assert.False((await processor.HandleAsync(Command(command, args))).Ok); Assert.Same(before, processor.Settings);
    }
    [Fact]
    public async Task SecretsGoOnlyToInjectedEncryptedStoresAndNeverStateResponsesOrTrace()
    {
        const string token = "0123456789ABCDEF0123456789ABCDEF", password = "private-router-secret";
        byte[]? savedToken = null; string? savedPassword = null;
        var runtime = new Runtime(); var store = new MemorySettingsStore();
        var processor = new CommandProcessor(store, runtime, t => savedToken = t.ToArray(), p => savedPassword = p);
        using var log = new StringWriter(); using var listener = new TextWriterTraceListener(log); Trace.Listeners.Add(listener);
        try
        {
            var lampResponse = await processor.HandleAsync(Command(Commands.SetLampToken, new { host = "192.168.0.2", token }));
            var routerResponse = await processor.HandleAsync(Command(Commands.SetRouterPassword, new { password }));
            Assert.True(lampResponse.Ok); Assert.True(routerResponse.Ok);
            Assert.Equal(Convert.FromHexString(token), savedToken); Assert.Equal(password, savedPassword); Assert.Equal(2, runtime.CredentialChanges);
            var json = JsonSerializer.Serialize(new { settings = store.Load(), state = processor.GetState(), lampResponse, routerResponse }, PipeContract.Json);
            listener.Flush();
            Assert.DoesNotContain(token, json); Assert.DoesNotContain(password, json);
            Assert.DoesNotContain(token, log.ToString()); Assert.DoesNotContain(password, log.ToString());
            var failing = new CommandProcessor(store, runtime, _ => throw new ArgumentException(token), _ => throw new InvalidOperationException(password));
            Assert.DoesNotContain(token, JsonSerializer.Serialize(await failing.HandleAsync(Command(Commands.SetLampToken, new { host = "192.168.0.2", token }))));
            Assert.DoesNotContain(password, JsonSerializer.Serialize(await failing.HandleAsync(Command(Commands.SetRouterPassword, new { password }))));
        }
        finally { Trace.Listeners.Remove(listener); }
    }
    [Fact]
    public async Task ServiceOptionsValidatePersistAndReload()
    {
        var store = new MemorySettingsStore(); var runtime = new Runtime(); var processor = new CommandProcessor(store, runtime);
        Assert.True((await processor.HandleAsync(Command(Commands.SetServiceOptions, new { openRgbPath = @"C:\Tools\OpenRGB.exe", launchOpenRgb = true, routerEnabled = true, routerHost = "192.168.0.1" }))).Ok);
        var loaded = new CommandProcessor(store, runtime).Settings;
        Assert.True(loaded.LaunchOpenRgb); Assert.True(loaded.RouterEnabled); Assert.Equal(@"C:\Tools\OpenRGB.exe", loaded.OpenRgbPath);
    }
}
