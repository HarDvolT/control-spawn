using System.Text.Json;
using ControlSpawn.Core.Ipc;
using ControlSpawn.Lighting;
using ControlSpawn.Router;
using Xunit;

namespace ControlSpawn.Service.Tests;

public sealed class LampWhiteCommandTests
{
    private sealed class Runtime : IServiceRuntime
    {
        public List<string> TakenOver { get; } = new();
        public void TakeOverLamp(string id) => TakenOver.Add(id);
        public StateMessage GetState(ServiceSettings settings) => StateAssembler.Assemble(new(DateTimeOffset.UnixEpoch, []), settings, [], [], null, null, new(true, false, [], false));
        public Task ApplyAsync(ServiceSettings settings, CancellationToken token) => Task.CompletedTask;
        public Task<SpeedTestResult?> StartSpeedTestAsync(CancellationToken token) => Task.FromResult<SpeedTestResult?>(null);
    }
    private static Request Command(object args) => new(1, Commands.SetLamp, JsonSerializer.SerializeToElement(args, PipeContract.Json));

    [Fact]
    public async Task AWhiteIsSavedAsAColourTemperatureAndAColourClearsIt()
    {
        var store = new MemorySettingsStore(); store.Save(new() { Lamps = [new(DeviceId: 10, Id: "lamp:10", Follow: "pc")] });
        var runtime = new Runtime(); var processor = new CommandProcessor(store, runtime);
        Assert.True((await processor.HandleAsync(Command(new { lamp = "lamp:10", follow = "own", color = LampWhite.Hex(2700), kelvin = 2700 }))).Ok);
        Assert.Equal(2700, processor.Settings.Lamps![0].Kelvin);
        Assert.True((await processor.HandleAsync(Command(new { lamp = "lamp:10", brightness = 40 }))).Ok);
        Assert.Equal(2700, processor.Settings.Lamps[0].Kelvin);   // untouched by an unrelated change
        Assert.True((await processor.HandleAsync(Command(new { lamp = "lamp:10", color = "#36D6FF" }))).Ok);
        Assert.Null(processor.Settings.Lamps[0].Kelvin);
        Assert.False((await processor.HandleAsync(Command(new { lamp = "lamp:10", kelvin = 9000 }))).Ok);
        Assert.Equal(["lamp:10", "lamp:10", "lamp:10"], runtime.TakenOver);   // every change here takes a handed-off bulb back
    }

    [Fact]
    public void WhatABulbWasLeftShowingSurvivesARestartWithoutStoringAStreamingColour()
    {
        var path = Path.Combine(Path.GetTempPath(), "controlspawn-lamps-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            var white = new LampReading(true, 90, LampReading.WhiteMode, 5000, 0x112233);
            new LampMemory(path).Remember("lamp:10", white, moving: false);
            new LampMemory(path).Remember("lamp:20", white with { Mode = LampReading.ColorMode }, moving: true);
            var reloaded = new LampMemory(path);
            Assert.Equal(new LeftLamp(white, false), reloaded.Get("lamp:10"));
            Assert.Equal(new LeftLamp(white with { Mode = LampReading.ColorMode, Rgb = 0 }, true), reloaded.Get("lamp:20"));
            Assert.Null(reloaded.Get("lamp:30"));
        }
        finally { File.Delete(path); }
    }
}
