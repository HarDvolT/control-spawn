using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Prism.App.Common;
using System.Windows.Controls.Primitives;

namespace Prism.App.Controls;

/// <summary>Every checkbox in Prism is this orange-glowing toggle switch.</summary>
public class ToggleSwitch : ToggleButton
{
    static ToggleSwitch()
    {
        DefaultStyleKeyProperty.OverrideMetadata(typeof(ToggleSwitch), new FrameworkPropertyMetadata(typeof(ToggleSwitch)));
    }

    public override void OnApplyTemplate()
    {
        base.OnApplyTemplate();
        MoveKnob(false);
    }
    protected override void OnChecked(RoutedEventArgs e) { base.OnChecked(e); MoveKnob(true); }
    protected override void OnUnchecked(RoutedEventArgs e) { base.OnUnchecked(e); MoveKnob(true); }
    private void MoveKnob(bool animate)
    {
        if (GetTemplateChild("Knob") is FrameworkElement knob && knob.RenderTransform is TranslateTransform transform)
        {
            if (transform.IsFrozen) { transform = transform.Clone(); knob.RenderTransform = transform; }
            var target = IsChecked == true ? 20d : 0d;
            transform.BeginAnimation(TranslateTransform.XProperty, null);
            if (animate && !Motion.ReduceAnimations) transform.BeginAnimation(TranslateTransform.XProperty,
                new DoubleAnimation(target, TimeSpan.FromMilliseconds(220)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
            else transform.X = target;
        }
    }

    public ToggleSwitch()
    {
        Cursor = System.Windows.Input.Cursors.Hand;
        Focusable = true;
    }
}
