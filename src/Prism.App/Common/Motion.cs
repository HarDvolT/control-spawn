namespace Prism.App.Common;

/// <summary>App preference combined with the Windows animation accessibility preference.</summary>
public static class Motion
{
    private static bool reduceAnimations;
    /// <summary>PRISM_STILL=1 turns off all decorative motion, for measuring what animations cost.</summary>
    private static readonly bool ForcedStill = Environment.GetEnvironmentVariable("PRISM_STILL") == "1";
    public static bool ReduceAnimations
    {
        get => reduceAnimations || ForcedStill || !System.Windows.SystemParameters.ClientAreaAnimation;
        set => reduceAnimations = value;
    }
    /// <summary>Runs <paramref name="start"/> only while the element is loaded and actually visible, and <paramref name="stop"/> otherwise,
    /// so animations stop on hidden pages, a minimized window or the tray.</summary>
    public static void WhileVisible(System.Windows.FrameworkElement element, Action start, Action stop)
    {
        void Update() { if (element.IsLoaded && element.IsVisible) start(); else stop(); }
        element.Loaded += (_, _) => Update();
        element.Unloaded += (_, _) => stop();
        element.IsVisibleChanged += (_, _) => Update();
    }
    /// <summary>
    /// A value change worth showing as motion. Live readings move a little every second; gliding each of those
    /// kept gauges animating (and their glows re-blurring) most of the time. Small steps jump instead.
    /// </summary>
    public static bool Glides(double from, double to, double range) => !ReduceAnimations && double.IsFinite(from) && double.IsFinite(to) && Math.Abs(to - from) >= range * 0.08;
    /// <summary>The glide used for those bigger changes: short, eased, at 30 frames a second.</summary>
    public static System.Windows.Media.Animation.DoubleAnimation Glide(double to)
    {
        var glide = new System.Windows.Media.Animation.DoubleAnimation(to, TimeSpan.FromMilliseconds(250))
        { EasingFunction = new System.Windows.Media.Animation.CubicEase { EasingMode = System.Windows.Media.Animation.EasingMode.EaseOut } };
        System.Windows.Media.Animation.Timeline.SetDesiredFrameRate(glide, 30);
        return glide;
    }
    public static TimeSpan Duration(TimeSpan normal) => ReduceAnimations ? TimeSpan.Zero : normal;
    /// <summary>Caps an endless decorative animation's frame rate; WPF otherwise redraws at 60 fps for as long as it runs.</summary>
    public static T Decorative<T>(T timeline, int framesPerSecond) where T : System.Windows.Media.Animation.Timeline
    {
        System.Windows.Media.Animation.Timeline.SetDesiredFrameRate(timeline, framesPerSecond);
        return timeline;
    }
}
