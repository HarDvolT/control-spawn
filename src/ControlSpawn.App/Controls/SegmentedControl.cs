using System.Windows;
using System.Windows.Controls;

namespace ControlSpawn.App.Controls;

/// <summary>A pill-shaped segmented control (fan modes, modem views, widget sizes).</summary>
public class SegmentedControl : ListBox
{
    static SegmentedControl()
    {
        DefaultStyleKeyProperty.OverrideMetadata(typeof(SegmentedControl), new FrameworkPropertyMetadata(typeof(SegmentedControl)));
    }

    public SegmentedControl()
    {
        SelectionMode = SelectionMode.Single;
        Focusable = false;
        BorderThickness = new Thickness(0);
    }
}
