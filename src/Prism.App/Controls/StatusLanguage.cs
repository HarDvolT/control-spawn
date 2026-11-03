using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Effects;

namespace Prism.App.Controls;

public enum StatusLevel { Cool, Ok, Warm, Hot, Critical, Unavailable }
public enum SensorKind { CpuTctl, GpuEdge, GpuHotspot, Dimm, Nvme, Load, Ping }
public sealed record SensorThreshold(double Warm, double Hot, double Critical);
public static class Thresholds
{
    public static IReadOnlyDictionary<SensorKind, SensorThreshold> Table { get; } = new Dictionary<SensorKind, SensorThreshold>
    {
        [SensorKind.CpuTctl] = new(75, 85, 95), [SensorKind.GpuEdge] = new(80, 90, 100),
        [SensorKind.GpuHotspot] = new(90, 100, 110), [SensorKind.Dimm] = new(50, 60, 70),
        [SensorKind.Nvme] = new(60, 70, 80), [SensorKind.Load] = new(70, 90, 99), [SensorKind.Ping] = new(60, 120, 250)
    };
    public static StatusLevel Level(double value, SensorKind kind)
    {
        if (!double.IsFinite(value)) return StatusLevel.Unavailable;
        var t = Table[kind];
        return value >= t.Critical ? StatusLevel.Critical : value >= t.Hot ? StatusLevel.Hot : value >= t.Warm ? StatusLevel.Warm : value < t.Warm * .5 ? StatusLevel.Cool : StatusLevel.Ok;
    }
    public static Color Color(StatusLevel level) => (Color)ColorConverter.ConvertFromString(level switch
    { StatusLevel.Unavailable => "#7C808A", StatusLevel.Cool => "#36D6FF", StatusLevel.Ok => "#3DDC84", StatusLevel.Warm => "#F5A524", _ => "#FF4D4F" });
    public static Brush Brush(StatusLevel level) => new SolidColorBrush(Color(level));
    public static DropShadowEffect Glow(StatusLevel level) => new() { Color = Color(level), BlurRadius = 18, ShadowDepth = 0, Opacity = .5 };
}
public sealed class MetricToLevelConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value is double n && double.IsFinite(n) ? Thresholds.Level(n, Enum.TryParse<SensorKind>(parameter?.ToString(), out var k) ? k : SensorKind.Load) : StatusLevel.Unavailable;
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}
public sealed class LevelToBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => Thresholds.Brush(value is StatusLevel level ? level : StatusLevel.Unavailable);
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}
public sealed class LevelToGlowConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => Thresholds.Glow(value is StatusLevel level ? level : StatusLevel.Unavailable);
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}
public sealed class FriendlyLabelConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => Label(value);
    public static string Label(object? value) => value?.ToString() switch
    {
        "True" => "Connected", "False" => "Offline", "miio" => "Mi Home", "yeelight" => "Local network", "mock" => "Preview",
        "openrgb" => "OpenRGB", "lhm" => "System sensors", "router" => "Router",
        "start-cook" => "Start cooking", "resume-cook" => "Resume cooking", "cancel-cooking" => "Cancel cooking",
        "true" => "On", "false" => "Off",
        "own" => "Own colour", "pc" => "Same as PC", "average" => "Average PC colour", "map" => "Lighting map",
        "RgbController" => "PC lighting", "FanHeader" => "Fan", "NetworkDevice" => "Network device",
        null => "Unavailable", var text when text.StartsWith("rgb:", StringComparison.Ordinal) => "PC lighting",
        var text when text.StartsWith("yeelink.light.", StringComparison.Ordinal) => "Smart lamp",
        var text => System.Text.RegularExpressions.Regex.Replace(text.Replace('-', ' ').Replace('_', ' '), "([a-z])([A-Z])", "$1 $2")
    };
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}
