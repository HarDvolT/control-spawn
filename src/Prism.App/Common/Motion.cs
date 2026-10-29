namespace Prism.App.Common;

/// <summary>Global motion preference, driven by the "Reduce animations" setting.</summary>
public static class Motion
{
    public static bool ReduceAnimations { get; set; }
    public static TimeSpan Duration(TimeSpan normal) => ReduceAnimations ? TimeSpan.Zero : normal;
}
