using Prism.Core;

namespace Prism.Service;

/// <summary>
/// Decides, per RGB device, whether a rendered frame is worth sending. The effect engine renders at 30 fps,
/// but some controllers sit on slow buses: the ASRock RX 9070 XT's lighting chip (GPU I2C) flickered and
/// showed mixed LEDs at 30 fps in the live test on 2026-10-29. So each device kind gets its own maximum rate,
/// frames that are visually identical to the last one sent are skipped, and an unchanged frame is still
/// re-sent every <see cref="KeepAlive"/> so a device that dropped a write recovers.
/// </summary>
public sealed class DeviceFramePacer
{
    public static readonly TimeSpan KeepAlive = TimeSpan.FromSeconds(2);
    /// <summary>Largest per-channel difference still treated as "the same colour" (avoids bus traffic for invisible changes).</summary>
    public const int Tolerance = 2;
    public static readonly TimeSpan TickSlack = TimeSpan.FromMilliseconds(4);

    private readonly Dictionary<string, (TimeSpan At, RgbColor[] Colors)> sent = new();
    private readonly Dictionary<string, (TimeSpan Since, RgbColor[] Colors)> pending = new();

    /// <summary>
    /// The RX 9070 XT's lighting chip flashes other colours on writes, even at 6 per second (live, 2026-11-02). So the
    /// card steps through animations in <see cref="GpuStep"/> jumps and a new colour is only written once it has held
    /// for <see cref="GpuSettle"/>: dragging the brightness slider costs one write at the end, not dozens.
    /// </summary>
    public static readonly TimeSpan GpuStep = TimeSpan.FromSeconds(0.4), GpuSettle = TimeSpan.FromMilliseconds(120);
    /// <summary>
    /// The animation time a device of this kind is drawn at. The GPU's is held still between steps, and each step shows
    /// where the case will be when the write lands (after the settle wait), so the card keeps up instead of trailing.
    /// </summary>
    public static double FrameTime(string kind, double seconds) => kind == "GPU" ? Math.Ceiling((seconds + GpuSettle.TotalSeconds) / GpuStep.TotalSeconds) * GpuStep.TotalSeconds : seconds;

    public static TimeSpan MinInterval(string kind) => kind switch
    {
        // Every write can flash the card's LEDs through other colours (live, 2026-11-02), so it gets few of them.
        "GPU" => TimeSpan.FromSeconds(1d / 6),
        // DIMM temperature reads now take the SMBus only every 10 s, so the RAM can keep up with fast effects.
        "RAM" => TimeSpan.FromSeconds(1d / 30),
        // The board's USB link takes a frame in ~0.1 ms, so the fans get every tick.
        _ => TimeSpan.FromSeconds(1d / 60)
    };

    public bool ShouldSend(string deviceId, string kind, IReadOnlyList<RgbColor> colors, TimeSpan now)
    {
        if (!sent.TryGetValue(deviceId, out var last)) return true;
        var since = now - last.At;
        if (kind == "GPU" && !Same(last.Colors, colors))
        {
            if (!pending.TryGetValue(deviceId, out var wait) || !Same(wait.Colors, colors)) { pending[deviceId] = (now, colors.ToArray()); return false; }
            if (now - wait.Since < GpuSettle) return false;
        }
        // Loop ticks land a few ms either side of the frame time; without this slack an early tick dropped a whole frame.
        if (since < MinInterval(kind) - TickSlack) return false;
        // Only the shared buses (GPU, RAM) skip near-identical frames. On the board's USB link every step is sent: slow
        // effects move 1-2 steps a frame, and skipping those made the fans update in uneven bursts (live, 2026-11-02).
        return since >= KeepAliveFor(kind) || (kind is "GPU" or "RAM" ? !Same(last.Colors, colors) : !Identical(last.Colors, colors));
    }

    private static bool Identical(RgbColor[] a, IReadOnlyList<RgbColor> b)
    {
        if (a.Length != b.Count) return false;
        for (int i = 0; i < a.Length; i++) if (!a[i].Equals(b[i])) return false;
        return true;
    }

    /// <summary>
    /// Every write to the GPU's lighting chip is a chance for a glitch (a single LED flicked red even with
    /// Prism's sensor reads off), so a GPU showing a still colour is only refreshed every 10 s.
    /// </summary>
    public static TimeSpan KeepAliveFor(string kind) => kind == "GPU" ? TimeSpan.FromSeconds(10) : KeepAlive;

    /// <summary>The frame last sent to a device, or null before the first.</summary>
    public RgbColor[]? Last(string deviceId) => sent.TryGetValue(deviceId, out var last) ? last.Colors : null;

    /// <summary>True if a zone's LEDs differ visibly from the last frame sent.</summary>
    public static bool ZoneChanged(RgbColor[] last, IReadOnlyList<RgbColor> colors, int first, int count)
    {
        for (int i = first; i < first + count && i < last.Length && i < colors.Count; i++)
            if (!Close(last[i].R, colors[i].R) || !Close(last[i].G, colors[i].G) || !Close(last[i].B, colors[i].B)) return true;
        return false;
    }

    public void Sent(string deviceId, IReadOnlyList<RgbColor> colors, TimeSpan now) => sent[deviceId] = (now, colors.ToArray());

    /// <summary>Forget a device (after a reconnect or resize) so its next frame is sent immediately.</summary>
    public void Reset() { sent.Clear(); pending.Clear(); }

    private static bool Same(RgbColor[] a, IReadOnlyList<RgbColor> b)
    {
        if (a.Length != b.Count) return false;
        for (int i = 0; i < a.Length; i++)
            if (!Close(a[i].R, b[i].R) || !Close(a[i].G, b[i].G) || !Close(a[i].B, b[i].B)) return false;
        return true;
    }

    // Near black every step shows (and a fade's last steps are only 1-2 apart), so low values must match exactly:
    // with the tolerance there, a heartbeat stopped on a dim, off-hue glow instead of reaching black between beats.
    private static bool Close(byte a, byte b) => a < 24 || b < 24 ? a == b : Math.Abs(a - b) <= Tolerance;
}
