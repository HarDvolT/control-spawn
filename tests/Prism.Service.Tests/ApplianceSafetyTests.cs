using System.Text.Json;
using Prism.Core;
using Prism.Core.Ipc;
using Prism.Lighting;
using Xunit;

namespace Prism.Service.Tests;

public sealed class ApplianceSafetyTests
{
    private sealed class Clock : IClock
    { public TimeSpan Elapsed { get; set; } public Task DelayAsync(TimeSpan d, CancellationToken t) { Elapsed += d; return Task.CompletedTask; } }
    private sealed class Client : IMiotClient
    {
        public List<string> Writes { get; } = [];
        public bool Offline;
        public Task<JsonElement> InfoAsync(CancellationToken t) => Task.FromResult(JsonSerializer.SerializeToElement(new { model = "careli.fryer.maf10a" }));
        public Task<JsonElement> GetAsync(IReadOnlyList<MiotPropertySpec> p, CancellationToken t) => Offline ? Task.FromException<JsonElement>(new IOException()) : Task.FromResult(JsonSerializer.SerializeToElement(new[] { new { siid = 2, piid = 1, code = 0, value = 4 } }));
        public Task SetAsync(IReadOnlyList<(MiotPropertySpec Property, JsonElement Value)> values, CancellationToken t) { Writes.AddRange(values.Select(v => v.Property.Name + ":" + v.Value)); return Task.CompletedTask; }
        public Task ActionAsync(MiotActionSpec action, IReadOnlyList<JsonElement> inputs, CancellationToken t) { Writes.Add(action.Name); return Task.CompletedTask; }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
    private static DeviceState Fryer => new("fryer", DeviceKind.Appliance, "Kitchen", "miio", DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, DeviceStatus.Ready, [], Model: "careli.fryer.maf10a", Configured: true);
    private sealed class Reader(Func<int, object[]> reply) : IMiotClient
    {
        public List<int> Reads { get; } = [];
        public int Opened, Closed;
        public Task<JsonElement> InfoAsync(CancellationToken t) => throw new NotSupportedException();
        public Task<JsonElement> GetAsync(IReadOnlyList<MiotPropertySpec> p, CancellationToken t) { Reads.Add(p.Count); return Task.FromResult(JsonSerializer.SerializeToElement(reply(Reads.Count))); }
        public Task SetAsync(IReadOnlyList<(MiotPropertySpec Property, JsonElement Value)> values, CancellationToken t) => Task.CompletedTask;
        public Task ActionAsync(MiotActionSpec action, IReadOnlyList<JsonElement> inputs, CancellationToken t) => Task.CompletedTask;
        public ValueTask DisposeAsync() { Closed++; return ValueTask.CompletedTask; }
    }
    [Fact]
    public async Task AQuickReadAsksOnlyForLiveValuesOverOneKeptSession()
    {
        // Read 1 is full (cooking, keep-warm on); read 2 is quick and reports paused without keep-warm.
        var reader = new Reader(n => n == 1 ? [new { siid = 2, piid = 1, code = 0, value = 4 }, new { siid = 2, piid = 6, code = 0, value = (object)true }] : [new { siid = 2, piid = 1, code = 0, value = 2 }]);
        await using var controller = new ApplianceController((_, _) => { reader.Opened++; return Task.FromResult<IMiotClient>(reader); }, new Clock());
        await controller.PollAsync([Fryer], default);
        await controller.PollAsync([Fryer], default, quick: true);
        Assert.Equal(1, reader.Opened);
        Assert.True(reader.Reads[1] < reader.Reads[0]);
        Assert.Equal("paused", controller.States[0].Status);
        Assert.Equal(JsonValueKind.True, controller.States[0].Properties.Single(p => p.Name == "auto-keep-warm").Value?.ValueKind);
    }
    [Fact]
    public async Task StartIsRefusedInWordsWhenTheFryerIsBusyOrOffline()
    {
        var client = new Client(); await using var controller = new ApplianceController((_, _) => Task.FromResult<IMiotClient>(client));
        await controller.PollAsync([Fryer], default); // cooking
        Assert.Contains("already busy", Assert.Throws<InvalidOperationException>(() => controller.Prepare(Fryer, 2, 1)).Message);
        Assert.Contains("no paused cook", Assert.Throws<InvalidOperationException>(() => controller.Prepare(Fryer, 2, 4)).Message);
        client.Offline = true; await controller.PollAsync([Fryer], default);
        Assert.Contains("isn't answering", Assert.Throws<InvalidOperationException>(() => controller.Prepare(Fryer, 2, 1)).Message);
    }
    [Fact]
    public async Task ARemovedFryerGivesBackItsSession()
    {
        var reader = new Reader(_ => [new { siid = 2, piid = 1, code = 0, value = 1 }]);
        await using var controller = new ApplianceController((_, _) => Task.FromResult<IMiotClient>(reader), new Clock());
        await controller.PollAsync([Fryer], default);
        await controller.PollAsync([], default);
        Assert.Equal(1, reader.Closed);
        Assert.Empty(controller.States);
    }
    [Fact]
    public void RecentCooksSurviveARestartAndKeepTheTimeActuallyCooked()
    {
        var path = Path.Combine(Path.GetTempPath(), $"prism-cooks-{Guid.NewGuid():N}.json");
        try
        {
            var state = new ApplianceState("fryer", "Fryer", "model", true, "cooking", [], [], TargetTemperature: 180, TargetMinutes: 15, RemainingMinutes: 15);
            var now = DateTimeOffset.UnixEpoch;
            var history = new CookingHistory(path);
            history.Observe(state, now);
            history.Observe(state with { TargetMinutes = 20, RemainingMinutes = 1 }, now.AddMinutes(19));
            history.Observe(state with { Status = "idle", RemainingMinutes = 0 }, now.AddMinutes(20));
            var cook = Assert.Single(new CookingHistory(path).Observe(state with { Status = "idle" }, now.AddMinutes(21)));
            Assert.Equal((20d, "Finished"), (cook.TargetMinutes!.Value, cook.Outcome));
        }
        finally { File.Delete(path); }
    }
    [Fact]
    public void AStopThatZeroesTheTimerIsNotAFinishedCook()
    {
        var history = new CookingHistory();
        var state = new ApplianceState("fryer", "Fryer", "model", true, "cooking", [], [], TargetTemperature: 180, TargetMinutes: 15, RemainingMinutes: 12);
        history.Observe(state, DateTimeOffset.UnixEpoch);
        Assert.Equal("Ended", Assert.Single(history.Observe(state with { Status = "idle", RemainingMinutes = 0 }, DateTimeOffset.UnixEpoch.AddMinutes(3))).Outcome);
    }
    [Fact]
    public async Task StartupPollIsReadOnlyAndContactLossLeavesFirmwareInControl()
    {
        var client = new Client(); await using var controller = new ApplianceController((_, _) => Task.FromResult<IMiotClient>(client));
        await controller.PollAsync([Fryer], default); Assert.Empty(client.Writes); Assert.Equal("cooking", Assert.Single(controller.States).Status);
        client.Offline = true; await controller.PollAsync([Fryer], default); Assert.Empty(client.Writes); Assert.False(controller.States[0].Connected); Assert.Contains("firmware", controller.States[0].Warning);
    }
    [Fact]
    public async Task StartRequiresSingleUseConfirmationExpiresAndCancelNeedsNone()
    {
        var clock = new Clock(); var client = new Client(); await using var controller = new ApplianceController((_, _) => Task.FromResult<IMiotClient>(client), clock);
        await Assert.ThrowsAsync<InvalidOperationException>(() => controller.ActAsync(Fryer, 2, 1, null, default)); Assert.Empty(client.Writes);
        var nonce = controller.Prepare(Fryer, 2, 1); await controller.ActAsync(Fryer, 2, 1, nonce, default);
        await Assert.ThrowsAsync<InvalidOperationException>(() => controller.ActAsync(Fryer, 2, 1, nonce, default)); Assert.Single(client.Writes);
        nonce = controller.Prepare(Fryer, 2, 4); clock.Elapsed += TimeSpan.FromSeconds(31);
        await Assert.ThrowsAsync<InvalidOperationException>(() => controller.ActAsync(Fryer, 2, 4, nonce, default));
        await controller.ActAsync(Fryer, 2, 2, null, default); Assert.Equal("cancel-cooking", client.Writes.Last());
    }
    [Fact]
    public async Task PropertiesClampAndInvalidateConfirmationAndCannotWriteHeatingTriggers()
    {
        var client = new Client(); await using var controller = new ApplianceController((_, _) => Task.FromResult<IMiotClient>(client));
        var nonce = controller.Prepare(Fryer, 2, 1);
        await controller.SetAsync(Fryer, JsonSerializer.SerializeToElement(new[] { new { siid = 2, piid = 4, value = 999 }, new { siid = 2, piid = 3, value = -10 } }), default);
        Assert.Equal(new[] { "target-temperature:230", "target-time:1" }, client.Writes);
        await Assert.ThrowsAsync<InvalidOperationException>(() => controller.ActAsync(Fryer, 2, 1, nonce, default));
        foreach (int piid in new[] { 1, 10, 12, 17 }) await Assert.ThrowsAsync<ArgumentException>(() => controller.SetAsync(Fryer, JsonSerializer.SerializeToElement(new[] { new { siid = 2, piid, value = 1 } }), default));
        Assert.Throws<ArgumentException>(() => controller.Prepare(Fryer, 2, 5));
        await Assert.ThrowsAsync<ArgumentException>(() => controller.ActAsync(Fryer with { Model = "unknown.fryer.model" }, 2, 1, null, default));
    }
}
