using ControlSpawn.App.Common;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace ControlSpawn.App.Controls;

/// <summary>Continuous hue/saturation wheel. Pointer changes update the binding on every movement.</summary>
public sealed class ColorWheel : FrameworkElement
{
    public static readonly DependencyProperty ColorHexProperty = DependencyProperty.Register(nameof(ColorHex), typeof(string), typeof(ColorWheel),
        new FrameworkPropertyMetadata("#FF4D1A", FrameworkPropertyMetadataOptions.AffectsRender | FrameworkPropertyMetadataOptions.BindsTwoWayByDefault));
    public string ColorHex { get => (string)GetValue(ColorHexProperty); set => SetValue(ColorHexProperty, value); }
    public static readonly DependencyProperty RingModeProperty = DependencyProperty.Register(nameof(RingMode), typeof(bool), typeof(ColorWheel),
        new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender));
    public bool RingMode { get => (bool)GetValue(RingModeProperty); set => SetValue(RingModeProperty, value); }
    public event EventHandler? ColorCommitted;
    public event Action<string>? ColorPicked;
    private static readonly BitmapSource Wheel = CreateWheel();
    private static readonly Color[] RingPalette = [Color.FromRgb(255, 77, 79), Color.FromRgb(245, 165, 36), Color.FromRgb(255, 227, 77), Color.FromRgb(61, 220, 132), Color.FromRgb(54, 214, 255), Color.FromRgb(77, 107, 255), Color.FromRgb(178, 107, 255), Color.FromRgb(255, 77, 184), Color.FromRgb(255, 77, 79)];
    private static readonly BitmapSource RingWheel = CreateWheel(true);
    public ColorWheel() { MinWidth = 160; MinHeight = 160; Focusable = true; Cursor = Cursors.Cross; }
    protected override Size MeasureOverride(Size available) => new(Math.Min(240, available.Width), Math.Min(240, available.Height));
    protected override void OnRender(DrawingContext dc)
    {
        double diameter = Math.Max(0, Math.Min(ActualWidth, ActualHeight) - 8);
        var rect = new Rect((ActualWidth - diameter) / 2, (ActualHeight - diameter) / 2, diameter, diameter);
        var center = new Point(ActualWidth / 2, ActualHeight / 2);
        if (RingMode) dc.PushClip(new CombinedGeometry(GeometryCombineMode.Exclude,
            new EllipseGeometry(center, diameter / 2, diameter / 2), new EllipseGeometry(center, diameter / 2 - 18, diameter / 2 - 18)));
        dc.DrawImage(RingMode ? RingWheel : Wheel, rect);
        if (RingMode) dc.Pop();
        if (ColorConverter.ConvertFromString(ColorHex) is not Color c) return;
        if (RingMode)
        {
            var orb = new Point(center.X, center.Y - 11);
            dc.DrawEllipse(new RadialGradientBrush(Color.FromArgb(100, c.R, c.G, c.B), Color.FromArgb(0, c.R, c.G, c.B)), null, orb, 46, 46);
            dc.DrawEllipse(new LinearGradientBrush(c, Color.FromRgb((byte)(c.R * .75), (byte)(c.G * .75), (byte)(c.B * .75)), 90), null, orb, 26, 26);
            var text = new FormattedText(ColorHex, System.Globalization.CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                Typefaces.Number(FontWeights.SemiBold), 12, Brushes.White, VisualTreeHelper.GetDpi(this).PixelsPerDip);
            dc.DrawText(text, new Point(center.X - text.Width / 2, center.Y + 22));
        }
        double max = Math.Max(c.R, Math.Max(c.G, c.B)), min = Math.Min(c.R, Math.Min(c.G, c.B)), delta = max - min;
        double hue = delta == 0 ? 0 : max == c.R ? ((c.G - c.B) / delta + 6) % 6 : max == c.G ? (c.B - c.R) / delta + 2 : (c.R - c.G) / delta + 4;
        double angle = hue * Math.PI / 3, radius = max == 0 ? 0 : delta / max * diameter / 2;
        if (RingMode)
        {
            radius = diameter / 2 - 8;
            angle = Enumerable.Range(0, 360).MinBy(i => Distance(RingColorAt(i / 360d), c)) * Math.PI / 180 - Math.PI / 2;
        }
        var marker = new Point(ActualWidth / 2 + Math.Cos(angle) * radius, ActualHeight / 2 + Math.Sin(angle) * radius);
        if (RingMode) dc.DrawRoundedRectangle(Brushes.WhiteSmoke, new Pen(Brushes.Black, 3), new Rect(marker.X - 9.5, marker.Y - 9.5, 19, 19), 7, 7);
        else { dc.DrawEllipse(null, new Pen(Brushes.Black, 5), marker, 6, 6); dc.DrawEllipse(null, new Pen(Brushes.White, 2), marker, 6, 6); }
    }
    private void Pick(Point p)
    {
        double x = p.X - ActualWidth / 2, y = p.Y - ActualHeight / 2, r = Math.Max(1, (Math.Min(ActualWidth, ActualHeight) - 8) / 2);
        var c = RingMode ? RingColorAt((Math.Atan2(y, x) * 180 / Math.PI + 450) % 360 / 360) : Hsv((Math.Atan2(y, x) * 180 / Math.PI + 360) % 360, Math.Min(1, Math.Sqrt(x * x + y * y) / r));
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
    private static double Distance(Color a, Color b) => Math.Pow(a.R - b.R, 2) + Math.Pow(a.G - b.G, 2) + Math.Pow(a.B - b.B, 2);
    private static Color RingColorAt(double position)
    {
        double p = Math.Clamp(position, 0, 1) * (RingPalette.Length - 1); int i = Math.Min(RingPalette.Length - 2, (int)p);
        double t = p - i; var a = RingPalette[i]; var b = RingPalette[i + 1];
        return Color.FromRgb((byte)(a.R + (b.R - a.R) * t), (byte)(a.G + (b.G - a.G) * t), (byte)(a.B + (b.B - a.B) * t));
    }
    private static BitmapSource CreateWheel(bool ring = false)
    {
        const int size = 384; var pixels = new byte[size * size * 4];
        for (int y = 0; y < size; y++) for (int x = 0; x < size; x++)
        {
            double dx = x - (size - 1) / 2d, dy = y - (size - 1) / 2d, s = Math.Sqrt(dx * dx + dy * dy) / (size / 2d - 1);
            if (s > 1) continue;
            var c = ring ? RingColorAt((Math.Atan2(dy, dx) * 180 / Math.PI + 450) % 360 / 360) : Hsv((Math.Atan2(dy, dx) * 180 / Math.PI + 360) % 360, s); int i = (y * size + x) * 4;
            pixels[i] = c.B; pixels[i + 1] = c.G; pixels[i + 2] = c.R; pixels[i + 3] = 255;
        }
        var bitmap = BitmapSource.Create(size, size, 96, 96, PixelFormats.Bgra32, null, pixels, size * 4); bitmap.Freeze(); return bitmap;
    }
}
