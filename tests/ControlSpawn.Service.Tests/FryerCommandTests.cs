using System.Text.Json;
using ControlSpawn.Core.Ipc;
using ControlSpawn.Router;
using Xunit;

namespace ControlSpawn.Service.Tests;

public sealed class FryerCommandTests
{
    private sealed class Runtime(Exception failure) : IServiceRuntime
    {
        public StateMessage GetState(ServiceSettings settings) => StateAssembler.Assemble(new(DateTimeOffset.UnixEpoch, []), settings, [], [], null, null, new(true, false, [], false));
        public Task ApplyAsync(ServiceSettings settings, CancellationToken token) => Task.CompletedTask;
        public Task<SpeedTestResult?> StartSpeedTestAsync(CancellationToken token) => Task.FromResult<SpeedTestResult?>(null);
        public Task ActApplianceAsync(DeviceState device, int siid, int aiid, string? nonce, CancellationToken token) => Task.FromException(failure);
    }
    private static readonly DeviceState Fryer = new("lamp:685455924", DeviceKind.Appliance, "Air Fryer", "miio", DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, DeviceStatus.Ready, [], Model: "careli.fryer.maf10a", DeviceId: 685455924, Configured: true);

    /// <summary>A pause the fryer didn't answer used to say "Settings could not be saved." (2026-11-03).</summary>
    [Theory]
    [InlineData("timeout", "The fryer didn't answer. Check it is on and try again.")]
    [InlineData("Device rejected the action.", "The fryer refused that. It may not allow it right now.")]
    public async Task AFryerThatFailsSaysSoInsteadOfBlamingSettings(string failure, string expected)
    {
        var store = new MemorySettingsStore(); store.Save(new() { Devices = [Fryer] });
        var processor = new CommandProcessor(store, new Runtime(failure == "timeout" ? new OperationCanceledException() : new IOException(failure)));
        var reply = await processor.HandleAsync(new(1, Commands.ApplianceAction, JsonSerializer.SerializeToElement(new { id = Fryer.Id, siid = 2, aiid = 3 })));
        Assert.False(reply.Ok);
        Assert.Equal(expected, reply.Error);
    }
}
