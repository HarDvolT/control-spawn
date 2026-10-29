using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using Prism.App.Common;

namespace Prism.App.Controls;

/// <summary>
/// The design's slider: a thick capsule with no thumb and a percentage label elsewhere.
/// Drag anywhere on the track (or use the arrow keys) to change the value.
/// </summary>
public class ThickSlider : FrameworkElement
{
    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(
        nameof(Value), typeof(double), typeof(ThickSlider),
        new FrameworkPropertyMetadata(50d,
            FrameworkPropertyMetadataOptions.AffectsRender | FrameworkPropertyMetadataOptions.BindsTwoWayByDefault,
            OnValueChanged, CoerceValue));

    public static readonly DependencyProperty MinimumProperty = DependencyProperty.Register(
        nameof(Minimum), typeof(double), typeof(ThickSlider),
        new FrameworkPropertyMetadata(0d, FrameworkPropertyMetadataOptions.AffectsRender, OnRangeChanged));

    public static readonly DependencyProperty MaximumProperty = DependencyProperty.Register(
        nameof(Maximum), typeof(double), typeof(ThickSlider),
        new FrameworkPropertyMetadata(100d, FrameworkPropertyMetadataOptions.AffectsRender, OnRangeChanged));

    public static readonly DependencyProperty AccentColorProperty = DependencyProperty.Register(
        nameof(AccentColor), typeof(Color), typeof(ThickSlider),
        new FrameworkPropertyMetadata(ColorUtil.Accent, FrameworkPropertyMetadataOptions.AffectsRender, OnAccentChanged));

