using System.Globalization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Prism.App.Common;

namespace Prism.App.Controls;

/// <summary>A number that tweens between values (the mockups' tabular count-up).</summary>
public class NumberTicker : FrameworkElement
{
    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(
        nameof(Value), typeof(double), typeof(NumberTicker),
        new FrameworkPropertyMetadata(0d, FrameworkPropertyMetadataOptions.AffectsRender, OnValueChanged));

    public static readonly DependencyProperty ValueFormatProperty = DependencyProperty.Register(
        nameof(ValueFormat), typeof(string), typeof(NumberTicker),
        new FrameworkPropertyMetadata("F0", FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty SuffixProperty = DependencyProperty.Register(
        nameof(Suffix), typeof(string), typeof(NumberTicker),
        new FrameworkPropertyMetadata(string.Empty, FrameworkPropertyMetadataOptions.AffectsRender | FrameworkPropertyMetadataOptions.AffectsMeasure));

    public static readonly DependencyProperty PrefixProperty = DependencyProperty.Register(
        nameof(Prefix), typeof(string), typeof(NumberTicker),
        new FrameworkPropertyMetadata(string.Empty, FrameworkPropertyMetadataOptions.AffectsRender | FrameworkPropertyMetadataOptions.AffectsMeasure));

    public static readonly DependencyProperty ForegroundProperty = DependencyProperty.Register(
        nameof(Foreground), typeof(Color), typeof(NumberTicker),
        new FrameworkPropertyMetadata(ColorUtil.TextPrimary, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty FontSizeProperty = DependencyProperty.Register(
        nameof(FontSize), typeof(double), typeof(NumberTicker),
        new FrameworkPropertyMetadata(28d, FrameworkPropertyMetadataOptions.AffectsRender | FrameworkPropertyMetadataOptions.AffectsMeasure));

    public static readonly DependencyProperty FontWeightProperty = DependencyProperty.Register(
        nameof(FontWeight), typeof(FontWeight), typeof(NumberTicker),
        new FrameworkPropertyMetadata(FontWeights.SemiBold, FrameworkPropertyMetadataOptions.AffectsRender | FrameworkPropertyMetadataOptions.AffectsMeasure));

    private static readonly DependencyProperty DisplayValueProperty = DependencyProperty.Register(
        nameof(DisplayValue), typeof(double), typeof(NumberTicker),
        new FrameworkPropertyMetadata(0d, FrameworkPropertyMetadataOptions.AffectsRender | FrameworkPropertyMetadataOptions.AffectsMeasure));

    public NumberTicker()
    {
        SnapsToDevicePixels = true;
        UseLayoutRounding = true;
        Loaded += (_, _) =>
        {
            if (Math.Abs(Value - DisplayValue) > 0.001) SetDisplay(Value, animate: true);
        };
    }

    public double Value { get => (double)GetValue(ValueProperty); set => SetValue(ValueProperty, value); }
    public string ValueFormat { get => (string)GetValue(ValueFormatProperty); set => SetValue(ValueFormatProperty, value); }
    public string Suffix { get => (string)GetValue(SuffixProperty); set => SetValue(SuffixProperty, value); }
    public string Prefix { get => (string)GetValue(PrefixProperty); set => SetValue(PrefixProperty, value); }
    public Color Foreground { get => (Color)GetValue(ForegroundProperty); set => SetValue(ForegroundProperty, value); }
    public double FontSize { get => (double)GetValue(FontSizeProperty); set => SetValue(FontSizeProperty, value); }
    public FontWeight FontWeight { get => (FontWeight)GetValue(FontWeightProperty); set => SetValue(FontWeightProperty, value); }

    private double DisplayValue
    {
        get => (double)GetValue(DisplayValueProperty);
        set => SetValue(DisplayValueProperty, value);
    }

    private static void OnValueChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is NumberTicker ticker) ticker.SetDisplay((double)e.NewValue, animate: true);
    }

    private void SetDisplay(double value, bool animate)
    {
        value = double.IsFinite(value) ? value : 0;
        if (!animate || !IsLoaded || !Motion.Glides(DisplayValue, value, Math.Max(10, Math.Abs(value))))
        {
            BeginAnimation(DisplayValueProperty, null);
            DisplayValue = value;
            InvalidateMeasure();
            return;
        }
        var animation = Motion.Glide(value);
        animation.Completed += (_, _) => InvalidateMeasure();
        BeginAnimation(DisplayValueProperty, animation);
        InvalidateMeasure();
    }

    private string Text
    {
        get
        {
            if (!double.IsFinite(Value)) return Format.Dash;
            string number;
            try { number = DisplayValue.ToString(string.IsNullOrWhiteSpace(ValueFormat) ? "F0" : ValueFormat, CultureInfo.InvariantCulture); }
            catch (FormatException) { number = DisplayValue.ToString("F0", CultureInfo.InvariantCulture); }
            return Prefix + number + Suffix;
        }
    }

    private FormattedText BuildText(double pixelsPerDip) => new(
        Text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
        Typefaces.Number(FontWeight),
        Math.Max(1, FontSize), ColorUtil.Brush(Foreground), pixelsPerDip);

    protected override Size MeasureOverride(Size availableSize)
    {
        var formatted = BuildText(VisualTreeHelper.GetDpi(this).PixelsPerDip);
        return new Size(formatted.WidthIncludingTrailingWhitespace, formatted.Height);
    }

    protected override void OnRender(DrawingContext dc)
    {
        if (ActualWidth <= 0 || ActualHeight <= 0) return;
        var formatted = BuildText(VisualTreeHelper.GetDpi(this).PixelsPerDip);
        dc.DrawText(formatted, new Point(0, Math.Max(0, (ActualHeight - formatted.Height) / 2)));
    }
}
