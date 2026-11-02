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
    /// <summary>Runs <paramref name="start"/> only while the element is loaded and actually visible, and <paramref name="stop"/> otherwise,
    /// so animations stop on hidden pages, a minimized window or the tray.</summary>
    public static void WhileVisible(System.Windows.FrameworkElement element, Action start, Action stop)
    {
        void Update() { if (element.IsLoaded && element.IsVisible) start(); else stop(); }
        element.Loaded += (_, _) => Update();
        element.Unloaded += (_, _) => stop();
        element.IsVisibleChanged += (_, _) => Update();
    }
    public static TimeSpan Duration(TimeSpan normal) => ReduceAnimations ? TimeSpan.Zero : normal;
    /// <summary>Caps an endless decorative animation's frame rate; WPF otherwise redraws at 60 fps for as long as it runs.</summary>
    public static T Decorative<T>(T timeline, int framesPerSecond) where T : System.Windows.Media.Animation.Timeline
    {
        System.Windows.Media.Animation.Timeline.SetDesiredFrameRate(timeline, framesPerSecond);
        return timeline;
    }
}
