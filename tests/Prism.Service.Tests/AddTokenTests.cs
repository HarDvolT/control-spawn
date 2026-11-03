using System.Text.Json;
using Prism.Core.Ipc;
using Prism.Lighting;
using Prism.Router;
using Xunit;

namespace Prism.Service.Tests;

public sealed class AddTokenTests
{
    private sealed class Runtime(Func<string, (uint, string)> identify) : IServiceRuntime
    {
        public StateMessage GetState(ServiceSettings settings) => StateAssembler.Assemble(new(DateTimeOffset.UnixEpoch, []), settings, [], [], null, null, new(true, false, [], false));
        public Task ApplyAsync(ServiceSettings settings, CancellationToken token) => Task.CompletedTask;
        public Task<SpeedTestResult?> StartSpeedTestAsync(CancellationToken token) => Task.FromResult<SpeedTestResult?>(null);
        public Task<(uint DeviceId, string Model)> IdentifyMiioAsync(string ip, byte[] token, CancellationToken cancellationToken)
        {
            try { return Task.FromResult(identify(ip)); } catch (Exception e) { return Task.FromException<(uint, string)>(e); }
        }
    }
    private const string Token = "0123456789abcdef0123456789abcdef";
    private static readonly DeviceState Fryer = new("lamp:685455924", DeviceKind.Lamp, "Air Fryer", "miio", DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, DeviceStatus.NeedsToken, [], DeviceId: 685455924, Ip: "192.0.2.83", Dismissed: true);
    private static (CommandProcessor Processor, Dictionary<uint, byte[]> Saved) Make(Func<string, (uint, string)> identify)
    {
        var store = new MemorySettingsStore(); store.Save(new() { Devices = [Fryer] });
        var saved = new Dictionary<uint, byte[]>();
        return (new CommandProcessor(store, new Runtime(identify), saveDeviceToken: (id, bytes) => saved[id] = bytes.ToArray()), saved);
    }
    private static Request Command(object args) => new(1, Commands.AddToken, JsonSerializer.SerializeToElement(args, PipeContract.Json));

    /// <summary>The dismissed fryer could only take a token through a five-step import that refused a bare token (2026-11-03).</summary>
    [Fact]
    public async Task APastedTokenIsCheckedOnTheDeviceAndAddsTheFryer()
    {
        var (processor, saved) = Make(ip => (685455924, "careli.fryer.maf10a"));
        var reply = await processor.HandleAsync(Command(new { token = "01234567 89abcdef-0123456789abcdef", id = Fryer.Id }));
        Assert.True(reply.Ok, reply.Error);
        var fryer = processor.Settings.Devices.Single(d => d.Id == Fryer.Id);
        Assert.Equal((DeviceKind.Appliance, DeviceStatus.Ready, true, false, "careli.fryer.maf10a"), (fryer.Kind, fryer.Status, fryer.Configured, fryer.Dismissed, fryer.Model));
        Assert.Equal("Air Fryer", fryer.Name);
        Assert.Equal(Convert.FromHexString(Token), saved[685455924]);
    }

    [Theory]
    [InlineData("0123", "This one has 4")]
    [InlineData("0123456789abcdef0123456789abcdeg", "0-9 and a-f")]
    public async Task AMistypedTokenSaysWhatIsWrong(string token, string reason)
    {
        var (processor, saved) = Make(_ => throw new Exception("never asked"));
        var reply = await processor.HandleAsync(Command(new { token, id = Fryer.Id }));
        Assert.False(reply.Ok); Assert.Contains(reason, reply.Error); Assert.Empty(saved);
    }

    [Fact]
    public async Task ATokenTheDeviceRefusesIsNotSavedAndTheReasonReachesTheApp()
    {
        var (processor, saved) = Make(ip => throw new TokenProblem($"The device at {ip} answered but didn't accept this token."));
        var reply = await processor.HandleAsync(Command(new { token = Token, id = Fryer.Id }));
        Assert.Equal("The device at 192.0.2.83 answered but didn't accept this token.", reply.Error);
        Assert.Empty(saved); Assert.False(processor.Settings.Devices.Single().Configured);
        Assert.DoesNotContain(Token, reply.Error);
    }

    [Fact]
    public async Task ADeviceTypedInByIpIsAddedUnderTheIdItReports()
    {
        var (processor, _) = Make(ip => ip == "192.0.2.50" ? (470124183u, "yeelink.light.color5") : throw new TokenProblem("Nothing answered."));
        Assert.True((await processor.HandleAsync(Command(new { token = Token, ip = "192.0.2.50" }))).Ok);
        Assert.Contains(processor.Settings.ConfiguredLamps, l => l.DeviceId == 470124183 && l.Name == "Yeelight bulb");
        Assert.False((await processor.HandleAsync(Command(new { token = Token, ip = "not an ip" }))).Ok);
    }
}
