using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Effects;
using ControlSpawn.App.Common;

namespace ControlSpawn.App.Controls;

public sealed class ColorSwatch : Button
{
    public static readonly DependencyProperty ColorHexProperty = DependencyProperty.Register(nameof(ColorHex), typeof(string), typeof(ColorSwatch), new PropertyMetadata("#000000", Changed));
    public static readonly DependencyProperty SelectedHexProperty = DependencyProperty.Register(nameof(SelectedHex), typeof(string), typeof(ColorSwatch), new PropertyMetadata("", Changed));
    public string ColorHex { get => (string)GetValue(ColorHexProperty); set => SetValue(ColorHexProperty, value); }
    public string SelectedHex { get => (string)GetValue(SelectedHexProperty); set => SetValue(SelectedHexProperty, value); }
    public ColorSwatch()
    {
        MinHeight = 30; Padding = new Thickness(0);
        var border = new FrameworkElementFactory(typeof(Border)); border.SetValue(Border.CornerRadiusProperty, new CornerRadius(9));
        border.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(BackgroundProperty)); border.SetValue(Border.BorderBrushProperty, new TemplateBindingExtension(BorderBrushProperty)); border.SetValue(Border.BorderThicknessProperty, new TemplateBindingExtension(BorderThicknessProperty));
        Template = new ControlTemplate(typeof(ColorSwatch)) { VisualTree = border };
        Loaded += (_, _) => Update();
    }
    private static void Changed(DependencyObject d, DependencyPropertyChangedEventArgs e) => ((ColorSwatch)d).Update();
    private void Update()
    {
        var color = ColorUtil.Parse(ColorHex, Colors.Transparent);
        bool selected = string.Equals(ColorHex, SelectedHex, StringComparison.OrdinalIgnoreCase);
        Background = new SolidColorBrush(color); BorderThickness = new Thickness(selected ? 2 : 0); BorderBrush = selected ? Brushes.WhiteSmoke : Brushes.Transparent;
        Effect = selected ? new DropShadowEffect { Color = color, BlurRadius = 14, ShadowDepth = 0, Opacity = .85 } : null;
        ToolTip = ColorHex; System.Windows.Automation.AutomationProperties.SetName(this, ColorHex);
    }
}

public sealed class ChoiceButton : Button
{
    public static readonly DependencyProperty ChoiceProperty = DependencyProperty.Register(nameof(Choice), typeof(object), typeof(ChoiceButton), new PropertyMetadata(null, Changed));
    public static readonly DependencyProperty SelectionProperty = DependencyProperty.Register(nameof(Selection), typeof(object), typeof(ChoiceButton), new PropertyMetadata(null, Changed));
    public object? Choice { get => GetValue(ChoiceProperty); set => SetValue(ChoiceProperty, value); }
    public object? Selection { get => GetValue(SelectionProperty); set => SetValue(SelectionProperty, value); }
    public ChoiceButton() { SetResourceReference(StyleProperty, "BaseButton"); MinHeight = 34; FontSize = 12; Loaded += (_, _) => Update(); }
    private static void Changed(DependencyObject d, DependencyPropertyChangedEventArgs e) => ((ChoiceButton)d).Update();
    private void Update()
    {
        bool selected = Selection is not null && string.Equals(Convert.ToString(Choice, CultureInfo.InvariantCulture), Convert.ToString(Selection, CultureInfo.InvariantCulture), StringComparison.OrdinalIgnoreCase);
        Background = ColorUtil.Brush(ColorUtil.Parse(selected ? "#442D1E" : "#1E2026", Colors.Transparent));
        BorderBrush = ColorUtil.Brush(ColorUtil.Parse(selected ? "#FF4D1A" : "#303239", Colors.Transparent));
        Foreground = ColorUtil.Brush(ColorUtil.Parse(selected ? "#FF8A60" : "#C9CBD1", Colors.White));
    }
}
