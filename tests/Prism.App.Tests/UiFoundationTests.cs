using Prism.App.Controls;
using Prism.App.Services;
using Prism.App.ViewModels;
using Prism.Core.Ipc;

namespace Prism.App.Tests;

public sealed class UiFoundationTests
{
    [Fact]
    public async Task BurstSendsLeadingAndFinalValuesAndCompletesEveryCaller()
    {
        var sent = new List<int>();
        var queue = new LatestCommandQueue((_, args, _) => { lock (sent) sent.Add((int)args!); return Task.FromResult(new Response(1, true)); });
        var calls = Enumerable.Range(0, 100).Select(i => queue.SendAsync("slider", "set", i)).ToArray();
        await Task.WhenAll(calls).WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal(new[] { 0, 99 }, sent);
        Assert.All(calls, t => Assert.True(t.Result.Ok));
    }

    [Fact]
    public async Task FinalValueArrivingDuringSlowSendIsDeliveredWithoutOverlap()
    {
        var first = new TaskCompletionSource<Response>(TaskCreationOptions.RunContinuationsAsynchronously);
        var values = new List<int>();
        var queue = new LatestCommandQueue((_, a, _) => { values.Add((int)a!); return values.Count == 1 ? first.Task : Task.FromResult(new Response(2, true)); });
        var leading = queue.SendAsync("lamp", "set", 1);
        var trailing = queue.SendAsync("lamp", "set", 2);
        Assert.Single(values);
        first.SetResult(new(1, true));
        await Task.WhenAll(leading, trailing).WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal(new[] { 1, 2 }, values);
    }

    [Fact]
    public async Task LanesAreIndependentAndCancelledWaiterDoesNotLoseFinalValue()
    {
        var blocked = new TaskCompletionSource<Response>(TaskCreationOptions.RunContinuationsAsynchronously);
        var sent = new List<string>();
        var queue = new LatestCommandQueue((cmd, _, _) => { sent.Add(cmd); return cmd == "slow" ? blocked.Task : Task.FromResult(new Response(1, true)); });
        var slow = queue.SendAsync("one", "slow", null);
        using var cancel = new CancellationTokenSource();
        var trailing = queue.SendAsync("one", "final", null, cancel.Token); cancel.Cancel();
        Assert.True((await queue.SendAsync("two", "independent", null)).Ok);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => trailing);
        blocked.SetResult(new(1, true)); await slow;
        await Task.Delay(80);
        Assert.Contains("final", sent);
    }

    private sealed class RejectingClient : PrismClientBase
    {
        public TaskCompletionSource<Response> Result { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override Task StartAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public override Task<Response> SendAsync(string command, object? args = null, CancellationToken cancellationToken = default) => Result.Task;
        public override ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    [Fact]
    public async Task OptimisticLightingSurvivesStaleStateAndRollsBackOnlyOnRejection()
    {
        await using var mock = new MockPrismClient(); await using var client = new RejectingClient();
        var vm = new LightingViewModel(client); vm.Apply(mock.CurrentState);
        double original = vm.BrightnessPercent;
        vm.BrightnessPercent = 23;
        Assert.Equal(.23, vm.Brightness, 3);
        vm.Apply(mock.CurrentState);
        Assert.Equal(23, vm.BrightnessPercent);
        client.Result.SetResult(new(1, false, "Rejected"));
        for (int i = 0; i < 50 && !vm.HasError; i++) await Task.Delay(10);
        Assert.True(vm.HasError); Assert.Equal(original, vm.BrightnessPercent);
    }

    [Fact]
    public async Task WholeCaseMockUpdatesBothLampsAndPreservesLayersUnlessReset()
    {
        await using var client = new MockPrismClient();
        await client.SendAsync(Commands.SetLedColors, new { device = "ram", layer = "custom", colors = Enumerable.Repeat("#112233", 10).ToArray() });
        await client.SendAsync(Commands.SetAllLighting, new { color = "#123456", brightness = .4 });
        Assert.All(client.CurrentState.Lamps!, l => { Assert.Equal("pc", l.Follow); Assert.Equal(40, l.Brightness); Assert.Equal("#123456", l.Color); });
        Assert.All(client.CurrentState.Lighting.Devices, d => Assert.True(d.Synced));
        Assert.NotNull(client.CurrentState.Lighting.Devices.Single(d => d.Id == "ram").CustomColors);
        await client.SendAsync(Commands.SetAllLighting, new { resetOverrides = true });
        Assert.Null(client.CurrentState.Lighting.Devices.Single(d => d.Id == "ram").CustomColors);
    }

    [Theory]
    [InlineData(SensorKind.CpuTctl, 75, StatusLevel.Warm)]
    [InlineData(SensorKind.CpuTctl, 85, StatusLevel.Hot)]
    [InlineData(SensorKind.GpuHotspot, 100, StatusLevel.Hot)]
    [InlineData(SensorKind.Ping, 250, StatusLevel.Critical)]
    public void ThresholdBoundariesUseSharedStatus(SensorKind kind, double value, StatusLevel expected) => Assert.Equal(expected, Thresholds.Level(value, kind));
}
