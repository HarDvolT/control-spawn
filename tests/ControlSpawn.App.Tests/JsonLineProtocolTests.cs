using System.Text.Json;
using ControlSpawn.App.Services;
using ControlSpawn.Core;
using ControlSpawn.Core.Ipc;

namespace ControlSpawn.App.Tests;

public class JsonLineProtocolTests
{
    private static StateMessage SampleState() => new(
        new DateTimeOffset(2026, 10, 29, 21, 48, 0, TimeSpan.FromHours(1)),
        new SystemState(22.5, new List<double?> { 10, 20, 30 }, 4800, 48.2, 65,
            35, 41.5, 52.5, 2100, 130, 0, 3.1, 16, 14.2, 32,
            new List<double?> { 38.5, 38.3 }, 39,
            new List<DiskState> { new("SN850X", 41, 120, 40, 62) },
            60.4, 8.2),
        new List<FanState>
        {
            new(2, "Radiator", 1268, 44, true, false, "Cpu"),
            new(7, "AIO pump", 2801, 100, false, true, "Fixed 100%")
        },
        FanMode.Standard,
        new LightingState(LightingEffect.Smart, "#FF4D1A", 0.8, 1,
            new List<RgbDeviceState> { new("board", "Motherboard", "Motherboard", true, true, 64) }),
        new LampState("yeelink.light.color5", true, true, 70, "#FF4D1A", "pc", "music mode"),
        new RouterState(true, "5G NSA · n78", "n78", 46, 3, "Fair", "Usable, speed may drop at busy times.",
            -97, 7, -11, -92, 0.5, -13, 21, 0, null),
        new ServiceHealth(true, false, Array.Empty<string>(), false));

    [Fact]
    public void StateMessage_round_trips_through_one_json_line()
    {
        var original = SampleState();
        var line = JsonLineProtocol.Serialize(original);
        Assert.DoesNotContain('\n', line);

        var parsed = JsonLineProtocol.ParseState(line);

        Assert.NotNull(parsed);
        Assert.Equal(original.Timestamp, parsed!.Timestamp);
        Assert.Equal(original.FanMode, parsed.FanMode);
        Assert.Equal(original.System.CpuLoad, parsed.System.CpuLoad);
        Assert.Equal(original.System.CpuCoreLoads.Count, parsed.System.CpuCoreLoads.Count);
        Assert.Equal(original.System.Disks[0].Name, parsed.System.Disks[0].Name);
        Assert.Equal(original.Fans[1].Name, parsed.Fans[1].Name);
        Assert.True(parsed.Fans[1].IsPump);
        Assert.Equal(original.Lighting.Effect, parsed.Lighting.Effect);
        Assert.Equal(original.Lighting.Devices[0].LedCount, parsed.Lighting.Devices[0].LedCount);
        Assert.Equal(original.Lamp!.Brightness, parsed.Lamp!.Brightness);
        Assert.Equal(original.Router!.Verdict, parsed.Router!.Verdict);
        Assert.True(parsed.Health.Mock);
        Assert.Equal("state", parsed.Type);
    }

    [Fact]
    public void Missing_readings_stay_null()
    {
        var state = SampleState() with
        {
            System = SampleState().System with { CpuTemp = null, GpuLoad = null, NetDownMbps = null }
        };
        var parsed = JsonLineProtocol.ParseState(JsonLineProtocol.Serialize(state));

        Assert.NotNull(parsed);
        Assert.Null(parsed!.System.CpuTemp);
        Assert.Null(parsed.System.GpuLoad);
        Assert.Null(parsed.System.NetDownMbps);
    }

    [Fact]
    public void Response_round_trips_with_data_and_error()
    {
        var withData = new Response(7, true, null, JsonSerializer.SerializeToElement(new { hello = "world" }, PipeContract.Json));
        var parsed = JsonLineProtocol.ParseResponse(JsonLineProtocol.Serialize(withData));
        Assert.NotNull(parsed);
        Assert.Equal(7, parsed!.Id);
        Assert.True(parsed.Ok);
        Assert.Equal("world", parsed.Data!.Value.GetProperty("hello").GetString());

        var failure = new Response(8, false, "Service not running");
        var parsedFailure = JsonLineProtocol.ParseResponse(JsonLineProtocol.Serialize(failure));
        Assert.NotNull(parsedFailure);
        Assert.False(parsedFailure!.Ok);
        Assert.Equal("Service not running", parsedFailure.Error);
    }

    [Fact]
    public void Request_serializes_camel_case_args()
    {
        var request = JsonLineProtocol.CreateRequest(3, Commands.SetFanMode, new { group = "Radiator", mode = "Turbo" });
        var line = JsonLineProtocol.Serialize(request);
        using var document = JsonDocument.Parse(line);
        var root = document.RootElement;

        Assert.Equal(3, root.GetProperty("id").GetInt32());
        Assert.Equal("setFanMode", root.GetProperty("command").GetString());
        Assert.Equal("Radiator", root.GetProperty("args").GetProperty("group").GetString());
        Assert.Equal("Turbo", root.GetProperty("args").GetProperty("mode").GetString());
    }

    [Fact]
    public void Reader_reassembles_split_messages_and_strips_crlf()
    {
        var reader = new JsonLineReader();

        Assert.Empty(reader.Append("{\"id\":1,"));
        Assert.Empty(reader.Append("\"command\":\"hello\"}"));
        var first = reader.Append("\n{\"id\":2,\"command\":\"subscribe\"}\r\n");
        Assert.Equal(2, first.Count);
        Assert.Equal("{\"id\":1,\"command\":\"hello\"}", first[0]);
        Assert.Equal("{\"id\":2,\"command\":\"subscribe\"}", first[1]);
        Assert.Equal(string.Empty, reader.Pending);
    }

    [Fact]
    public void Reader_ignores_blank_lines_and_flushes_the_tail()
    {
        var reader = new JsonLineReader();
        var lines = reader.Append("\n\r\n{\"id\":1}\n\n{\"id\":2}");
        Assert.Single(lines);
        Assert.Equal("{\"id\":1}", lines[0]);
        Assert.Equal("{\"id\":2}", Assert.Single(reader.Flush()));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not json")]
    [InlineData("[1,2,3]")]
    [InlineData("{\"type\":\"unknown\"}")]
    public void Malformed_lines_are_rejected_without_throwing(string line)
    {
        Assert.False(JsonLineProtocol.TryParse(line, out var state, out var response));
        Assert.Null(state);
        Assert.Null(response);
    }

    [Fact]
    public void State_and_response_are_told_apart_by_type()
    {
        var stateLine = JsonLineProtocol.Serialize(SampleState());
        var responseLine = JsonLineProtocol.Serialize(new Response(1, true));

        Assert.True(JsonLineProtocol.TryParse(stateLine, out var state, out var response));
        Assert.NotNull(state);
        Assert.Null(response);

        Assert.True(JsonLineProtocol.TryParse(responseLine, out var state2, out var response2));
        Assert.Null(state2);
        Assert.NotNull(response2);
    }
}
