using System.Globalization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using Prism.App.Common;

namespace Prism.App.Controls;

/// <summary>
/// Thick capsule bar with an animated fill, a soft glow and a shimmer sweep, plus a value label.
/// </summary>
public class GlowProgressBar : FrameworkElement
{
    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(
        nameof(Value), typeof(double), typeof(GlowProgressBar),
        new FrameworkPropertyMetadata(0d, FrameworkPropertyMetadataOptions.AffectsRender, OnValueChanged));

    public static readonly DependencyProperty MaximumProperty = DependencyProperty.Register(
        nameof(Maximum), typeof(double), typeof(GlowProgressBar),
        new FrameworkPropertyMetadata(100d, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty BarColorProperty = DependencyProperty.Register(
        nameof(BarColor), typeof(Color), typeof(GlowProgressBar),
        new FrameworkPropertyMetadata(ColorUtil.Accent, FrameworkPropertyMetadataOptions.AffectsRender, OnColorChanged));

    public static readonly DependencyProperty TrackColorProperty = DependencyProperty.Register(
        nameof(TrackColor), typeof(Color), typeof(GlowProgressBar),
        new FrameworkPropertyMetadata(ColorUtil.Track, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty ShowLabelProperty = DependencyProperty.Register(
        nameof(ShowLabel), typeof(bool), typeof(GlowProgressBar),
        new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty LabelFormatProperty = DependencyProperty.Register(
        nameof(LabelFormat), typeof(string), typeof(GlowProgressBar),
        new FrameworkPropertyMetadata("{0:0}%", FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty GlowProperty = DependencyProperty.Register(
        nameof(Glow), typeof(bool), typeof(GlowProgressBar),
        new FrameworkPropertyMetadata(true, FrameworkPropertyMetadataOptions.AffectsRender, OnColorChanged));

    private static readonly DependencyProperty DisplayValueProperty = DependencyProperty.Register(
        nameof(DisplayValue), typeof(double), typeof(GlowProgressBar),
        new FrameworkPropertyMetadata(0d, FrameworkPropertyMetadataOptions.AffectsRender));

    private double _phase;
    private TimeSpan _lastFrame;
    private bool _animating;
    // A 20 fps timer instead of CompositionTarget.Rendering, which would keep the whole window redrawing at 60 fps.
    private readonly System.Windows.Threading.DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1d / 20) };

    public GlowProgressBar()
    {
        SnapsToDevicePixels = true;
        UseLayoutRounding = true;
        _timer.Tick += OnFrame;
        Loaded += (_, _) => { if (Value > 0 && DisplayValue <= 0) SetDisplay(Value, animate: true); };
        Motion.WhileVisible(this, () => { if (Shimmer) _timer.Start(); }, _timer.Stop);
        MinHeight = 8;
    }

    public double Value
    {
        get => (double)GetValue(ValueProperty);
        set => SetValue(ValueProperty, value);
    }

    public double Maximum
    {
        get => (double)GetValue(MaximumProperty);
        set => SetValue(MaximumProperty, value);
    }

    public Color BarColor
    {
        get => (Color)GetValue(BarColorProperty);
        set => SetValue(BarColorProperty, value);
    }

    public Color TrackColor
    {
        get => (Color)GetValue(TrackColorProperty);
        set => SetValue(TrackColorProperty, value);
    }

    /// <summary>The moving highlight and the glide to new values. Off in desktop widgets, which redraw in software.</summary>
    public bool Shimmer { get; set; } = true;
    public bool ShowLabel
    {
        get => (bool)GetValue(ShowLabelProperty);
        set => SetValue(ShowLabelProperty, value);
    }

    public string LabelFormat
    {
        get => (string)GetValue(LabelFormatProperty);
        set => SetValue(LabelFormatProperty, value);
    }

    public bool Glow
    {
        get => (bool)GetValue(GlowProperty);
        set => SetValue(GlowProperty, value);
    }

    private double DisplayValue
    {
        get => (double)GetValue(DisplayValueProperty);
        set => SetValue(DisplayValueProperty, value);
    }

    private static void OnValueChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is GlowProgressBar bar) bar.SetDisplay((double)e.NewValue, animate: true);
    }

    private static void OnColorChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not GlowProgressBar bar) return;
        if (!bar.Glow)
        {
            bar.Effect = null;
            return;
        }
        bar.Effect = new DropShadowEffect
        {
            Color = bar.BarColor,
            BlurRadius = 14,
            ShadowDepth = 0,
            Opacity = 0.55
        };
    }

    private void SetDisplay(double value, bool animate)
    {
        value = double.IsFinite(value) ? value : 0;
        // Without shimmer (desktop widgets) values also jump: a gliding bar in a transparent window means a full software repaint every frame.
        if (!animate || !Shimmer || !IsLoaded || !Motion.Glides(DisplayValue, value, 100))
        {
            BeginAnimation(DisplayValueProperty, null);
            DisplayValue = value;
            return;
        }
        _animating = true;
        var animation = Motion.Glide(value);
        animation.Completed += (_, _) => _animating = false;
        BeginAnimation(DisplayValueProperty, animation);
    }

    private void OnFrame(object? sender, EventArgs e)
    {
        if (Motion.ReduceAnimations || !IsVisible || ActualWidth <= 0) return;
        var now = TimeSpan.FromMilliseconds(Environment.TickCount64);
        if (_lastFrame == TimeSpan.Zero) { _lastFrame = now; return; }
        var delta = (now - _lastFrame).TotalSeconds;
        _lastFrame = now;
        if (delta is <= 0 or > 0.25) return;
        _phase = (_phase + delta / 2.6) % 1;
        if (!_animating) InvalidateVisual();
    }

    protected override void OnRender(DrawingContext dc)
    {
        var width = ActualWidth;
        var height = ActualHeight;
        if (width <= 0 || height <= 0) return;

        var radius = height / 2;
        dc.DrawRoundedRectangle(ColorUtil.Brush(TrackColor), null, new Rect(0, 0, width, height), radius, radius);

        var maximum = Maximum <= 0 ? 100 : Maximum;
        var ratio = Math.Clamp(DisplayValue / maximum, 0, 1);
        var fillWidth = width * ratio;
        if (fillWidth <= 0.5) return;

        var fillRect = new Rect(0, 0, Math.Max(fillWidth, height), height);
        var geometry = new RectangleGeometry(fillRect, radius, radius);
        geometry.Freeze();
        dc.PushClip(geometry);
        dc.DrawRectangle(ColorUtil.Brush(BarColor), null, fillRect);

        if (!Motion.ReduceAnimations && fillWidth > 12)
        {
            var shimmerWidth = Math.Max(48, width * 0.28);
            var x = -shimmerWidth + _phase * (width + shimmerWidth * 2);
            var brush = new LinearGradientBrush(
                Color.FromArgb(0, 255, 255, 255),
                Color.FromArgb(0, 255, 255, 255), 0);
            brush.GradientStops.Insert(1, new GradientStop(Color.FromArgb(90, 255, 255, 255), 0.5));
            brush.StartPoint = new Point(0, 0.5);
            brush.EndPoint = new Point(1, 0.5);
            brush.Freeze();
            dc.DrawRectangle(brush, null, new Rect(x, 0, shimmerWidth, height));
        }
        dc.Pop();

        if (ShowLabel)
        {
            var text = FormatLabel();
            var formatted = new FormattedText(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                Typefaces.Number(FontWeights.SemiBold),
                Math.Max(9, Math.Min(13, height * 0.55)), ColorUtil.Brush(ColorUtil.TextPrimary),
                VisualTreeHelper.GetDpi(this).PixelsPerDip);
            dc.DrawText(formatted, new Point(Math.Max(8, width - formatted.Width - 10), (height - formatted.Height) / 2));
        }
    }

    private string FormatLabel()
    {
        try { return string.Format(CultureInfo.InvariantCulture, LabelFormat, DisplayValue); }
        catch (FormatException) { return DisplayValue.ToString("0", CultureInfo.InvariantCulture); }
    }
}
