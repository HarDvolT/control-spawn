using System.Windows;
using System.Windows.Controls;

namespace ControlSpawn.App.Controls;

public enum CardSize { Auto, S, M, L, XL }

/// <summary>Twelve columns, collapsing to six and then one at narrow widths.</summary>
public sealed class CardGrid : Panel
{
    public static readonly DependencyProperty SpanProperty = DependencyProperty.RegisterAttached("Span", typeof(int), typeof(CardGrid),
        new FrameworkPropertyMetadata(4, FrameworkPropertyMetadataOptions.AffectsParentMeasure), v => v is int n && n is >= 1 and <= 12);
    public static int GetSpan(DependencyObject d) => (int)d.GetValue(SpanProperty);
    public static void SetSpan(DependencyObject d, int value) => d.SetValue(SpanProperty, value);
    public double Gap { get; set; } = 16;
    private readonly Dictionary<UIElement, Rect> slots = new();
    protected override Size MeasureOverride(Size available)
    {
        slots.Clear();
        double width = double.IsFinite(available.Width) ? available.Width : 1200;
        int columns = width < 600 ? 1 : width < 960 ? 6 : 12;
        double unit = Math.Max(0, (width - Gap * (columns - 1)) / columns), y = 0, rowHeight = 0;
        int column = 0;
        foreach (UIElement child in InternalChildren)
        {
            if (child.Visibility == Visibility.Collapsed) continue;
            int span = Math.Min(columns, child is GlassCard card ? card.Span : GetSpan(child));
            if (column + span > columns) { y += rowHeight + Gap; column = 0; rowHeight = 0; }
            double w = unit * span + Gap * (span - 1);
            child.Measure(new Size(w, double.PositiveInfinity));
            slots[child] = new Rect(column * (unit + Gap), y, w, child.DesiredSize.Height);
            rowHeight = Math.Max(rowHeight, child.DesiredSize.Height); column += span;
        }
        return new Size(width, y + rowHeight);
    }
    protected override Size ArrangeOverride(Size finalSize)
    {
        foreach (var row in slots.GroupBy(slot => slot.Value.Y))
        {
            var height = row.Max(slot => slot.Value.Height);
            foreach (var (child, rect) in row)
                child.Arrange(new Rect(rect.X, rect.Y, rect.Width, height));
        }
        return finalSize;
    }
}
