using System.Windows;
using System.Windows.Media;
using ControlSpawn.App.Services;

namespace ControlSpawn.App.Common;

/// <summary>Display conversion only. Service readings, thresholds and commands remain Celsius.</summary>
public static class DisplayPreferences
{
    public static bool Fahrenheit { get; private set; }
    public static event EventHandler? Changed;
    public static double Temperature(double celsius) => Fahrenheit ? celsius * 9 / 5 + 32 : celsius;
    public static string Unit => Fahrenheit ? "°F" : "°C";
    public static void Apply(AppSettings settings)
    {
        Fahrenheit = settings.TemperatureUnit == "°F";
        if (Application.Current is { } app)
        {
            var color = ColorUtil.Parse(settings.AccentColor, ColorUtil.Accent);
            app.Resources["AccentColor"] = color;
            app.Resources["AccentBrush"] = new SolidColorBrush(color);
            app.Resources["AccentHoverBrush"] = new SolidColorBrush(ColorUtil.Mix(color, Colors.White, .25));
            app.Resources["AccentSoftBrush"] = new SolidColorBrush(Color.FromArgb(38, color.R, color.G, color.B));
            app.Resources["NavActiveBrush"] = new LinearGradientBrush(Color.FromArgb(46, color.R, color.G, color.B), Color.FromArgb(10, color.R, color.G, color.B), 0);
        }
        Changed?.Invoke(null, EventArgs.Empty);
    }
}
