namespace Prism.App.Widgets;
public static class WidgetPlacement
{
    public static (double X, double Y) Restore(WidgetLayout layout, double width, double height, PixelRect area)
    {
        var x = double.IsFinite(layout.X) ? layout.X : area.X;
        var y = double.IsFinite(layout.Y) ? layout.Y : area.Y;
        if (layout.SnapToGrid) { x = WidgetGrid.Snap(x); y = WidgetGrid.Snap(y); }
        return (Math.Clamp(x, area.X, Math.Max(area.X, area.X + area.Width - width)),
                Math.Clamp(y, area.Y, Math.Max(area.Y, area.Y + area.Height - height)));
    }
}