    public static readonly DependencyProperty TrackColorProperty = DependencyProperty.Register(
        nameof(TrackColor), typeof(Color), typeof(ThickSlider),
        new FrameworkPropertyMetadata(ColorUtil.Track, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty GlowProperty = DependencyProperty.Register(
        nameof(Glow), typeof(bool), typeof(ThickSlider),
        new FrameworkPropertyMetadata(true, FrameworkPropertyMetadataOptions.AffectsRender, OnAccentChanged));

    public static readonly DependencyProperty StepProperty = DependencyProperty.Register(
        nameof(Step), typeof(double), typeof(ThickSlider),
        new FrameworkPropertyMetadata(1d));

    public event EventHandler? ValueChanged;
    public event EventHandler? ValueCommitted;
    private bool _dragging;

    public ThickSlider()
    {
        MinHeight = 14;
        Height = 14;
        Cursor = Cursors.Hand;
        Focusable = true;
        SnapsToDevicePixels = true;
        UseLayoutRounding = true;
        ApplyGlow();
    }

    public double Value
    {
        get => (double)GetValue(ValueProperty);
        set => SetValue(ValueProperty, value);
    }

    public double Minimum
    {
        get => (double)GetValue(MinimumProperty);
        set => SetValue(MinimumProperty, value);
    }

    public double Maximum
    {
        get => (double)GetValue(MaximumProperty);
        set => SetValue(MaximumProperty, value);
    }

    public Color AccentColor
    {
        get => (Color)GetValue(AccentColorProperty);
        set => SetValue(AccentColorProperty, value);
    }

    public Color TrackColor
    {
        get => (Color)GetValue(TrackColorProperty);
        set => SetValue(TrackColorProperty, value);
    }

    public bool Glow
    {
        get => (bool)GetValue(GlowProperty);
        set => SetValue(GlowProperty, value);
    }

    public double Step
    {
        get => (double)GetValue(StepProperty);
        set => SetValue(StepProperty, value);
    }

    private static object CoerceValue(DependencyObject d, object baseValue)
    {
        var slider = (ThickSlider)d;
        var value = baseValue is double number && double.IsFinite(number) ? number : slider.Minimum;
        var low = Math.Min(slider.Minimum, slider.Maximum);
        var high = Math.Max(slider.Minimum, slider.Maximum);
        return Math.Clamp(value, low, high);
    }

    private static void OnValueChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        ((ThickSlider)d).ValueChanged?.Invoke(d, EventArgs.Empty);
        if (d is ThickSlider slider) slider.InvalidateVisual();
    }

    private static void OnRangeChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not ThickSlider slider) return;
        slider.CoerceValue(ValueProperty);
        slider.InvalidateVisual();
    }

    private static void OnAccentChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is ThickSlider slider) slider.ApplyGlow();
    }

    private void ApplyGlow()
    {
        if (!Glow)
        {
            Effect = null;
            return;
        }
        Effect = new DropShadowEffect
        {
            Color = AccentColor,
            BlurRadius = 12,
            ShadowDepth = 0,
            Opacity = 0.45
        };
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        if (!IsEnabled) return;
        Focus();
        _dragging = CaptureMouse();
        SetFromPoint(e.GetPosition(this).X);
        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (_dragging) SetFromPoint(e.GetPosition(this).X);
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        if (!_dragging) return;
        SetFromPoint(e.GetPosition(this).X);
        _dragging = false;
        ReleaseMouseCapture();
        ValueCommitted?.Invoke(this, EventArgs.Empty);
        e.Handled = true;
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        var step = Step <= 0 ? 1 : Step;
        switch (e.Key)
        {
            case Key.Left:
            case Key.Down:
                SetCurrentValue(ValueProperty, Value - step);
                e.Handled = true;
                break;
            case Key.Right:
            case Key.Up:
                SetCurrentValue(ValueProperty, Value + step);
                e.Handled = true;
                break;
            case Key.Home:
                SetCurrentValue(ValueProperty, Minimum);
                e.Handled = true;
                break;
            case Key.End:
                SetCurrentValue(ValueProperty, Maximum);
                e.Handled = true;
                break;
        }
    }

    private void SetFromPoint(double x)
    {
        var width = ActualWidth;
        if (width <= 0) return;
        var ratio = Math.Clamp(x / width, 0, 1);
        var value = Minimum + ratio * (Maximum - Minimum);
        if (Step > 0) value = Math.Round(value / Step) * Step;
        SetCurrentValue(ValueProperty, value);
    }

    protected override void OnRender(DrawingContext dc)
    {
        var width = ActualWidth;
        var height = ActualHeight;
        if (width <= 0 || height <= 0) return;

        var radius = height / 2;
        dc.DrawRoundedRectangle(ColorUtil.Brush(TrackColor), null, new Rect(0, 0, width, height), radius, radius);

        var low = Math.Min(Minimum, Maximum);
        var high = Math.Max(Minimum, Maximum);
        var span = high - low;
        var ratio = span <= 0 ? 0 : Math.Clamp((Value - low) / span, 0, 1);
        var fillWidth = width * ratio;
        if (fillWidth > 0)
        {
            var rect = new Rect(0, 0, fillWidth, height);
            var geometry = new RectangleGeometry(rect, radius, radius);
            geometry.Freeze();
            dc.PushClip(geometry);
            dc.DrawRectangle(ColorUtil.Brush(AccentColor), null, rect);
            // A soft top highlight so the capsule reads as glass.
            var highlight = new LinearGradientBrush(
                Color.FromArgb(70, 255, 255, 255), Color.FromArgb(0, 255, 255, 255), 90);
            dc.DrawRectangle(highlight, null, new Rect(0, 0, rect.Width, height * 0.45));
            dc.Pop();
        }

        if (IsKeyboardFocused)
            dc.DrawRoundedRectangle(null, new Pen(ColorUtil.Brush(ColorUtil.WithAlpha(ColorUtil.TextPrimary, 0.6)), 1),
                new Rect(0.5, 0.5, Math.Max(0, width - 1), Math.Max(0, height - 1)), radius, radius);

        if (!IsEnabled)
            dc.DrawRoundedRectangle(ColorUtil.Brush(Color.FromArgb(120, 10, 10, 11)), null,
                new Rect(0, 0, width, height), radius, radius);
    }
}
