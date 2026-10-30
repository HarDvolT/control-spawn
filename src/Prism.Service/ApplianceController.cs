using System.Security.Cryptography;
using System.Text.Json;
using Prism.Core;
using Prism.Core.Ipc;
using Prism.Lighting;

namespace Prism.Service;

/// <summary>Read-only on startup. Heating actions have a separate, expiring, one-use confirmation path.</summary>
public sealed class ApplianceController(Func<DeviceState, CancellationToken, Task<IMiotClient>> factory, IClock? clock = null) : IAsyncDisposable
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly IClock clock = clock ?? new SystemClock();
    private readonly Dictionary<string, (string Nonce, int Siid, int Aiid, TimeSpan Expires)> pending = new();
    private ApplianceState[] states = [];
    private readonly CookingHistory cookingHistory = new();
    private readonly Dictionary<string, TimeSpan> lastCalls = new();
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
        string nonce = Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
        lock (pending) pending[device.Id] = (nonce, siid, aiid, clock.Elapsed + TimeSpan.FromSeconds(30));
        return nonce;
    }
    private static MiotSpec Spec(DeviceState device) => MiotSpec.Load(device.Model ?? "") ?? throw new ArgumentException("Unknown model is read-only.");
    public async Task PollAsync(IEnumerable<DeviceState> devices, CancellationToken token)
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
                    await using var client = await factory(device, token);
                    result.Add(MiotMapping.Map(device.Id, device.Name, spec, await client.GetAsync(spec.Properties, token)));
                }
                catch (Exception e) when (!token.IsCancellationRequested && e is IOException or InvalidOperationException or System.Net.Sockets.SocketException or OperationCanceledException or JsonException or KeyNotFoundException)
                {
                    var previous = States.FirstOrDefault(s => s.Id == device.Id);
                    result.Add(previous is null ? MiotMapping.Map(device.Id, device.Name, spec, JsonSerializer.SerializeToElement(Array.Empty<object>())) with { Connected = false, Status = "offline", Warning = "Device unavailable." }
                        : previous with { Connected = false, Warning = previous.Status is "cooking" or "preheating" ? "Contact lost while cooking. The fryer's firmware remains in control." : "Device unavailable." });
                }
            }
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
        try { lock (pending) pending.Remove(device.Id); await RateLimitAsync(device.Id, token); await using var client = await factory(device, token); await client.SetAsync(parsed, token); }
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
            await using var client = await factory(device, token); await client.ActionAsync(action, [], token);
        }
        finally { gate.Release(); }
    }
    public ValueTask DisposeAsync() { gate.Dispose(); return ValueTask.CompletedTask; }
}
