using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
using ControlSpawn.Core.Ipc;
using Xunit;

namespace ControlSpawn.Lighting.Tests;

public sealed class TokenAndMiotTests
{
    private const string Secret = "0123456789abcdef0123456789abcdef";
    public const string TextFixture = "NAME: Room lamp\nID: 470481367\nIP: 192.0.2.11\nTOKEN: " + Secret + "\nMODEL: yeelink.light.color5\n\nNAME: Desk lamp\nID: 470481368\nIP: 192.0.2.12\nTOKEN: " + Secret + "\nMODEL: yeelink.light.color5";
    [Fact]
    public void TextJsonAndCsvProduceBothDecimalLampIdentitiesWithNoSerializedSecret()
    {
        var text = TokenExtractorParser.Parse(TextFixture);
        var json = TokenExtractorParser.Parse("{\"devices\":[{\"name\":\"Room lamp\",\"did\":470481367,\"localip\":\"192.0.2.11\",\"token\":\"" + Secret + "\",\"model\":\"yeelink.light.color5\"},{\"name\":\"Desk lamp\",\"id\":\"470481368\",\"ip\":\"192.0.2.12\",\"token\":\"" + Secret + "\",\"model\":\"yeelink.light.color5\"}]}");
        var csv = TokenExtractorParser.Parse("NAME,ID,IP,TOKEN,MODEL\n\"Room, lamp\",470481367,192.0.2.11," + Secret + ",yeelink.light.color5\nDesk lamp,470481368,192.0.2.12," + Secret + ",yeelink.light.color5\n");
        foreach (var list in new[] { text, json, csv })
        {
            Assert.Equal(new uint[] { 470481367, 470481368 }, list.Select(d => d.DeviceId));
            Assert.All(list, d => { Assert.Equal(Secret, d.Token); Assert.Equal("yeelink.light.color5", d.Model); Assert.DoesNotContain(Secret, d.ToString()); });
            Assert.DoesNotContain(Secret, JsonSerializer.Serialize(list, PipeContract.Json));
        }
        Assert.Equal("Room, lamp", csv[0].Name);
    }
    [Theory]
    [InlineData("{bad json")] [InlineData("NAME: bad\nID: -1\nTOKEN: 0123456789abcdef0123456789abcdef")]
    [InlineData("NAME: bad\nID: 1\nTOKEN: SECRET-NOT-A-TOKEN")]
    public void BadImportsHaveSafeErrors(string input)
    { Assert.DoesNotContain("SECRET", Assert.Throws<ArgumentException>(() => TokenExtractorParser.Parse(input)).Message); }
    [Theory]
    [InlineData("careli.fryer.maf10a", 230, 5)] [InlineData("careli.fryer.maf02", 200, 1)]
    public void ExactOfflineModelSpecClampsAndMaps(string model, int max, int step)
    {
        var spec = MiotSpec.Load(model)!; var temp = spec.Properties.Single(p => p.Name == "target-temperature");
        Assert.Equal(max, temp.Clamp(JsonSerializer.SerializeToElement(1000)).GetInt32());
        Assert.Equal(40, temp.Clamp(JsonSerializer.SerializeToElement(-100)).GetInt32());
        Assert.Equal(step, temp.Range![2]);
        var state = MiotMapping.Map("fixture", "Kitchen", spec, JsonSerializer.SerializeToElement(new[] {
            new { siid = 2, piid = 1, code = 0, value = 4 }, new { siid = 2, piid = 4, code = 0, value = 180 }, new { siid = 2, piid = 5, code = 0, value = 12 } }));
        Assert.Equal("cooking", state.Status); Assert.Equal(180, state.TargetTemperature); Assert.Equal(12, state.RemainingMinutes);
        Assert.Null(state.Temperature); Assert.All(state.Actions.Where(a => a.Name.Contains("start", StringComparison.Ordinal)), a => Assert.True(a.NeedsConfirmation));
        Assert.DoesNotContain("Undefined", JsonSerializer.Serialize(state, PipeContract.Json));
        Assert.Null(MiotSpec.Load(model + ".unknown"));
    }
    [Theory]
    [InlineData(0, "idle")] [InlineData(2, "paused")] [InlineData(5, "preheating")] [InlineData(6, "done")]
    public void MapsStatuses(int status, string expected)
    {
        var reply = JsonSerializer.SerializeToElement(new[] { new { siid = 2, piid = 1, code = 0, value = status } });
        Assert.Equal(expected, MiotMapping.Map("id", "name", MiotSpec.Load("careli.fryer.maf10a")!, reply).Status);
    }
    private sealed class MiotFixtureTransport : IMiioTransport
    {
        public List<JsonElement> Requests { get; } = [];
        public bool FailAction;
        public Task<byte[]> ExchangeAsync(byte[] packet, CancellationToken token)
        {
            if (packet.Length == 32) { var hello = MiioProtocol.Hello(); BinaryPrimitives.WriteUInt32BigEndian(hello.AsSpan(8), 123); BinaryPrimitives.WriteUInt32BigEndian(hello.AsSpan(12), 100); return Task.FromResult(hello); }
            using var doc = JsonDocument.Parse(Encoding.UTF8.GetString(MiioProtocol.Decode(packet, new byte[16])).TrimEnd('\0'));
            var r = doc.RootElement; Requests.Add(r.Clone()); string method = r.GetProperty("method").GetString()!;
            if (method == "action" && FailAction) throw new IOException("Fixture timeout");
            object result = method switch {
                "get_properties" => r.GetProperty("params").EnumerateArray().Select(p => new { siid = p.GetProperty("siid").GetInt32(), piid = p.GetProperty("piid").GetInt32(), code = 0, value = 1 }).ToArray(),
                "set_properties" => new[] { new { code = 0 } },
                "action" => new { code = 0, @out = Array.Empty<int>() }, _ => new { model = "careli.fryer.maf10a", fw_ver = "fixture" } };
            return Task.FromResult(MiioProtocol.Encode(123, 100, new byte[16], JsonSerializer.SerializeToUtf8Bytes(new { id = r.GetProperty("id").GetInt32(), result })));
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
    [Fact]
    public async Task MiotUsesBatchesRangeClampingAndSharedRateLimitAndNeverRetriesActions()
    {
        var clock = new ManualClock(); var wire = new MiotFixtureTransport();
        await using var client = new MiotClient(new MiioLampClient(wire, new byte[16], clock, 123), 123);
        var spec = MiotSpec.Load("careli.fryer.maf10a")!;
        Assert.Equal("careli.fryer.maf10a", (await client.InfoAsync(default)).GetProperty("model").GetString());
        Assert.Equal(spec.Properties.Length, (await client.GetAsync(spec.Properties, default)).GetArrayLength());
        var temp = spec.Properties.Single(p => p.Name == "target-temperature");
        await client.SetAsync([(temp, JsonSerializer.SerializeToElement(999))], default);
        Assert.Equal(230, wire.Requests.Last().GetProperty("params")[0].GetProperty("value").GetInt32());
        await client.ActionAsync(spec.Actions[0], [], default);
        Assert.Equal(JsonValueKind.Object, wire.Requests.Last().GetProperty("params").ValueKind);
        wire.FailAction = true; int before = wire.Requests.Count;
        await Assert.ThrowsAsync<IOException>(() => client.ActionAsync(spec.Actions[0], [], default));
        Assert.Equal(before + 1, wire.Requests.Count); Assert.True(clock.Elapsed.TotalSeconds >= 2.5);
    }
    [Fact]
    public void CloudRecordedEnvelopeAndRc4RoundTripStayOffline()
    {
        using var json = XiaomiTokenProvider.Parse("&&&START&&&{\"notificationUrl\":\"https://account.xiaomi.com/identity/authStart?context=fixture\"}");
        Assert.Equal("account.xiaomi.com", new Uri(json.RootElement.GetProperty("notificationUrl").GetString()!).Host);
        byte[] data = Encoding.UTF8.GetBytes(TextFixture), key = Enumerable.Range(1, 32).Select(i => (byte)i).ToArray();
        Assert.Equal(data, XiaomiTokenProvider.Rc4(key, XiaomiTokenProvider.Rc4(key, data)));
    }
}
