using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Data;
using System.Windows.Media;
using ControlSpawn.App.Services;
using ControlSpawn.Core.Ipc;

namespace ControlSpawn.App.Controls;

/// <summary>Shared inline rename. Draft survives state pushes; Enter saves, Escape cancels.</summary>
public sealed class DeviceNameEditor : UserControl
{
    public static readonly DependencyProperty DeviceIdProperty = DependencyProperty.Register(nameof(DeviceId), typeof(string), typeof(DeviceNameEditor), new PropertyMetadata(""));
    public static readonly DependencyProperty DisplayNameProperty = DependencyProperty.Register(nameof(DisplayName), typeof(string), typeof(DeviceNameEditor), new PropertyMetadata("Device", Changed));
    public static readonly DependencyProperty ClientProperty = DependencyProperty.Register(nameof(Client), typeof(IControlSpawnClient), typeof(DeviceNameEditor));
    public string DeviceId { get => (string)GetValue(DeviceIdProperty); set => SetValue(DeviceIdProperty, value); }
    public string DisplayName { get => (string)GetValue(DisplayNameProperty); set => SetValue(DisplayNameProperty, value); }
    public IControlSpawnClient? Client { get => (IControlSpawnClient?)GetValue(ClientProperty); set => SetValue(ClientProperty, value); }
    private readonly TextBlock label = new() { VerticalAlignment = VerticalAlignment.Center, Foreground = Brushes.WhiteSmoke, TextWrapping = TextWrapping.Wrap };
    private readonly TextBox edit = new() { MaxLength = 40, MinWidth = 100, Visibility = Visibility.Collapsed };
    private readonly TextBlock error = new() { Visibility = Visibility.Collapsed, Foreground = Brushes.OrangeRed, TextWrapping = TextWrapping.Wrap };
    public DeviceNameEditor()
    {
        label.SetBinding(TextBlock.FontSizeProperty, new Binding("FontSize") { Source = this });
        label.SetBinding(TextBlock.FontWeightProperty, new Binding("FontWeight") { Source = this });
        var root = new StackPanel(); var row = new StackPanel { Orientation = Orientation.Horizontal };
        var pencil = new Button { Content = "✎", ToolTip = "Rename", Margin = new(8, 0, 4, 0), Padding = new(4), MinWidth = 28, MinHeight = 28 };
        var reset = new Button { Content = "↶", ToolTip = "Reset to detected name", Padding = new(4), MinWidth = 28, MinHeight = 28 };
        pencil.SetResourceReference(StyleProperty, "TextAction");
        pencil.MinWidth = 16; pencil.MinHeight = 20; pencil.Padding = new Thickness(0); pencil.FontSize = 12;
        pencil.Foreground = new SolidColorBrush(Color.FromRgb(124, 128, 138));
        reset.Visibility = Visibility.Collapsed;
        var menu = new ContextMenu();
        var resetItem = new MenuItem { Header = "Reset to detected name" }; resetItem.Click += async (_, _) => await SaveAsync(true);
        menu.Items.Add(resetItem); pencil.ContextMenu = menu;
        pencil.Click += (_, _) => { edit.Text = DisplayName; label.Visibility = Visibility.Collapsed; edit.Visibility = Visibility.Visible; edit.Focus(); edit.SelectAll(); };
        reset.Click += async (_, _) => await SaveAsync(true);
        edit.KeyDown += async (_, e) => { if (e.Key == Key.Escape) Cancel(); if (e.Key == Key.Enter) { e.Handled = true; await SaveAsync(false); } };
        row.Children.Add(label); row.Children.Add(edit); row.Children.Add(pencil); row.Children.Add(reset); root.Children.Add(row); root.Children.Add(error); Content = root;
    }
    private static void Changed(DependencyObject d, DependencyPropertyChangedEventArgs e) => ((DeviceNameEditor)d).label.Text = (string)e.NewValue;
    private void Cancel() { edit.Text = ""; edit.Visibility = Visibility.Collapsed; label.Visibility = Visibility.Visible; error.Text = ""; error.Visibility = Visibility.Collapsed; }
    private async Task SaveAsync(bool reset)
    {
        if (Client is null) return;
        try
        {
            var response = await Client.SendAsync(Commands.RenameDevice, new { id = DeviceId, name = edit.Text, reset });
            if (!response.Ok) { error.Text = response.Error; error.Visibility = Visibility.Visible; return; }
            Cancel();
        }
        catch { error.Text = "Name could not be saved."; error.Visibility = Visibility.Visible; }
    }
}
