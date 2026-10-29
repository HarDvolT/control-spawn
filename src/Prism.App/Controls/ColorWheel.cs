using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Prism.App.Controls;

/// <summary>Continuous hue/saturation wheel. Pointer changes update the binding on every movement.</summary>
public sealed class ColorWheel : FrameworkElement
{
    public static readonly DependencyProperty ColorHexProperty = DependencyProperty.Register(nameof(ColorHex), typeof(string), typeof(ColorWheel),
        new FrameworkPropertyMetadata("#FF4D1A", FrameworkPropertyMetadataOptions.AffectsRender | FrameworkPropertyMetadataOptions.BindsTwoWayByDefault));
    public string ColorHex { get => (string)GetValue(ColorHexProperty); set => SetValue(ColorHexProperty, value); }
    public event EventHandler? ColorCommitted;
    public event Action<string>? ColorPicked;
    private static readonly BitmapSource Wheel = CreateWheel();
    public ColorWheel() { MinWidth = 160; MinHeight = 160; Focusable = true; Cursor = Cursors.Cross; }
    protected override Size MeasureOverride(Size available) => new(Math.Min(240, available.Width), Math.Min(240, available.Height));
    protected override void OnRender(DrawingContext dc)
    {
        double diameter = Math.Max(0, Math.Min(ActualWidth, ActualHeight) - 8);
        var rect = new Rect((ActualWidth - diameter) / 2, (ActualHeight - diameter) / 2, diameter, diameter);
        dc.DrawImage(Wheel, rect);
        if (ColorConverter.ConvertFromString(ColorHex) is not Color c) return;
        double max = Math.Max(c.R, Math.Max(c.G, c.B)), min = Math.Min(c.R, Math.Min(c.G, c.B)), delta = max - min;
        double hue = delta == 0 ? 0 : max == c.R ? ((c.G - c.B) / delta + 6) % 6 : max == c.G ? (c.B - c.R) / delta + 2 : (c.R - c.G) / delta + 4;
        double angle = hue * Math.PI / 3, radius = max == 0 ? 0 : delta / max * diameter / 2;
        var marker = new Point(ActualWidth / 2 + Math.Cos(angle) * radius, ActualHeight / 2 + Math.Sin(angle) * radius);
        dc.DrawEllipse(null, new Pen(Brushes.Black, 5), marker, 6, 6);
        dc.DrawEllipse(null, new Pen(Brushes.White, 2), marker, 6, 6);
    }
    private void Pick(Point p)
    {
        double x = p.X - ActualWidth / 2, y = p.Y - ActualHeight / 2, r = Math.Max(1, (Math.Min(ActualWidth, ActualHeight) - 8) / 2);
        var c = Hsv((Math.Atan2(y, x) * 180 / Math.PI + 360) % 360, Math.Min(1, Math.Sqrt(x * x + y * y) / r));
        SetCurrentValue(ColorHexProperty, $"#{c.R:X2}{c.G:X2}{c.B:X2}");
        ColorPicked?.Invoke(ColorHex);
    }
    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e) { Focus(); CaptureMouse(); Pick(e.GetPosition(this)); e.Handled = true; }
    protected override void OnMouseMove(MouseEventArgs e) { if (IsMouseCaptured) Pick(e.GetPosition(this)); }
    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e) { if (IsMouseCaptured) { Pick(e.GetPosition(this)); ReleaseMouseCapture(); ColorCommitted?.Invoke(this, EventArgs.Empty); } }
    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.Key is not (Key.Left or Key.Right or Key.Up or Key.Down)) return;
        var p = Mouse.GetPosition(this);
        Pick(new Point(p.X + (e.Key == Key.Right ? 3 : e.Key == Key.Left ? -3 : 0), p.Y + (e.Key == Key.Down ? 3 : e.Key == Key.Up ? -3 : 0))); e.Handled = true;
        ColorCommitted?.Invoke(this, EventArgs.Empty);
    }
    private static Color Hsv(double hue, double saturation)
    {
        double h = hue / 60, x = 1 - Math.Abs(h % 2 - 1);
        var (r, g, b) = (int)h switch { 0 => (1d, x, 0d), 1 => (x, 1d, 0d), 2 => (0d, 1d, x), 3 => (0d, x, 1d), 4 => (x, 0d, 1d), _ => (1d, 0d, x) };
        byte Channel(double v) => (byte)Math.Round(255 * (1 - saturation + v * saturation));
        return Color.FromRgb(Channel(r), Channel(g), Channel(b));
    }
    private static BitmapSource CreateWheel()
    {
        const int size = 384; var pixels = new byte[size * size * 4];
        for (int y = 0; y < size; y++) for (int x = 0; x < size; x++)
        {
            double dx = x - (size - 1) / 2d, dy = y - (size - 1) / 2d, s = Math.Sqrt(dx * dx + dy * dy) / (size / 2d - 1);
            if (s > 1) continue;
            var c = Hsv((Math.Atan2(dy, dx) * 180 / Math.PI + 360) % 360, s); int i = (y * size + x) * 4;
            pixels[i] = c.B; pixels[i + 1] = c.G; pixels[i + 2] = c.R; pixels[i + 3] = 255;
        }
        var bitmap = BitmapSource.Create(size, size, 96, 96, PixelFormats.Bgra32, null, pixels, size * 4); bitmap.Freeze(); return bitmap;
    }
}
