using System.Net;
using System.Text;
using System.Text.Json;
using ControlSpawn.Core;

namespace ControlSpawn.Lighting;

public static class YeelightMessages
{
    public static byte[] Encode(long id, string method, params object[] parameters) =>
        Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { id, method, @params = parameters }) + "\r\n");
    public static object[] Power(bool on) => new object[] { on ? "on" : "off", "smooth", 100 };
    public static object[] Color(RgbColor color) => new object[] { color.Packed, "smooth", 100 };
    public static object[] Brightness(int brightness) => brightness is >= 1 and <= 100
        ? new object[] { brightness, "smooth", 100 } : throw new ArgumentOutOfRangeException(nameof(brightness));
    public static object[] ColorTemperature(int kelvin) => kelvin is >= 1700 and <= 6500
        ? new object[] { kelvin, "smooth", 100 } : throw new ArgumentOutOfRangeException(nameof(kelvin));
}

/// <summary>Serial callers use a sliding window of 60 commands and at least one second spacing.</summary>
public sealed class YeelightRateLimiter(IClock clock)
{
    private readonly Queue<TimeSpan> sent = new();
    private readonly SemaphoreSlim gate = new(1, 1);
    public async Task WaitAsync(CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var now = clock.Elapsed;
                while (sent.TryPeek(out var oldest) && now - oldest >= TimeSpan.FromMinutes(1)) sent.Dequeue();
                TimeSpan due = sent.Count == 0 ? now : sent.Last() + TimeSpan.FromSeconds(1);
                if (sent.Count >= 60 && sent.Peek() + TimeSpan.FromMinutes(1) > due) due = sent.Peek() + TimeSpan.FromMinutes(1);
                if (due <= now) { sent.Enqueue(now); return; }
                await clock.DelayAsync(due - now, cancellationToken).ConfigureAwait(false);
            }
        }
        finally { gate.Release(); }
    }
}

