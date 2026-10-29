using System.Text.Json;
using Prism.Core;
using Prism.Core.Ipc;
using Prism.Router;
using Prism.Hardware;
using Prism.Lighting;
using Prism.Service;
using Xunit;

namespace Prism.Service.Tests;
public sealed class SensorBindingTests
{
    [Fact]
    public async Task FakeRuntimeUsesSensorBindingForUnsyncedDeviceWhileOthersKeepNormalEffect()
    {
        var rgb = new FakeRgbBackend();
        await using var runtime = new ServiceRuntime(true, new FakeSensorSource(), new FakeFanControlPort(), new FakeHealthProbe(), rgb, new FakeLampClient(), new FakeRouterBackend());
        var settings = new ServiceSettings {
            Lighting = new(LightingEffect.Static, "#FF0000", 1), SyncedDevices = new() { ["0"] = false },
            Bindings = new() { ["0"] = new("cpu.temperature", 0, 100, Palette: [new(0, "#0000FF"), new(1, "#0000FF")], Smoothing: 0) }
        };
        await runtime.ApplyAsync(settings, default);
        using var stop = new CancellationTokenSource(); var run = runtime.RunAsync(stop.Token);
        Assert.All(rgb.Colors[0], c => Assert.Equal(new RgbColor(0, 0, 255), c));
        Assert.All(rgb.Colors[1], c => Assert.Equal(new RgbColor(255, 0, 0), c));
        Assert.Equal(45d, runtime.GetState(settings).Lighting.BindingReadings!["0"].Value);
        stop.Cancel(); await run;
    }
    private sealed class Runtime : IServiceRuntime
    {
        public int Applies;
        public StateMessage GetState(ServiceSettings settings) => StateAssembler.Assemble(new(DateTimeOffset.UnixEpoch, []), settings, [],
            [new("gpu", "GPU", "GPU", true, true, 4, [new("Zone", "Linear", 1, 2, 2, 2)]), new("ram", "RAM", "RAM", true, true, 4)], null, null, new(true, false, [], false));
        public Task ApplyAsync(ServiceSettings settings, CancellationToken token) { Applies++; return Task.CompletedTask; }
        public Task<SpeedTestResult?> StartSpeedTestAsync(CancellationToken token) => Task.FromResult<SpeedTestResult?>(null);
    }
    private static Request Command(object args) => new(1, Commands.SetSensorBinding, JsonSerializer.SerializeToElement(args, PipeContract.Json));
    [Fact]
    public async Task BindingsPersistIndependentlyAndRoundTripThroughStateAndRemoval()
    {
        var store = new MemorySettingsStore(); var runtime = new Runtime(); var p = new CommandProcessor(store, runtime);
        foreach (var target in new[] { "gpu", "ram", "gpu/zone/0" })
            Assert.True((await p.HandleAsync(Command(new { target, source = target == "ram" ? "cpu.load" : "gpu.temperature", min = 35, max = 85, autoLearn = true }))).Ok);
        Assert.Equal(3, store.Load().Bindings.Count);
        Assert.Equal("cpu.load", runtime.GetState(p.Settings).Lighting.Bindings!["ram"].Source);
        Assert.True(new CommandProcessor(store, runtime).Settings.Bindings["gpu"].AutoLearn);
        Assert.True((await p.HandleAsync(Command(new { target = "gpu", source = (string?)null }))).Ok);
        Assert.Equal(2, p.Settings.Bindings.Count); Assert.Equal(LightingEffect.Static, p.Settings.Lighting.Effect);
    }
    [Theory]
    [InlineData("missing", "gpu.temperature", 35, 85)] [InlineData("gpu/zone/9", "gpu.temperature", 35, 85)]
    [InlineData("gpu", "fake", 35, 85)] [InlineData("gpu", "gpu.temperature", 85, 35)]
    public async Task InvalidCommandsAreAtomic(string target, string source, double min, double max)
    {
        var runtime = new Runtime(); var p = new CommandProcessor(new MemorySettingsStore(), runtime); var before = p.Settings;
        Assert.False((await p.HandleAsync(Command(new { target, source, min, max }))).Ok);
        Assert.Same(before, p.Settings); Assert.Equal(0, runtime.Applies);
    }
}
