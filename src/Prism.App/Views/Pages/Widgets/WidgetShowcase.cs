using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Prism.App.Common;
using Prism.App.ViewModels;
using Prism.App.Widgets;

namespace Prism.App.Views;

/// <summary>The widgets that are on, at their real size and live, on a wallpaper-like backdrop.</summary>
public sealed class WidgetShowcase : Border
{
    private readonly WrapPanel cards = new() { HorizontalAlignment = HorizontalAlignment.Left };
    private readonly TextBlock empty = new() { Text = "No widgets are on. Switch one on in the list.", Foreground = new SolidColorBrush(Color.FromRgb(163, 166, 174)), FontSize = 13, Margin = new(8) };
    private WidgetsViewModel? model;
    private string signature = "";

    public WidgetShowcase()
    {
        CornerRadius = new(14); Padding = new(18, 18, 2, 2); MinHeight = 300;
        Background = new LinearGradientBrush(Color.FromRgb(38, 22, 40), Color.FromRgb(10, 30, 46), 35);
        var stack = new Grid(); stack.Children.Add(empty); stack.Children.Add(cards); Child = stack;
        DataContextChanged += (_, _) => Attach(DataContext as WidgetsViewModel);
        Unloaded += (_, _) => Attach(null);
        Loaded += (_, _) => Attach(DataContext as WidgetsViewModel);
    }

    private void Attach(WidgetsViewModel? next)
    {
        if (model is not null)
        {
            model.Widgets.CollectionChanged -= ListChanged;
            foreach (var w in model.Widgets) w.PropertyChanged -= WidgetChanged;
        }
        model = next; signature = "";
        if (model is null) return;
        model.Widgets.CollectionChanged += ListChanged;
        foreach (var w in model.Widgets) w.PropertyChanged += WidgetChanged;
        Rebuild();
    }

    private void ListChanged(object? sender, NotifyCollectionChangedEventArgs e) => Attach(model);
    private void WidgetChanged(object? sender, PropertyChangedEventArgs e)
    { if (e.PropertyName is nameof(WidgetViewModel.Enabled) or nameof(WidgetViewModel.Size)) Rebuild(); }

    private void Rebuild()
    {
        if (model is null) return;
        var on = model.Widgets.Where(w => w.Enabled).ToList();
        var next = string.Join("|", on.Select(w => w.Id + w.Size));
        if (next == signature) return;
        signature = next; cards.Children.Clear();
        foreach (var widget in on)
            cards.Children.Add(new Viewbox { Width = widget.Width, Stretch = Stretch.Uniform, Margin = new(0, 0, 16, 16), VerticalAlignment = VerticalAlignment.Top, Child = new WidgetCard(widget) });
        empty.Visibility = on.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }
}

/// <summary>Your main screen to scale, with a block where each widget sits. Move widgets on the desktop itself.</summary>
public sealed class ScreenMap : FrameworkElement
{
    private WidgetsViewModel? model;
    public ScreenMap()
    {
        Height = 130;
        DataContextChanged += (_, _) => { model = DataContext as WidgetsViewModel; if (model is not null) foreach (var w in model.Widgets) w.PropertyChanged += (_, _) => InvalidateVisual(); InvalidateVisual(); };
    }
    protected override void OnRender(DrawingContext dc)
    {
        var area = SystemParameters.WorkArea;
        double scale = Math.Min(ActualWidth / area.Width, ActualHeight / area.Height);
        double w = area.Width * scale, h = area.Height * scale, x0 = 0, y0 = (ActualHeight - h) / 2;
        dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromRgb(16, 17, 21)), new Pen(new SolidColorBrush(Color.FromArgb(50, 255, 255, 255)), 1), new Rect(x0, y0, w, h), 6, 6);
        foreach (var widget in model?.Widgets.Where(v => v.Enabled) ?? [])
        {
            var rect = new Rect(x0 + (widget.Layout.X - area.Left) * scale, y0 + (widget.Layout.Y - area.Top) * scale, Math.Max(4, widget.Width * scale), Math.Max(4, widget.Height * scale));
            rect.Intersect(new Rect(x0, y0, w, h));
            if (rect.IsEmpty) continue;
            dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromArgb(70, 255, 77, 26)), new Pen(new SolidColorBrush(ColorUtil.Accent), 1), rect, 2, 2);
        }
    }
}
