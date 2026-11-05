using System.Windows.Controls;
using System.Windows.Input;
using ControlSpawn.App.Widgets;
namespace ControlSpawn.App.Views;
public partial class WidgetContent : UserControl
{
    public WidgetContent() { InitializeComponent(); }
    private void DragHeader(object sender, MouseButtonEventArgs e)
    { if (System.Windows.Window.GetWindow(this) is WidgetWindow window) window.DragWidget(); }
}
