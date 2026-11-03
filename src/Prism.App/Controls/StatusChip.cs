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
    public StatusChip() { CornerRadius = new CornerRadius(16); Padding = new Thickness(12, 6, 12, 6); MinHeight = 30; Loaded += (_, _) => Update(); }
    private static void Changed(DependencyObject d, DependencyPropertyChangedEventArgs e) => ((StatusChip)d).Update();
    private void Update()
    {
        var color = Thresholds.Color(Level);
        BorderBrush = new SolidColorBrush(Color.FromArgb(85, color.R, color.G, color.B)); BorderThickness = new Thickness(1);
        Background = new SolidColorBrush(Color.FromArgb(26, color.R, color.G, color.B));
        var row = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        row.Children.Add(new LiveIndicator { Width = 7, Height = 7, Color = color, Margin = new Thickness(0, 0, 8, 0) });
        row.Children.Add(new TextBlock { Text = Text, FontSize = 12, Foreground = new SolidColorBrush(Color.FromRgb(244, 245, 247)) });
        Child = row;
        Effect = null;
        BeginAnimation(OpacityProperty, null);
        if (Level == StatusLevel.Critical && !Motion.ReduceAnimations && IsLoaded)
            BeginAnimation(OpacityProperty, Motion.Decorative(new DoubleAnimation(.5, 1, TimeSpan.FromMilliseconds(700)) { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever }, 20));
    }
}
