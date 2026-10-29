using System.Diagnostics;

namespace Prism.Core;

/// <summary>
/// GPU sensor reads (LibreHardwareMonitor via the AMD driver) and GPU lighting writes (OpenRGB) share the
/// card's I2C bus. When they overlap, the RX 9070 XT's lighting chip can latch a wrong colour for a moment:
/// the live A/B test on 2026-10-29 showed a red flash on static blue with sensor reads on, none with them off.
/// This makes the two take turns: a sensor read waits until the last lighting write has had time to finish,
/// and lighting writes are held back while a read is running and shortly after it.
/// </summary>
public static class GpuBusTurns
{
    /// <summary>How long after a lighting packet OpenRGB may still be writing it to the card.</summary>
    public static readonly TimeSpan WriteSettle = TimeSpan.FromMilliseconds(120);
    /// <summary>Quiet time after a sensor read before lighting writes resume.</summary>
    public static readonly TimeSpan ReadSettle = TimeSpan.FromMilliseconds(60);

    private static readonly object gate = new();
    private static readonly Stopwatch clock = Stopwatch.StartNew();
    private static TimeSpan lastWrite = TimeSpan.MinValue / 2, quietUntil = TimeSpan.Zero;
    private static bool reading;

    /// <summary>Lighting side: true if a GPU frame may be sent now; records the write when it may.</summary>
    public static bool TryBeginWrite()
    {
        lock (gate)
        {
            var now = clock.Elapsed;
            if (reading || now < quietUntil) return false;
            lastWrite = now;
            return true;
        }
    }

    /// <summary>Sensor side: waits for the bus, runs the read, then keeps lights quiet for <see cref="ReadSettle"/>.</summary>
    public static void Read(Action read)
    {
        TimeSpan wait;
        lock (gate) { reading = true; wait = lastWrite + WriteSettle - clock.Elapsed; }
        try
        {
            if (wait > TimeSpan.Zero) Thread.Sleep(wait);
            read();
        }
        finally
        {
            lock (gate) { reading = false; quietUntil = clock.Elapsed + ReadSettle; }
        }
    }
}
