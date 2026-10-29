namespace Prism.App.Common;

/// <summary>App preference combined with the Windows animation accessibility preference.</summary>
public static class Motion
{
    private static bool reduceAnimations;
    public static bool ReduceAnimations
    {
        get => reduceAnimations || !System.Windows.SystemParameters.ClientAreaAnimation;
        set => reduceAnimations = value;
    }
    public static TimeSpan Duration(TimeSpan normal) => ReduceAnimations ? TimeSpan.Zero : normal;
}
