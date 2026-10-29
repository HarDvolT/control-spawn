using System.Windows;
using System.Windows.Controls;

namespace Prism.App.Controls;

/// <summary>Equal columns with a real gap and no trailing gutter.</summary>
public sealed class SpacedGrid : Panel
{
    public int Columns { get; set; } = 4;
    public double Gap { get; set; } = 12;
    protected override Size MeasureOverride(Size availableSize)
    {
        var columns = Math.Max(1, Columns);
        double width = double.IsFinite(availableSize.Width) ? availableSize.Width : 1200;
        double cell = Math.Max(0, (width - Gap * (columns - 1)) / columns), height = 0;
        foreach (UIElement child in InternalChildren) { child.Measure(new Size(cell, availableSize.Height)); height = Math.Max(height, child.DesiredSize.Height); }
        int rows = (InternalChildren.Count + columns - 1) / columns;
        return new Size(width, rows == 0 ? 0 : height * rows + Gap * (rows - 1));
    }
    protected override Size ArrangeOverride(Size finalSize)
    {
        int columns = Math.Max(1, Columns), rows = Math.Max(1, (InternalChildren.Count + columns - 1) / columns);
        double width = Math.Max(0, (finalSize.Width - Gap * (columns - 1)) / columns);
        double height = Math.Max(0, (finalSize.Height - Gap * (rows - 1)) / rows);
        for (int i = 0; i < InternalChildren.Count; i++) InternalChildren[i].Arrange(new Rect(i % columns * (width + Gap), i / columns * (height + Gap), width, height));
        return finalSize;
    }
}
