using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Prism.App.Common;

namespace Prism.App.Controls;

public sealed class StatusChip : Border
{
    public static readonly DependencyProperty LevelProperty = DependencyProperty.Register(nameof(Level), typeof(StatusLevel), typeof(StatusChip), new PropertyMetadata(StatusLevel.Ok, Changed));
    public static readonly DependencyProperty TextProperty = DependencyProperty.Register(nameof(Text), typeof(string), typeof(StatusChip), new PropertyMetadata("", Changed));
    public StatusLevel Level { get => (StatusLevel)GetValue(LevelProperty); set => SetValue(LevelProperty, value); }
    public string Text { get => (string)GetValue(TextProperty); set => SetValue(TextProperty, value); }
    public StatusChip() { CornerRadius = new CornerRadius(12); Padding = new Thickness(10, 5, 10, 5); Loaded += (_, _) => Update(); }
    private static void Changed(DependencyObject d, DependencyPropertyChangedEventArgs e) => ((StatusChip)d).Update();
    private void Update()
    {
        BorderBrush = Thresholds.Brush(Level); BorderThickness = new Thickness(1);
        Background = new SolidColorBrush(Color.FromArgb(24, 255, 255, 255));
        Child = new TextBlock { Text = $"{(Level is StatusLevel.Hot or StatusLevel.Critical ? "▲" : "●")} {Text}", FontSize = 11, Foreground = BorderBrush };
        Effect = Thresholds.Glow(Level);
        BeginAnimation(OpacityProperty, null);
        if (Level == StatusLevel.Critical && !Motion.ReduceAnimations && IsLoaded)
            BeginAnimation(OpacityProperty, new DoubleAnimation(.5, 1, TimeSpan.FromMilliseconds(700)) { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever });
    }
}