public sealed class YeelightClient(IConnectionFactory connections, IClock clock, IMusicListener listener) : ILampClient
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly YeelightRateLimiter limiter = new(clock);
    private Stream? control;
    private StreamReader? reader;
    private Stream? music;
    private long id;
    public bool IsMusicMode => music is not null;
    public async Task ConnectAsync(string host, int port = 55443, CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            if (control is not null) throw new InvalidOperationException("Already connected.");
            control = await connections.ConnectAsync(host, port, cancellationToken);
            reader = new StreamReader(control, Encoding.UTF8, false, 1024, leaveOpen: true);
        }
        finally { gate.Release(); }
    }
    public Task SetPowerAsync(bool on, CancellationToken cancellationToken = default) => CommandAsync("set_power", YeelightMessages.Power(on), cancellationToken);
    public Task SetColorAsync(RgbColor color, CancellationToken cancellationToken = default) => CommandAsync("set_rgb", YeelightMessages.Color(color), cancellationToken);
    public Task SetBrightnessAsync(int brightness, CancellationToken cancellationToken = default) => CommandAsync("set_bright", YeelightMessages.Brightness(brightness), cancellationToken);
    public Task SetColorTemperatureAsync(int kelvin, CancellationToken cancellationToken = default) => CommandAsync("set_ct_abx", YeelightMessages.ColorTemperature(kelvin), cancellationToken);
    public Task StartColorFlowAsync(int count, int action, IReadOnlyList<YeelightFlowStep> steps, CancellationToken cancellationToken = default)
    {
        if (count < 0 || action is < 0 or > 2 || steps.Count == 0) throw new ArgumentOutOfRangeException(nameof(count));
        return CommandAsync("start_cf", new object[] { count, action, string.Join(",", steps.Select(s => s.Format())) }, cancellationToken);
    }
    private async Task CommandAsync(string method, object[] parameters, CancellationToken token)
    {
        await gate.WaitAsync(token);
        try { await SendLockedAsync(method, parameters, false, token); }
        finally { gate.Release(); }
    }
    private async Task SendLockedAsync(string method, object[] parameters, bool forceControl, CancellationToken token)
    {
        var stream = !forceControl && music is not null ? music : control;
        if (stream is null) throw new InvalidOperationException("Not connected.");
        bool normal = ReferenceEquals(stream, control);
        if (normal) await limiter.WaitAsync(token);
        long requestId = ++id;
        try
        {
            using var writeDeadline = CancellationTokenSource.CreateLinkedTokenSource(token); writeDeadline.CancelAfter(TimeSpan.FromSeconds(5));
            await stream.WriteAsync(YeelightMessages.Encode(requestId, method, parameters), writeDeadline.Token);
            if (normal) await ReadResponseAsync(requestId, token);
        }
        catch
        {
            // A partial write or timed-out response makes framing uncertain. Require reconnection.
            if (music is not null) await music.DisposeAsync(); music = null;
            reader?.Dispose(); reader = null;
            if (control is not null) await control.DisposeAsync(); control = null;
            throw;
        }
    }
    private async Task ReadResponseAsync(long requestId, CancellationToken token)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token); deadline.CancelAfter(TimeSpan.FromSeconds(5));
        while (true)
        {
            string? line = await reader!.ReadLineAsync(deadline.Token);
            if (line is null) throw new EndOfStreamException("Bulb closed connection.");
            if (line.Length > 65536) throw new InvalidDataException("Oversized bulb response.");
            using var json = JsonDocument.Parse(line); var root = json.RootElement;
            if (!root.TryGetProperty("id", out var responseId)) continue; // property notification
            if (responseId.GetInt64() != requestId) throw new InvalidDataException("Unexpected bulb response ID.");
            if (root.TryGetProperty("error", out var error)) throw new IOException($"Bulb rejected command: {error}");
            if (!root.TryGetProperty("result", out _)) throw new InvalidDataException("Missing bulb result.");
            return;
        }
    }
    public async Task StartMusicModeAsync(IPAddress localAddress, IPAddress bulbAddress, CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            if (music is not null) throw new InvalidOperationException("Music mode already active.");
            listener.Start(localAddress);
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken); deadline.CancelAfter(TimeSpan.FromSeconds(10));
            try
            {
                await SendLockedAsync("set_music", new object[] { 1, localAddress.ToString(), listener.Port }, true, deadline.Token);
                music = await listener.AcceptAsync(bulbAddress, deadline.Token);
            }
            catch
            {
                if (control is not null)
                {
                    using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                    try { await SendLockedAsync("set_music", new object[] { 0 }, true, cleanup.Token); } catch { }
                }
                throw;
            }
            finally { await listener.DisposeAsync(); }
        }
        finally { gate.Release(); }
    }
    public async Task StopMusicModeAsync(CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            if (music is not null) { await music.DisposeAsync(); music = null; }
            if (control is not null) await SendLockedAsync("set_music", new object[] { 0 }, true, cancellationToken);
        }
        finally { gate.Release(); }
    }
    public async ValueTask DisposeAsync()
    {
        await gate.WaitAsync();
        try
        {
            if (music is not null) { await music.DisposeAsync(); music = null; }
            reader?.Dispose(); reader = null;
            if (control is not null) await control.DisposeAsync(); control = null;
            await listener.DisposeAsync();
        }
        finally { gate.Release(); }
    }
}
public sealed record YeelightFlowStep(int Duration, int Mode, int Value, int Brightness)
{
    public string Format()
    {
        if (Duration < 50 || Mode is not (1 or 2 or 7) ||
            (Mode == 1 && (Value < 0 || Value > 0xFFFFFF)) || (Mode == 2 && (Value < 1700 || Value > 6500)) ||
            (Mode != 7 && Brightness != -1 && (Brightness < 1 || Brightness > 100))) throw new ArgumentOutOfRangeException(nameof(YeelightFlowStep));
        return FormattableString.Invariant($"{Duration},{Mode},{Value},{Brightness}");
    }
}
