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
