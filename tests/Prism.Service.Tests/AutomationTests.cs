using System.Text.Json;
using Prism.Core;
using Prism.Core.Ipc;
using Prism.Router;
using Xunit;

namespace Prism.Service.Tests;

public sealed class AutomationTests
{
    private sealed class Runtime : IServiceRuntime
    {
        public StateMessage GetState(ServiceSettings settings) => StateAssembler.Assemble(new(DateTimeOffset.UnixEpoch, []), settings, [2, 6, 7], [], null, null, new(true, false, [], false));
        public Task ApplyAsync(ServiceSettings settings, CancellationToken token) => Task.CompletedTask;
        public Task<SpeedTestResult?> StartSpeedTestAsync(CancellationToken token) => Task.FromResult<SpeedTestResult?>(null);
    }
    private static Request Request(int id, string command, object? args = null) => new(id, command, args is null ? null : JsonSerializer.SerializeToElement(args, PipeContract.Json));
    private static readonly string[] Scenes = ["Gaming", "Night", "Away"];
    private static readonly DateTime Noon = new(2026, 11, 2, 12, 0, 0), Midnight = new(2026, 11, 2, 0, 30, 0);

    [Theory]
    [InlineData(@"D:\SteamLibrary\steamapps\common\Cyberpunk 2077\bin\x64\Cyberpunk2077.exe", "Cyberpunk2077")]
    [InlineData(@"C:\Program Files\Epic Games\Fortnite\FortniteGame\Binaries\Win64\FortniteClient-Win64-Shipping.exe", "FortniteClient-Win64-Shipping")]
    [InlineData(@"C:\XboxGames\Forza Horizon 5\Content\ForzaHorizon5.exe", "ForzaHorizon5")]
    [InlineData(@"C:\Program Files (x86)\Steam\steam.exe", null)]
    [InlineData(@"C:\Program Files\Epic Games\Launcher\Portal\Binaries\Win64\EpicGamesLauncher.exe", null)]
    [InlineData(@"D:\SteamLibrary\steamapps\common\Steamworks Shared\_CommonRedist\vcredist.exe", null)]
    [InlineData(@"C:\Windows\explorer.exe", null)]
    public void DetectsGamesByInstallFolderButNotLaunchers(string path, string? expected) =>
        Assert.Equal(expected, AutomationEngine.FindGame([path], null));

    [Fact]
    public void ExtraGamesMatchByProgramName() =>
        Assert.Equal("osu!", AutomationEngine.FindGame([@"C:\Users\me\AppData\Local\osu!\osu!.exe"], ["osu!.exe"]));

    [Theory]
    [InlineData(23, 30, true)] [InlineData(3, 0, true)] [InlineData(7, 0, false)] [InlineData(12, 0, false)] [InlineData(22, 59, false)]
    public void NightWindowWrapsPastMidnight(int hour, int minute, bool expected) =>
        Assert.Equal(expected, AutomationEngine.InNight(new TimeSpan(hour, minute, 0), "23:00", "07:00"));

    [Fact]
    public void LockBeatsGameBeatsNightAndMissingScenesAreSkipped()
    {
        var rules = new AutomationSettings();
        string[] game = [@"D:\steamapps\common\Game\game.exe"];
        Assert.Equal("lock", AutomationEngine.Evaluate(rules, Scenes, Midnight, true, game)!.Rule);
        Assert.Equal("game", AutomationEngine.Evaluate(rules, Scenes, Midnight, false, game)!.Rule);
        Assert.Equal("night", AutomationEngine.Evaluate(rules, Scenes, Midnight, false, [])!.Rule);
        Assert.Null(AutomationEngine.Evaluate(rules, Scenes, Noon, false, []));
        Assert.Null(AutomationEngine.Evaluate(rules with { LockEnabled = false }, Scenes, Noon, true, []));
        Assert.Equal("night", AutomationEngine.Evaluate(rules, ["Night"], Midnight, true, game)!.Rule);
    }

    [Fact]
    public async Task AppliesTheSceneThenRestoresWhatTheUserHad()
    {
        var processor = new CommandProcessor(new MemorySettingsStore(), new Runtime());
        string[] programs = [];
        processor.Clock = () => Noon; processor.RunningPrograms = () => programs;
        Assert.True((await processor.HandleAsync(Request(1, Commands.SetLighting, new { effect = "Static", color = "#36D6FF", brightness = 1.0, speed = 1.0 }))).Ok);
        var mine = processor.Settings.Lighting;

        programs = [@"D:\steamapps\common\Game\game.exe"];
        await processor.EvaluateAutomationsAsync();
        Assert.Equal(LightingEffect.Smart, processor.Settings.Lighting.Effect);
        Assert.Equal("game", processor.GetState().Automations!.Rule);
        Assert.Equal("game is running", processor.GetState().Automations!.Detail);

        programs = [];
        await processor.EvaluateAutomationsAsync();
        Assert.Equal(mine, processor.Settings.Lighting);
        Assert.Null(processor.GetState().Automations!.Rule);
    }

