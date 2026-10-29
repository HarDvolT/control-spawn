using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Prism.App.ViewModels;
using Prism.App.Widgets;
namespace Prism.App.Views;
public partial class DesktopPreview : UserControl
{
    private WidgetsViewModel? _vm;
    private readonly List<WidgetViewModel> _observed = new();
    private Border? _drag;
    private Point _start;
    private double _x, _y, _originX, _originY;
    public DesktopPreview()
    {
        InitializeComponent();
        Loaded += (_, _) => Connect();
        Unloaded += (_, _) => Disconnect();
        DataContextChanged += (_, _) => Connect();
    }
    private void Disconnect()
    {
        if (_vm is not null) _vm.Widgets.CollectionChanged -= CollectionChanged;
        foreach (var item in _observed) item.PropertyChanged -= LayoutChanged;
        _observed.Clear(); _vm = null;
    }
    private void Connect()
    {
        Disconnect(); _vm = DataContext as WidgetsViewModel;
        if (_vm is null) return;
        _vm.Widgets.CollectionChanged += CollectionChanged; Rebuild();
    }
    private void CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e) => Rebuild();
    private void LayoutChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(WidgetViewModel.Enabled) or nameof(WidgetViewModel.Width) or nameof(WidgetViewModel.PreviewLeft) or nameof(WidgetViewModel.PreviewTop)) Rebuild();
    }
    private void Rebuild()
    {
        foreach (var item in _observed) item.PropertyChanged -= LayoutChanged;
        _observed.Clear(); Surface.Children.Clear();
        if (_vm is null) return;
        _originX = Math.Min(0,_vm.Widgets.Select(w => w.Layout.X).DefaultIfEmpty(0).Min());
        _originY = Math.Min(0,_vm.Widgets.Select(w => w.Layout.Y).DefaultIfEmpty(0).Min());
        Surface.Width = Math.Max(1920,_vm.Widgets.Select(w => w.Layout.X + w.Width).DefaultIfEmpty(0).Max()) - _originX;
        Surface.Height = Math.Max(1080,_vm.Widgets.Select(w => w.Layout.Y + w.Height).DefaultIfEmpty(0).Max()) - _originY;
        foreach (var item in _vm.Widgets)
        {
            item.PropertyChanged += LayoutChanged; _observed.Add(item);
            if (!item.Enabled) continue;
            var tile = new Border { DataContext = item, Width = item.Width * (item.Kind == WidgetKind.Lamp ? 1.4 : 2.1), Background = System.Windows.Media.Brushes.Transparent,
                Child = new Viewbox { Stretch = System.Windows.Media.Stretch.Uniform, LayoutTransform = new System.Windows.Media.ScaleTransform(1, .75), IsHitTestVisible = false, Child = new WidgetMiniature(item) { Width = item.Kind == WidgetKind.Lamp ? 160 : 250 } } };
            Canvas.SetLeft(tile,(item.Layout.X - _originX) * XScale(item)); Canvas.SetTop(tile,(item.Layout.Y - _originY) * YScale(item));
            tile.MouseLeftButtonDown += BeginDrag; tile.MouseMove += MoveDrag; tile.MouseLeftButtonUp += EndDrag;
            Surface.Children.Add(tile);
        }
    }
    private double XScale(WidgetViewModel item) => Math.Max(.1, (Surface.Width - item.Width * (item.Kind == WidgetKind.Lamp ? 1.4 : 2.1)) / Math.Max(1, Surface.Width - item.Width));
    private double YScale(WidgetViewModel item) => Math.Max(.1, (Surface.Height - item.Height * 2.1) / Math.Max(1, Surface.Height - item.Height));
    private void BeginDrag(object sender, MouseButtonEventArgs e)
    {
        if (_vm is null || sender is not Border tile || tile.DataContext is not WidgetViewModel widget) return;
        _vm.Selected = widget;
        if (widget.Locked) return;
        _drag = tile; _start = e.GetPosition(Surface); _x = Canvas.GetLeft(tile); _y = Canvas.GetTop(tile);
        tile.CaptureMouse(); e.Handled = true;
    }
    private void MoveDrag(object sender, MouseEventArgs e)
    {
        if (_drag is null || e.LeftButton != MouseButtonState.Pressed) return;
        var delta = e.GetPosition(Surface) - _start;
        Canvas.SetLeft(_drag,_x + delta.X); Canvas.SetTop(_drag,_y + delta.Y);
    }
    private void EndDrag(object sender, MouseButtonEventArgs e)
    {
        if (_drag is null || _drag.DataContext is not WidgetViewModel widget) return;
        var tile = _drag; _drag = null; tile.ReleaseMouseCapture();
        var x = Canvas.GetLeft(tile) / XScale(widget) + _originX; var y = Canvas.GetTop(tile) / YScale(widget) + _originY;
        if (widget.SnapToGrid) (x,y) = WidgetGrid.SnapPoint(x,y);
        widget.MoveTo(x,y,null); Rebuild(); e.Handled = true;
    }
}
