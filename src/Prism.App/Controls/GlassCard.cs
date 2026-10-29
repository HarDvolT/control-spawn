using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Prism.App.Common;

namespace Prism.App.Controls;

/// <summary>A glass panel that fades and rises on load (with a stagger) and lifts on hover.</summary>
public class GlassCard : ContentControl
{
    public static readonly DependencyProperty SizeProperty = DependencyProperty.Register(nameof(Size), typeof(CardSize), typeof(GlassCard),
        new FrameworkPropertyMetadata(CardSize.Auto, FrameworkPropertyMetadataOptions.AffectsMeasure, (d, e) =>
            d.SetValue(HeightProperty, (CardSize)e.NewValue switch { CardSize.S => 132d, CardSize.M => 208d, CardSize.XL => 456d, CardSize.L => 320d, _ => double.NaN })));
    public static readonly DependencyProperty SpanProperty = CardGrid.SpanProperty.AddOwner(typeof(GlassCard));
    public CardSize Size { get => (CardSize)GetValue(SizeProperty); set => SetValue(SizeProperty, value); }
    public int Span { get => (int)GetValue(SpanProperty); set => SetValue(SpanProperty, value); }

    public static readonly DependencyProperty CornerRadiusProperty = DependencyProperty.Register(
        nameof(CornerRadius), typeof(CornerRadius), typeof(GlassCard),
        new FrameworkPropertyMetadata(new CornerRadius(20)));

    public static readonly DependencyProperty StaggerIndexProperty = DependencyProperty.Register(
        nameof(StaggerIndex), typeof(int), typeof(GlassCard), new FrameworkPropertyMetadata(0));

    public static readonly DependencyProperty HoverLiftProperty = DependencyProperty.Register(
        nameof(HoverLift), typeof(bool), typeof(GlassCard), new FrameworkPropertyMetadata(true));

    static GlassCard()
    {
        DefaultStyleKeyProperty.OverrideMetadata(typeof(GlassCard), new FrameworkPropertyMetadata(typeof(GlassCard)));
    }

    public GlassCard()
    {
        Loaded += OnLoaded;
        MouseEnter += (_, _) => Lift(-2);
        MouseLeave += (_, _) => Lift(0);
    }

    public CornerRadius CornerRadius
    {
        get => (CornerRadius)GetValue(CornerRadiusProperty);
        set => SetValue(CornerRadiusProperty, value);
    }

    public int StaggerIndex
    {
        get => (int)GetValue(StaggerIndexProperty);
        set => SetValue(StaggerIndexProperty, value);
    }

    public bool HoverLift
    {
        get => (bool)GetValue(HoverLiftProperty);
        set => SetValue(HoverLiftProperty, value);
    }

    private void Lift(double y)
    {
        if (!HoverLift || Motion.ReduceAnimations) return;
        if (RenderTransform is TranslateTransform transform)
            transform.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(y, TimeSpan.FromMilliseconds(180))
            { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (Motion.ReduceAnimations)
        {
            Opacity = 1;
            return;
        }

        Opacity = 0;
        var translate = new TranslateTransform(0, 12);
        RenderTransform = translate;
        var delay = TimeSpan.FromMilliseconds(Math.Min(StaggerIndex, 12) * 40);
        var duration = TimeSpan.FromMilliseconds(620);
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };

        BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, duration)
        {
            BeginTime = delay,
            EasingFunction = ease
        });
        translate.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(12, 0, duration)
        {
            BeginTime = delay,
            EasingFunction = ease
        });
    }
}
