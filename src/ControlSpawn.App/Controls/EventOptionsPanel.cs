using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using ControlSpawn.App.ViewModels;
using ControlSpawn.Core.Ipc;

namespace ControlSpawn.App.Controls;

public sealed class EventOptionsPanel : StackPanel
{
    private readonly TextBlock status = new() { TextWrapping = TextWrapping.Wrap };
    private readonly List<CheckBox> toggles = new();
    public EventOptionsPanel()
    {
        Children.Add(new TextBlock { Text = "Event effects", FontSize = 17, FontWeight = FontWeights.SemiBold, Margin = new(0, 12, 0, 8) });
        foreach (var label in new[] { "Ping spike · amber ripple", "Poor router signal · red ripple", "Fan failsafe · red strobe", "Scene change · white sweep", "Notifications" })
        {
            var toggle = new CheckBox { Foreground = Brushes.WhiteSmoke, Content = label, IsChecked = true, Margin = new(0, 5, 0, 5) }; toggles.Add(toggle); Children.Add(toggle);
            toggle.Click += async (_, _) => {
                if (DataContext is not PageViewModel vm) return;
                var options = new EventEffectOptions(toggles[0].IsChecked == true, toggles[1].IsChecked == true, toggles[2].IsChecked == true, toggles[3].IsChecked == true, toggles[4].IsChecked == true);
                try { var response = await vm.Client.SendAsync(Commands.SetEventEffects, new { events = options }); status.Text = response.Ok ? "Event effects saved" : response.Error; }
                catch (Exception e) { status.Text = e.Message; }
            };
        }
        Children.Add(status);
        DataContextChanged += (_, _) => Refresh();
        Loaded += (_, _) => { if (DataContext is PageViewModel vm) vm.PropertyChanged += Changed; Refresh(); };
        Unloaded += (_, _) => { if (DataContext is PageViewModel vm) vm.PropertyChanged -= Changed; };
    }
    private void Changed(object? sender, PropertyChangedEventArgs args) => Refresh();
    private void Refresh()
    {
        var p = (DataContext as PageViewModel)?.State?.Lighting.Events ?? new();
        var values = new[] { p.PingSpike, p.PoorSignal, p.FanFailsafe, p.SceneChange, p.Notification };
        for (int i = 0; i < values.Length; i++) toggles[i].IsChecked = values[i];
    }
}
