namespace ControlSpawn.App.Common;

/// <summary>App preference combined with the Windows animation accessibility preference.</summary>
public static class Motion
{
    private static bool reduceAnimations;
    /// <summary>CONTROLSPAWN_STILL=1 turns off all decorative motion, for measuring what animations cost.</summary>
    private static readonly bool ForcedStill = Environment.GetEnvironmentVariable("CONTROLSPAWN_STILL") == "1";
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
    /// A value change worth showing as motion. Readings arrive once a second; gliding each one over half that second
    /// keeps gauges moving smoothly instead of stepping. Only noise-sized changes (under 1% of the range) jump.
    /// </summary>
    public static bool Glides(double from, double to, double range) => !ReduceAnimations && double.IsFinite(from) && double.IsFinite(to) && Math.Abs(to - from) >= range * 0.01;
    /// <summary>The glide used for those changes: eased over half a second, at 30 frames a second (each frame re-blurs the gauge glows).</summary>
    public static System.Windows.Media.Animation.DoubleAnimation Glide(double to)
    {
        var glide = new System.Windows.Media.Animation.DoubleAnimation(to, TimeSpan.FromMilliseconds(500))
        { EasingFunction = new System.Windows.Media.Animation.CubicEase { EasingMode = System.Windows.Media.Animation.EasingMode.EaseOut } };
        System.Windows.Media.Animation.Timeline.SetDesiredFrameRate(glide, 30);
        return glide;
    }
    public static TimeSpan Duration(TimeSpan normal) => ReduceAnimations ? TimeSpan.Zero : normal;
    /// <summary>Precise clock for timer-driven effects; Environment.TickCount moves in 15.6 ms steps and made them judder.</summary>
    public static readonly System.Diagnostics.Stopwatch Clock = System.Diagnostics.Stopwatch.StartNew();
    /// <summary>
    /// Timer for drawn effects (LED strips, case previews, shimmer). Windows timers tick in 15.6 ms steps, so 50 ms really
    /// meant 62 ms (16 fps); 31 ms lands on two steps for a steady 32 fps. Render priority keeps frames coming while busy.
    /// </summary>
    public static System.Windows.Threading.DispatcherTimer FrameTimer() =>
        new(System.Windows.Threading.DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(31) };
    /// <summary>Caps an endless decorative animation's frame rate; WPF otherwise redraws at 60 fps for as long as it runs.</summary>
    public static T Decorative<T>(T timeline, int framesPerSecond) where T : System.Windows.Media.Animation.Timeline
    {
        System.Windows.Media.Animation.Timeline.SetDesiredFrameRate(timeline, framesPerSecond);
        return timeline;
    }
}
