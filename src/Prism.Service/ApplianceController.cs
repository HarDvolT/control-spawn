using System.Security.Cryptography;
using System.Text.Json;
using Prism.Core;
using Prism.Core.Ipc;
using Prism.Lighting;

namespace Prism.Service;

/// <summary>Read-only on startup. Heating actions have a separate, expiring, one-use confirmation path.</summary>
public sealed class ApplianceController(Func<DeviceState, CancellationToken, Task<IMiotClient>> factory, IClock? clock = null, string? historyPath = null) : IAsyncDisposable
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly IClock clock = clock ?? new SystemClock();
    private readonly Dictionary<string, (string Nonce, int Siid, int Aiid, TimeSpan Expires)> pending = new();
    private ApplianceState[] states = [];
    private readonly CookingHistory cookingHistory = new(historyPath);
    private readonly Dictionary<string, TimeSpan> lastCalls = new();
    // One open session per fryer: a fresh one costs a miIO hello on every tap. Dropped on any failure so the next call starts clean.
    private readonly Dictionary<string, (IMiotClient Client, string? Ip)> clients = new();
    private readonly Dictionary<string, Dictionary<(int, int), JsonElement>> lastValues = new();
    /// <summary>What a pause, stop or new time changes; one get_properties call instead of two paced ones.</summary>
    private static readonly HashSet<string> Live = ["status", "fault", "target-time", "target-temperature", "left-time", "turn-pot", "turn-pot-config"];
    private async Task<IMiotClient> ClientAsync(DeviceState device, CancellationToken token)
    {
        if (clients.TryGetValue(device.Id, out var open) && open.Ip == device.Ip) return open.Client;
        await DropAsync(device.Id);
        var client = await factory(device, token); clients[device.Id] = (client, device.Ip); return client;
    }
    private async Task DropAsync(string id) { if (clients.Remove(id, out var open)) try { await open.Client.DisposeAsync(); } catch (Exception e) when (e is IOException or ObjectDisposedException or InvalidOperationException) { } }
    private async Task RateLimitAsync(string id, CancellationToken token)
    {
        if (lastCalls.TryGetValue(id, out var last))
        { var delay = TimeSpan.FromMilliseconds(500) - (clock.Elapsed - last); if (delay > TimeSpan.Zero) await clock.DelayAsync(delay, token); }
        lastCalls[id] = clock.Elapsed;
    }
    public IReadOnlyList<ApplianceState> States => Volatile.Read(ref states);
    public string Prepare(DeviceState device, int siid, int aiid)
    {
        var action = Spec(device).Actions.FirstOrDefault(a => a.Siid == siid && a.Aiid == aiid) ?? throw new ArgumentException("Unknown action.");
        if (action.Name is "cancel-cooking" or "pause" || action.Inputs is { Length: > 0 }) throw new ArgumentException("Use a supported cooking action.");
        if (States.FirstOrDefault(s => s.Id == device.Id) is { } now)
        {
            if (!now.Connected) throw new InvalidOperationException("The fryer isn't answering, so it can't start. Check it is plugged in and on Wi-Fi.");
            if (action.Name == "resume-cook" && now.Status != "paused") throw new InvalidOperationException("There is no paused cook to resume.");
            if (action.Name == "start-cook" && now.Status is "cooking" or "preheating" or "paused" or "scheduled") throw new InvalidOperationException("The fryer is already busy. Stop the current cook first.");
        }
        string nonce = Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
        lock (pending) pending[device.Id] = (nonce, siid, aiid, clock.Elapsed + TimeSpan.FromSeconds(30));
        return nonce;
    }
    private static MiotSpec Spec(DeviceState device) => MiotSpec.Load(device.Model ?? "") ?? throw new ArgumentException("Unknown model is read-only.");
    public async Task PollAsync(IEnumerable<DeviceState> devices, CancellationToken token, bool quick = false)
    {
        await gate.WaitAsync(token);
        try
        {
            var result = new List<ApplianceState>();
            foreach (var device in devices.Where(d => d.Kind == DeviceKind.Appliance && d.Configured))
            {
                var spec = MiotSpec.Load(device.Model ?? "");
                if (spec is null)
                {
                    result.Add(new(device.Id, device.Name, device.Model ?? "", false, "unsupported",
                        [new(1, 1, "model", "string", JsonSerializer.SerializeToElement(device.Model)), new(1, 2, "firmware", "string", JsonSerializer.SerializeToElement(device.Firmware))], [], Warning: "No offline spec; metadata only, read-only.")); continue;
                }
                try
                {
                    await RateLimitAsync(device.Id, token);
                    var client = await ClientAsync(device, token);
                    // A quick read refreshes only the live values and keeps the rest from the last full read.
                    var known = lastValues.GetValueOrDefault(device.Id);
                    var read = await client.GetAsync(quick && known is not null ? spec.Properties.Where(p => Live.Contains(p.Name)).ToArray() : spec.Properties, token);
                    var values = quick && known is not null ? new Dictionary<(int, int), JsonElement>(known) : new();
                    foreach (var item in read.EnumerateArray())
                        if (item.TryGetProperty("code", out var code) && code.GetInt32() == 0 && item.TryGetProperty("value", out _)) values[(item.GetProperty("siid").GetInt32(), item.GetProperty("piid").GetInt32())] = item.Clone();
                    lastValues[device.Id] = values;
                    result.Add(MiotMapping.Map(device.Id, device.Name, spec, JsonSerializer.SerializeToElement(values.Values)));
                }
                catch (Exception e) when (!token.IsCancellationRequested && e is IOException or InvalidOperationException or System.Net.Sockets.SocketException or OperationCanceledException or JsonException or KeyNotFoundException)
                {
                    await DropAsync(device.Id);
                    var previous = States.FirstOrDefault(s => s.Id == device.Id);
                    result.Add(previous is null ? MiotMapping.Map(device.Id, device.Name, spec, JsonSerializer.SerializeToElement(Array.Empty<object>())) with { Connected = false, Status = "offline", Warning = "Device unavailable." }
                        : previous with { Connected = false, Warning = previous.Status is "cooking" or "preheating" ? "Contact lost while cooking. The fryer's firmware remains in control." : "Device unavailable." });
                }
            }
            foreach (var gone in clients.Keys.Concat(lastValues.Keys).Except(result.Select(r => r.Id)).ToArray()) { await DropAsync(gone); lastValues.Remove(gone); }
            Volatile.Write(ref states, result.Select(a => a with { History = cookingHistory.Observe(a, DateTimeOffset.UtcNow) }).ToArray());
        }
        finally { gate.Release(); }
    }
    public async Task SetAsync(DeviceState device, JsonElement values, CancellationToken token)
    {
        var spec = Spec(device);
        var parsed = values.EnumerateArray().Select(v => {
            var p = spec.Properties.FirstOrDefault(p => p.Siid == v.GetProperty("siid").GetInt32() && p.Piid == v.GetProperty("piid").GetInt32()) ?? throw new ArgumentException("Unknown property.");
            // Only allow settings known not to trigger heating. Never expose writable status/reservation/recipe-sync.
            if (p.Name is not ("target-temperature" or "target-time" or "mode" or "preheat" or "auto-keep-warm" or "turn-pot-config" or "texture" or "cooking-weight" or "target-cooking-measure")) throw new ArgumentException("Property is read-only in Prism.");
            return (p, p.Clamp(v.GetProperty("value")));
        }).ToArray();
        await gate.WaitAsync(token);
        try { lock (pending) pending.Remove(device.Id); await RateLimitAsync(device.Id, token); await Guarded(device, c => c.SetAsync(parsed, token), token); }
        finally { gate.Release(); }
    }
    public async Task ActAsync(DeviceState device, int siid, int aiid, string? confirmation, CancellationToken token)
    {
        var action = Spec(device).Actions.FirstOrDefault(a => a.Siid == siid && a.Aiid == aiid) ?? throw new ArgumentException("Unknown action.");
        await gate.WaitAsync(token);
        try
        {
            lock (pending)
            {
                if (action.Name is not ("cancel-cooking" or "pause"))
                {
                    if (!pending.Remove(device.Id, out var p) || p.Expires < clock.Elapsed || p.Nonce != confirmation || p.Siid != siid || p.Aiid != aiid)
                        throw new InvalidOperationException("Explicit confirmation required. Prepare the start again.");
                }
                else pending.Remove(device.Id);
            }
            await RateLimitAsync(device.Id, token);
            await Guarded(device, c => c.ActionAsync(action, [], token), token);
        }
        finally { gate.Release(); }
    }
    private async Task Guarded(DeviceState device, Func<IMiotClient, Task> call, CancellationToken token)
    {
        try { await call(await ClientAsync(device, token)); }
        catch { await DropAsync(device.Id); throw; }
    }
    public async ValueTask DisposeAsync() { foreach (var id in clients.Keys.ToArray()) await DropAsync(id); gate.Dispose(); }
}
