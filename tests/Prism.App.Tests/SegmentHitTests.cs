using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Prism.App.Controls;

namespace Prism.App.Tests;

public sealed class SegmentHitTests
{
    /// <summary>The lamp Follows tiles had no background, so only the words took a click (live, 2026-11-03).</summary>
    [Fact]
    public void AFollowsTileTakesAClickAnywhereInsideIt() => Sta(() =>
    {
        var theme = (ResourceDictionary)Application.LoadComponent(new Uri("/Prism.App;component/Themes/Prism.xaml", UriKind.Relative));
        var control = new SegmentedControl { ItemsSource = new[] { "own", "pc", "average" }, ItemContainerStyle = (Style)theme["LampFollowItem"] };
        control.Resources.MergedDictionaries.Add(theme);
        control.Measure(new Size(600, 80)); control.Arrange(new Rect(0, 0, 600, 80)); control.UpdateLayout();
        foreach (var item in Enumerable.Range(0, 3).Select(i => (ListBoxItem)control.ItemContainerGenerator.ContainerFromIndex(i)))
        {
            var corner = item.TranslatePoint(new Point(6, 6), control);   // inside the padding, clear of the text
            var hit = VisualTreeHelper.HitTest(control, corner)?.VisualHit;
            while (hit is not null and not ListBoxItem) hit = VisualTreeHelper.GetParent(hit);
            Assert.Same(item, hit);
        }
    });

    private static void Sta(Action body)
    {
        System.Runtime.ExceptionServices.ExceptionDispatchInfo? failure = null;
        var thread = new Thread(() =>
        {
            try { body(); } catch (Exception e) { failure = System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(e); }
            finally { System.Windows.Threading.Dispatcher.CurrentDispatcher.InvokeShutdown(); }
        });
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); thread.Join();
        failure?.Throw();
    }
}
