using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;

namespace Prism.App.Controls;

/// <summary>Opens a button's context menu under it on a normal click: a dropdown chip without a ComboBox.</summary>
public static class MenuOnClick
{
    public static readonly DependencyProperty EnabledProperty = DependencyProperty.RegisterAttached("Enabled", typeof(bool), typeof(MenuOnClick),
        new PropertyMetadata(false, (d, e) =>
        {
            if (d is not ButtonBase button) return;
            if ((bool)e.NewValue) button.Click += Open; else button.Click -= Open;
        }));
    public static bool GetEnabled(DependencyObject element) => (bool)element.GetValue(EnabledProperty);
    public static void SetEnabled(DependencyObject element, bool value) => element.SetValue(EnabledProperty, value);

    private static void Open(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { ContextMenu: { } menu } button) return;
        menu.PlacementTarget = button; menu.Placement = PlacementMode.Bottom; menu.IsOpen = true;
        e.Handled = true;
    }
}
