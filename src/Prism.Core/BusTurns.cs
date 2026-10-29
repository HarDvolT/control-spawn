using System.Diagnostics;

namespace Prism.Core;

/// <summary>
/// Sensor reads and lighting writes that share one physical bus take turns, so a read never lands in the middle
/// of a lighting write. Two buses on this PC are shared:
/// <list type="bullet">
/// <item><see cref="Gpu"/>: GPU sensor reads (LibreHardwareMonitor via the AMD driver) and the RX 9070 XT's
/// lighting chip (OpenRGB over the card's I2C). The live A/B test on 2026-10-29 showed a red flash on static blue
/// with sensor reads on.</item>
/// <item><see cref="Smbus"/>: DIMM temperature reads (SPD sensors) and the Vengeance RGB controllers, both on the
/// chipset SMBus (PIIX4). The RAM flickered in the same live session.</item>
/// </list>
/// A sensor read waits until the last lighting write has had time to finish; lighting writes are held back while a
/// read is running and shortly after it.
/// </summary>
public sealed class BusTurns
{
    public static readonly BusTurns Gpu = new();
    public static readonly BusTurns Smbus = new();

    /// <summary>How long after a lighting packet OpenRGB may still be writing it to the device.</summary>
    public static readonly TimeSpan WriteSettle = TimeSpan.FromMilliseconds(120);
    /// <summary>Quiet time after a sensor read before lighting writes resume.</summary>
    public static readonly TimeSpan ReadSettle = TimeSpan.FromMilliseconds(60);

    private readonly object gate = new();
    private readonly Stopwatch clock = Stopwatch.StartNew();
    private TimeSpan lastWrite = TimeSpan.MinValue / 2, quietUntil = TimeSpan.Zero;
    private bool reading;

    /// <summary>The bus a lighting device of this kind writes on, or null if it has its own (USB) link.</summary>
    public static BusTurns? ForDeviceKind(string kind) => kind switch { "GPU" => Gpu, "RAM" => Smbus, _ => null };

    /// <summary>Lighting side: true if a frame may be sent now; records the write when it may.</summary>
    public bool TryBeginWrite()
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
    public void Read(Action read)
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