    [Fact]
    public async Task AManualChangeDuringAnAutomationIsKept()
    {
        var processor = new CommandProcessor(new MemorySettingsStore(), new Runtime());
        processor.Clock = () => Noon; processor.RunningPrograms = () => [];
        Assert.True((await processor.HandleAsync(Request(1, Commands.SetSessionLocked, new { locked = true }))).Ok);
        await processor.EvaluateAutomationsAsync();
        Assert.Equal(LightingEffect.Off, processor.Settings.Lighting.Effect);
        Assert.True((await processor.HandleAsync(Request(2, Commands.SetLighting, new { effect = "Static", color = "#FF4D4F", brightness = 1.0, speed = 1.0 }))).Ok);
        Assert.True((await processor.HandleAsync(Request(3, Commands.SetSessionLocked, new { locked = false }))).Ok);
        await processor.EvaluateAutomationsAsync();
        Assert.Equal("#FF4D4F", processor.Settings.Lighting.Color);
    }

    [Fact]
    public async Task SettingsCanBeChangedAndBadHoursAreRejected()
    {
        var processor = new CommandProcessor(new MemorySettingsStore(), new Runtime());
        Assert.True((await processor.HandleAsync(Request(1, Commands.SetAutomations, new { nightEnabled = false, gameScene = "Movie", extraGames = new[] { "osu!.exe", " " } }))).Ok);
        var rules = processor.Settings.Automations;
        Assert.False(rules.NightEnabled); Assert.Equal("Movie", rules.GameScene); Assert.Equal(["osu!.exe"], rules.ExtraGames);
        Assert.False((await processor.HandleAsync(Request(2, Commands.SetAutomations, new { nightStart = "25:99" }))).Ok);
        Assert.False((await processor.HandleAsync(Request(3, Commands.SetSessionLocked, new { }))).Ok);
    }

    private sealed class GrowingHistory : IServiceRuntime
    {
        private int calls;
        public StateMessage GetState(ServiceSettings settings)
        {
            int count = Interlocked.Increment(ref calls) + 2;
            var history = Enumerable.Range(0, count).Select(i => new TelemetrySample(DateTimeOffset.UnixEpoch.AddSeconds(i), i, null, null, null, null, null, null)).ToArray();
            return StateAssembler.Assemble(new(DateTimeOffset.UnixEpoch, []), settings, [2, 6, 7], [], null, null, new(true, false, [], false)) with { History = history };
        }
        public Task ApplyAsync(ServiceSettings settings, CancellationToken token) => Task.CompletedTask;
        public Task<SpeedTestResult?> StartSpeedTestAsync(CancellationToken token) => Task.FromResult<SpeedTestResult?>(null);
    }

    [Fact]
    public async Task SubscribersGetTheFullHistoryOnceThenOnlyNewSamples()
    {
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var processor = new CommandProcessor(new MemorySettingsStore(), new GrowingHistory());
        var (client, server) = ServiceTests.MemoryDuplex.Create();
        await using var a = client; await using var b = server;
        var session = new PipeSession(processor).RunAsync(server, stop.Token);
        using var writer = new System.IO.StreamWriter(client, new System.Text.UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };
        using var reader = new System.IO.StreamReader(client, leaveOpen: true);
        await writer.WriteLineAsync(JsonSerializer.Serialize(Request(1, Commands.Hello, new { version = 1, client = "app" }), PipeContract.Json));
        await reader.ReadLineAsync(stop.Token);
        await writer.WriteLineAsync(JsonSerializer.Serialize(Request(2, Commands.Subscribe), PipeContract.Json));
        var states = new List<StateMessage>();
        while (states.Count < 2)
        {
            using var json = JsonDocument.Parse((await reader.ReadLineAsync(stop.Token))!);
            if (json.RootElement.GetProperty("type").GetString() == "state") states.Add(json.RootElement.Deserialize<StateMessage>(PipeContract.Json)!);
        }
        Assert.False(states[0].HistoryIsDelta); Assert.True(states[0].History!.Count >= 3);
        Assert.True(states[1].HistoryIsDelta);
        Assert.All(states[1].History!, s => Assert.True(s.Timestamp > states[0].History![^1].Timestamp));
        Assert.NotEmpty(states[1].History!);
        await client.DisposeAsync(); await session;
    }
}
