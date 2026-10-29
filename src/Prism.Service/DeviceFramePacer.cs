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

    private readonly Dictionary<string, (TimeSpan At, RgbColor[] Colors)> sent = new();

    public static TimeSpan MinInterval(string kind) => kind switch
    {
        "GPU" => TimeSpan.FromSeconds(1d / 10),
        "RAM" => TimeSpan.FromSeconds(1d / 20),
        _ => TimeSpan.FromSeconds(1d / 30)
    };

    public bool ShouldSend(string deviceId, string kind, IReadOnlyList<RgbColor> colors, TimeSpan now)
    {
        if (!sent.TryGetValue(deviceId, out var last)) return true;
        var since = now - last.At;
        if (since < MinInterval(kind)) return false;
        return since >= KeepAlive || !Same(last.Colors, colors);
    }

    public void Sent(string deviceId, IReadOnlyList<RgbColor> colors, TimeSpan now) => sent[deviceId] = (now, colors.ToArray());

    /// <summary>Forget a device (after a reconnect or resize) so its next frame is sent immediately.</summary>
    public void Reset() => sent.Clear();

    private static bool Same(RgbColor[] a, IReadOnlyList<RgbColor> b)
    {
        if (a.Length != b.Count) return false;
        for (int i = 0; i < a.Length; i++)
            if (Math.Abs(a[i].R - b[i].R) > Tolerance || Math.Abs(a[i].G - b[i].G) > Tolerance || Math.Abs(a[i].B - b[i].B) > Tolerance) return false;
        return true;
    }
}
