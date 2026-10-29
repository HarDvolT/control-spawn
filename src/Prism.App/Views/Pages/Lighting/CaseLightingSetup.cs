using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Prism.App.ViewModels;
using Prism.Core.Ipc;

namespace Prism.App.Views;

/// <summary>One independent setup row for each resizable ARGB header.</summary>
public sealed class CaseLightingSetup : Expander
{
    private readonly StackPanel rows = new();
    private LightingViewModel? model;
    private string signature = "";
    private readonly Dictionary<(string, int), TextBlock> markers = new();
    public CaseLightingSetup()
    {
        Foreground = Brushes.WhiteSmoke;
        Header = "Set up case lighting"; Content = rows; Margin = new Thickness(0, 8, 0, 16);
        DataContextChanged += (_, _) =>
        {
            if (model is not null) model.PropertyChanged -= Changed;
            model = DataContext as LightingViewModel;
            if (model is not null) model.PropertyChanged += Changed;
            signature = ""; Rebuild();
        };
    }
    private void Changed(object? sender, PropertyChangedEventArgs e) => Rebuild();
    private void Rebuild()
    {
        if (model?.State is not { } state) return;
        var zones = state.Lighting.Devices.SelectMany(d => (d.Zones ?? []).Select((z, i) => (Device: d, Zone: z, Index: i)))
            .Where(p => p.Zone.MaxLeds > p.Zone.MinLeds).ToArray();
        string next = string.Join(";", zones.Select(z => $"{z.Device.Id}/{z.Index}"));
        foreach (var (key, text) in markers)
        {
            var identifying = state.Lighting.IdentifyingZones?.FirstOrDefault(z => z.Device == key.Item1 && z.Zone == key.Item2);
            text.Text = identifying is null ? "" : $"White marker: LED {identifying.LedIndex} of {identifying.Size}";
        }
        if (next == signature && rows.Children.Count > 0) return;
        signature = next; rows.Children.Clear(); markers.Clear();
        rows.Children.Add(new TextBlock { Text = "ARGB headers can start with zero LEDs. Start at 120 (or the header maximum) to light the chain, then lower the count until the last LED goes dark and add one back. You can also enter fans × LEDs per fan. Each header is saved separately.", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 12) });
        foreach (var item in zones)
        {
            var panel = new StackPanel { Margin = new Thickness(0, 0, 0, 12) }; rows.Children.Add(panel);
            panel.Children.Add(new TextBlock { Text = $"{item.Device.Name} · {item.Zone.Name}", FontWeight = FontWeights.SemiBold });
            int max = Math.Min(120, item.Zone.MaxLeds);
            var count = new TextBox { Text = item.Zone.LedCount.ToString(), Width = 64, Margin = new Thickness(4), ToolTip = "LED count" };
            var fans = new TextBox { Text = "3", Width = 44, ToolTip = "Number of fans" };
            var leds = new TextBox { Text = "8", Width = 44, ToolTip = "LEDs per fan" };
            var actions = new WrapPanel(); panel.Children.Add(actions);
            var result = new TextBlock { TextWrapping = TextWrapping.Wrap, Foreground = Brushes.LightGray };
            async Task Send(string command, int size)
            {
                if (model is null) return;
                if (size < item.Zone.MinLeds || size > max) { result.Text = $"Enter {item.Zone.MinLeds}–{max} LEDs."; return; }
                var response = await model.Client.SendAsync(command, new { device = item.Device.Id, zone = item.Index, size });
                result.Text = response.Ok ? command == Commands.IdentifyZone ? $"Identifying {size} LEDs for 30 seconds: a white marker moves through LED 1–{size}." : $"Saved {size} LEDs for this header." : response.Error;
            }
            var start = new Button { Content = $"Light up to {max}", Margin = new Thickness(4) };
            start.Click += async (_, _) => { count.Text = max.ToString(); await Send(Commands.IdentifyZone, max); };
            var identify = new Button { Content = "Identify count", Margin = new Thickness(4) };
            identify.Click += async (_, _) => { if (int.TryParse(count.Text, out var size)) await Send(Commands.IdentifyZone, size); else result.Text = "Enter a whole LED count."; };
            var save = new Button { Content = "Save count", Margin = new Thickness(4) };
            save.Click += async (_, _) => { if (int.TryParse(count.Text, out var size)) await Send(Commands.ResizeZone, size); else result.Text = "Enter a whole LED count."; };
            actions.Children.Add(start); actions.Children.Add(count); actions.Children.Add(identify); actions.Children.Add(save);
            var formula = new WrapPanel(); panel.Children.Add(formula);
            formula.Children.Add(fans); formula.Children.Add(new TextBlock { Text = " fans × ", VerticalAlignment = VerticalAlignment.Center }); formula.Children.Add(leds);
            formula.Children.Add(new TextBlock { Text = " LEDs per fan ", VerticalAlignment = VerticalAlignment.Center });
            var use = new Button { Content = "Use count", Margin = new Thickness(4) }; formula.Children.Add(use);
            use.Click += (_, _) => { if (int.TryParse(fans.Text, out int n) && int.TryParse(leds.Text, out int k) && n > 0 && k > 0 && (long)n * k <= max) count.Text = (n * k).ToString(); else result.Text = $"The total must be between 1 and {max}."; };
            panel.Children.Add(result);
            var marker = new TextBlock(); markers[(item.Device.Id, item.Index)] = marker; panel.Children.Add(marker);
        }
        if (zones.Length == 0) rows.Children.Add(new TextBlock { Text = "No adjustable ARGB headers are available. Connect the lighting controller first." });
    }
}
