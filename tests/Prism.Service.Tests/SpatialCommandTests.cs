using System.Text.Json;
using Prism.Core;
using Prism.Core.Ipc;
using Prism.Service;
using Prism.Router;
using Prism.Lighting;
using Prism.Hardware;
using Xunit;

namespace Prism.Service.Tests;

public sealed class SpatialCommandTests
{
    private sealed class Runtime : IServiceRuntime
    {
        public readonly List<LightingEventKind> Events = new();
        public StateMessage GetState(ServiceSettings s) => StateAssembler.Assemble(new(DateTimeOffset.UnixEpoch, []), s, [], [], null, null, new(true, false, [], false));
        public Task ApplyAsync(ServiceSettings settings, CancellationToken token) => Task.CompletedTask;
        public Task<SpeedTestResult?> StartSpeedTestAsync(CancellationToken token) => Task.FromResult<SpeedTestResult?>(null);
        public void TriggerLightingEvent(LightingEventKind kind) => Events.Add(kind);
    }
    private static Request Command(string name, object args) => new(1, name, JsonSerializer.SerializeToElement(args, PipeContract.Json));
    [Fact]
    public async Task ParamsMapEventsAndSceneRoundTripWithoutBreakingOldSetLighting()
    {
        var runtime = new Runtime(); var store = new MemorySettingsStore(); var p = new CommandProcessor(store, runtime);
        var parameters = new EffectParameters { Palette = "Ocean", Angle = 125, Stops = [new(0, "#001122"), new(1, "#77CCFF")], Devices = new Dictionary<string, bool> { ["ram"] = false } };
        Assert.True((await p.HandleAsync(Command(Commands.SetEffectParams, new { parameters }))).Ok);
        var map = new LightingMap([new("ram", 0.6, 0.4, 0.4, 90, true)], new(0.4, 0.2));
        Assert.True((await p.HandleAsync(Command(Commands.SetLightingMap, new { map }))).Ok);
        Assert.True((await p.HandleAsync(Command(Commands.SetEventEffects, new { events = new EventEffectOptions(PingSpike: false) }))).Ok);
        Assert.True((await p.HandleAsync(Command(Commands.SetLighting, new { effect = "OceanRipple", color = "#AABBCC", brightness = 0.7, speed = 1.2 }))).Ok);
        Assert.Equal(125, p.Settings.Lighting.Parameters!.Angle);
        Assert.True((await p.HandleAsync(Command(Commands.SaveLightingScene, new { name = "Gaming" }))).Ok);
        Assert.True((await p.HandleAsync(Command(Commands.SetEffectParams, new { parameters = new EffectParameters() }))).Ok);
        Assert.True((await p.HandleAsync(Command(Commands.ApplyScene, new { name = "Gaming" }))).Ok);
        Assert.Equal(LightingEffect.OceanRipple, p.Settings.Lighting.Effect);
        Assert.Equal("Ocean", p.Settings.Lighting.Parameters!.Palette);
        Assert.Contains(LightingEventKind.SceneChange, runtime.Events);
        Assert.True((await p.HandleAsync(Command(Commands.NotifyLighting, new { kind = "Notification" }))).Ok);
        Assert.Contains(LightingEventKind.Notification, runtime.Events);
        var restarted = new CommandProcessor(store, runtime).GetState().Lighting;
        Assert.False(restarted.Events!.PingSpike); Assert.Equal(new(0.4, 0.2), restarted.Map!.Lamp);
        Assert.True(restarted.Map.Devices[0].Flip); Assert.False(restarted.Parameters!.Devices!["ram"]);
        var json = JsonSerializer.Serialize(p.Settings, PipeContract.Json);
        var restored = JsonSerializer.Deserialize<ServiceSettings>(json, PipeContract.Json)!; restored.Validate();
        Assert.Equal(125, restored.Lighting.Parameters!.Angle);
    }
    public static IEnumerable<object[]> Invalid()
    {
        yield return [Commands.SetEffectParams, new { parameters = new { angle = 361 } }];
        yield return [Commands.SetEffectParams, new { parameters = new { scale = 0 } }];
        yield return [Commands.SetEffectParams, new { parameters = new { stops = new[] { new { position = 0, color = "#FFFFFF" } } } }];
        yield return [Commands.SetEffectParams, new { parameters = new { palette = "Unknown" } }];
        yield return [Commands.SetEffectParams, new { parameters = new { tailLength = -1 } }];
        yield return [Commands.SetEffectParams, new { parameters = new { cometCount = 100 } }];
        yield return [Commands.SetEffectParams, new { parameters = (object?)null }];
        yield return [Commands.SetLightingMap, new { map = new { devices = new[] { new { device = "ram", x = -1, y = 0.5 } } } }];
        yield return [Commands.SetLightingMap, new { map = new { devices = (object?)null } }];
        yield return [Commands.SetEventEffects, new { events = (object?)null }];
        yield return [Commands.NotifyLighting, new { kind = "FanFailsafe" }];
        yield return [Commands.SaveLightingScene, new { name = "Unknown" }];
    }
    [Theory, MemberData(nameof(Invalid))]
    public async Task InvalidCommandsDoNotPersistOrTriggerEvents(string command, object args)
    {
        var runtime = new Runtime(); var store = new MemorySettingsStore(); var p = new CommandProcessor(store, runtime); var before = p.Settings;
        Assert.False((await p.HandleAsync(Command(command, args))).Ok);
        Assert.Same(before, p.Settings); Assert.Same(before, store.Load()); Assert.Empty(runtime.Events);
    }
    [Fact]
    public async Task MockRuntimeStaysAliveAndEventOverlaysCustomOutputWithoutStartingCapture()
    {
        var rgb = new FakeRgbBackend();
        await using var runtime = new ServiceRuntime(true, new FakeSensorSource(), new FakeFanControlPort(), new FakeHealthProbe(), rgb, new FakeLampClient(), new FakeRouterBackend());
        var settings = new ServiceSettings { LedLayers = new() { ["0"] = new(Enumerable.Repeat("#0000FF", 12).ToArray(), CustomEnabled: true) } };
        await runtime.ApplyAsync(settings, default);
        runtime.TriggerLightingEvent(LightingEventKind.FanFailsafe);
        using var stop = new CancellationTokenSource(); var running = runtime.RunAsync(stop.Token);
        Assert.False(running.IsCompleted);
        Assert.True(rgb.Colors[0][0].R > 200); Assert.True(rgb.Colors[0][0].B < 100);
        stop.Cancel(); await running;
    }
    private sealed class CaptureFixture : IAudioLevels, IScreenZones
    {
        public IReadOnlyList<double> Bands => new[] { 0.2, 0.8 };
        public double Beat => 0.4;
        public RgbColor Left => new(10, 20, 30);
        public RgbColor Centre => new(40, 50, 60);
        public RgbColor Right => new(70, 80, 90);
    }
    [Fact]
    public async Task PreviewTelemetryUsesInjectedCaptureLevelsAndColorsOnly()
    {
        var capture = new CaptureFixture();
        await using var runtime = new ServiceRuntime(true, new FakeSensorSource(), new FakeFanControlPort(), new FakeHealthProbe(), new FakeRgbBackend(), new FakeLampClient(), new FakeRouterBackend(), capture, capture);
        var input = runtime.GetState(new()).Lighting.Inputs!;
        Assert.Equal(new[] { 0.2, 0.8 }, input.AudioBands); Assert.Equal(0.4, input.Beat);
        Assert.Equal("#0A141E", input.ScreenLeft); Assert.Equal("#28323C", input.ScreenCentre); Assert.Equal("#46505A", input.ScreenRight);
    }
    [Fact]
    public void OldProtocolPayloadsDeserializeWithDefaults()
    {
        var state = JsonSerializer.Deserialize<LightingState>("""{"effect":"Static","color":"#112233","brightness":1,"speed":1,"devices":[]}""", PipeContract.Json)!;
        Assert.Null(state.Parameters); Assert.Null(state.Map); Assert.Null(state.Events); Assert.Equal(1, PipeContract.ProtocolVersion);
        var settings = JsonSerializer.Deserialize<ServiceSettings>("{}", PipeContract.Json)!; settings.Validate(); Assert.Empty(settings.Map.Devices);
    }
}
