using System.Text.Json;
using ControlSpawn.Core;
using ControlSpawn.Core.Ipc;

namespace ControlSpawn.Lighting;

public sealed record MiotPropertySpec(int Siid, int Piid, string Name, string Format, bool Readable = true,
    bool Writable = false, double[]? Range = null, Dictionary<int, string>? Values = null, string? Unit = null)
{
    public JsonElement Clamp(JsonElement value)
    {
        if (!Writable) throw new ArgumentException("Read-only property.");
        if (Format == "bool") return JsonSerializer.SerializeToElement(value.GetBoolean());
        if (Format == "string") return JsonSerializer.SerializeToElement(value.GetString());
        double number = value.GetDouble();
        if (!double.IsFinite(number)) throw new ArgumentException("Invalid property value.");
        if (Values is not null && (!Values.ContainsKey((int)number) || number != (int)number)) throw new ArgumentException("Unknown enumeration value.");
        if (Range is { Length: 3 }) number = Math.Clamp(Range[0] + Math.Round((Math.Clamp(number, Range[0], Range[1]) - Range[0]) / Range[2]) * Range[2], Range[0], Range[1]);
        return Format is "float" or "double" ? JsonSerializer.SerializeToElement(number) : JsonSerializer.SerializeToElement((int)number);
    }
}
public sealed record MiotActionSpec(int Siid, int Aiid, string Name, int[]? Inputs = null);
public sealed record MiotSpec(string Model, string Urn, string Source, MiotPropertySpec[] Properties, MiotActionSpec[] Actions)
{
    public static MiotSpec? Load(string model)
    {
        // An exact model lookup only: never guess another heater's IDs/ranges.
        var assembly = typeof(MiotSpec).Assembly;
        using var stream = assembly.GetManifestResourceStream($"ControlSpawn.Lighting.Specs.{model}.json");
        return stream is null ? null : JsonSerializer.Deserialize<MiotSpec>(stream, PipeContract.Json);
    }
}
public interface IMiotClient : IAsyncDisposable
{
    Task<JsonElement> InfoAsync(CancellationToken token);
    Task<JsonElement> GetAsync(IReadOnlyList<MiotPropertySpec> properties, CancellationToken token);
    Task SetAsync(IReadOnlyList<(MiotPropertySpec Property, JsonElement Value)> values, CancellationToken token);
    Task ActionAsync(MiotActionSpec action, IReadOnlyList<JsonElement> inputs, CancellationToken token);
}
public sealed class MiotClient(MiioLampClient transport, uint deviceId) : IMiotClient
{
    public Task<JsonElement> InfoAsync(CancellationToken token) => transport.CallAsync("miIO.info", Array.Empty<object>(), token);
    public async Task<JsonElement> GetAsync(IReadOnlyList<MiotPropertySpec> properties, CancellationToken token)
    {
        var result = new List<JsonElement>();
        foreach (var batch in properties.Where(p => p.Readable).Chunk(10))
        {
            var reply = await transport.CallAsync("get_properties", batch.Select(p => new { did = deviceId.ToString(), siid = p.Siid, piid = p.Piid }).ToArray(), token);
            foreach (var value in reply.EnumerateArray()) result.Add(value.Clone());
        }
        return JsonSerializer.SerializeToElement(result);
    }
    public async Task SetAsync(IReadOnlyList<(MiotPropertySpec Property, JsonElement Value)> values, CancellationToken token)
    {
        foreach (var batch in values.Chunk(10))
        {
            var reply = await transport.CallAsync("set_properties", batch.Select(v => new { did = deviceId.ToString(), siid = v.Property.Siid, piid = v.Property.Piid, value = v.Property.Clamp(v.Value) }).ToArray(), token);
            foreach (var item in reply.EnumerateArray()) if (item.GetProperty("code").GetInt32() != 0) throw new IOException("Device rejected a property.");
        }
    }
    public async Task ActionAsync(MiotActionSpec action, IReadOnlyList<JsonElement> inputs, CancellationToken token)
    {
        // Actions are not idempotent. An ambiguous timeout must never repeat a heating action.
        var reply = await transport.CallAsync("action", new { did = deviceId.ToString(), siid = action.Siid, aiid = action.Aiid, @in = inputs }, token, retry: false);
        if (reply.GetProperty("code").GetInt32() != 0) throw new IOException("Device rejected the action.");
    }
    public ValueTask DisposeAsync() => transport.DisposeAsync();
}

public static class MiotMapping
{
    public static ApplianceState Map(string id, string name, MiotSpec spec, JsonElement reply)
    {
        var values = reply.EnumerateArray().Where(p => p.TryGetProperty("code", out var c) && c.GetInt32() == 0 && p.TryGetProperty("value", out _))
            .GroupBy(p => (p.GetProperty("siid").GetInt32(), p.GetProperty("piid").GetInt32())).ToDictionary(g => g.Key, g => g.Last().GetProperty("value").Clone());
        double? Number(string key) => spec.Properties.FirstOrDefault(p => p.Name == key) is { } p && values.TryGetValue((p.Siid, p.Piid), out var v) && v.TryGetDouble(out var n) ? n : null;
        string status = Number("fault") > 0 ? "fault" : Number("status") switch { 0 or 1 => "idle", 2 or 8 or 9 or 11 => "paused", 4 or 10 or 13 or 14 => "cooking", 5 => "preheating", 6 or 7 or 12 => "done", 3 => "scheduled", _ => "unknown" };
        return new(id, name, spec.Model, true, status,
            spec.Properties.Select(p => new ApplianceProperty(p.Siid, p.Piid, p.Name, p.Format, values.TryGetValue((p.Siid, p.Piid), out var value) ? value : null, p.Writable, p.Range, p.Values, p.Unit)).ToArray(),
            spec.Actions.Select(a => new ApplianceActionState(a.Siid, a.Aiid, a.Name, a.Name is not ("cancel-cooking" or "pause"))).ToArray(),
            Number("temperature"), Number("target-temperature"), Number("target-time"), Number("left-time"), Number("turn-pot") == 2);
    }
}
