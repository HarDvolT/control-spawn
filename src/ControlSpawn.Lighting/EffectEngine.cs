using ControlSpawn.Core;
using ControlSpawn.Core.Ipc;

namespace ControlSpawn.Lighting;

public interface IAudioLevelInput { double Level { get; } }
public interface IScreenAverageInput { RgbColor Average { get; } }
public interface ICpuTemperatureInput { double Temperature { get; } }
public sealed class EffectEngine(IClock clock, IAudioLevelInput audio, IScreenAverageInput screen, ICpuTemperatureInput cpu)
{
    public IReadOnlyList<RgbColor> Render(Scene scene, EffectParameters parameters, double time, IReadOnlyList<MapPoint> positions,
        string device = "", string kind = "", ReactiveFrame? input = null) => SpatialEffects.Render(scene, parameters, time, positions, device, kind, input);
    public IReadOnlyList<RgbColor> Render(Scene scene, int ledCount)
    {
        if (ledCount < 0 || ledCount > ushort.MaxValue) throw new ArgumentOutOfRangeException(nameof(ledCount));
        if (!double.IsFinite(scene.Speed) || scene.Speed < 0 || !double.IsFinite(scene.Brightness)) throw new ArgumentOutOfRangeException(nameof(scene));
        if (scene.Effect > LightingEffect.Custom)
            return SpatialEffects.Render(scene, new() { Palette = Palettes.ForEffect(scene.Effect) }, clock.Elapsed.TotalSeconds,
                Enumerable.Range(0, ledCount).Select(i => new MapPoint((double)i / Math.Max(1, ledCount - 1), 0.5)).ToArray());
        double time = clock.Elapsed.TotalSeconds * scene.Speed;
        var colors = new RgbColor[ledCount];
        for (int i = 0; i < ledCount; i++)
        {
            RgbColor c = scene.Effect switch
            {
                LightingEffect.Static => scene.Color,
                LightingEffect.Custom => RgbColor.Black,
                LightingEffect.Off => RgbColor.Black,
                LightingEffect.Breathing => Scale(scene.Color, (1 - Math.Cos(time * Math.PI)) / 2),
                LightingEffect.Strobing => (long)Math.Floor(time * 4) % 2 == 0 ? scene.Color : RgbColor.Black,
                LightingEffect.ColorCycle => Hue(time / 8),
                LightingEffect.RainbowWave => Hue(time / 8 + (double)i / Math.Max(1, ledCount)),
                LightingEffect.StarryNight => Star(scene.Color, i, time),
                LightingEffect.Smart => RgbColor.Lerp(new(0, 128, 255), new(255, 32, 0),
                    double.IsFinite(cpu.Temperature) ? (cpu.Temperature - 40) / 45 : 1),
                LightingEffect.Music => Scale(scene.Color, double.IsFinite(audio.Level) ? audio.Level : 0),
                LightingEffect.Adaptive => screen.Average,
                _ => RgbColor.Black
            };
            colors[i] = Scale(c, scene.Brightness);
        }
        return colors;
    }
    public async Task RunAsync(Func<Scene> scene, int ledCount, Func<IReadOnlyList<RgbColor>, CancellationToken, Task> output, CancellationToken cancellationToken)
    {
        TimeSpan frame = TimeSpan.FromSeconds(1d / 30), next = clock.Elapsed;
        while (!cancellationToken.IsCancellationRequested)
        {
            await output(Render(scene(), ledCount), cancellationToken).ConfigureAwait(false);
            next += frame;
            if (next < clock.Elapsed) next = clock.Elapsed + frame;
            TimeSpan delay = next - clock.Elapsed;
            if (delay > TimeSpan.Zero) await clock.DelayAsync(delay, cancellationToken).ConfigureAwait(false);
        }
    }
    private static RgbColor Scale(RgbColor color, double amount) => RgbColor.Lerp(RgbColor.Black, color, amount);
    private static RgbColor Star(RgbColor color, int led, double time)
    {
        // Stable integer hash, smooth independent pulses: no Random state and seek-safe.
        uint hash = unchecked((uint)(led + 1) * 2654435761u);
        double phase = hash / (double)uint.MaxValue * 8;
        double pulse = Math.Max(0, Math.Sin((time + phase) * 1.7));
        return Scale(color, Math.Pow(pulse, 12));
    }
    private static RgbColor Hue(double hue)
    {
        double h = (hue - Math.Floor(hue)) * 6;
        byte x = (byte)Math.Round(255 * (1 - Math.Abs(h % 2 - 1)));
        return (int)h switch { 0 => new(255, x, 0), 1 => new(x, 255, 0), 2 => new(0, 255, x),
            3 => new(0, x, 255), 4 => new(x, 0, 255), _ => new(255, 0, x) };
    }
}
