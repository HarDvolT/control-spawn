using Prism.Core;

namespace Prism.Lighting;

public enum LampColorSelection { Average, Main }
public sealed class LampSync(ILampClient lamp, IClock clock)
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private TimeSpan? last;
    private RgbColor? previous;
    public RgbColor? LastColor => previous;
    public async Task<bool> PushAsync(IReadOnlyList<RgbColor> colors, LampColorSelection selection = LampColorSelection.Average,
        double smoothingSeconds = 0.25, CancellationToken cancellationToken = default)
    {
        if (!double.IsFinite(smoothingSeconds) || smoothingSeconds < 0) throw new ArgumentOutOfRangeException(nameof(smoothingSeconds));
        await gate.WaitAsync(cancellationToken);
        try
        {
            if (colors.Count == 0) return false;
            var now = clock.Elapsed;
            var interval = TimeSpan.FromSeconds(lamp.IsMusicMode ? 0.1 : lamp is MiioLampClient ? 0.5 : 1);
            if (last is not null && now - last.Value < interval) return false;
            RgbColor target = selection == LampColorSelection.Main ? colors[0] : new(
                (byte)Math.Round(colors.Average(c => c.R)), (byte)Math.Round(colors.Average(c => c.G)), (byte)Math.Round(colors.Average(c => c.B)));
            double alpha = smoothingSeconds == 0 || last is null ? 1 : 1 - Math.Exp(-(now - last.Value).TotalSeconds / smoothingSeconds);
            RgbColor output = previous is null ? target : RgbColor.Lerp(previous.Value, target, alpha);
            await lamp.SetColorAsync(output, cancellationToken);
            previous = output; last = clock.Elapsed; return true;
        }
        finally { gate.Release(); }
    }
}
