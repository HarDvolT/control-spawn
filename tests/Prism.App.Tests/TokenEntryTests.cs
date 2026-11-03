using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Threading;
using Prism.App.Services;
using Prism.App.Views;
using Prism.Core.Ipc;

namespace Prism.App.Tests;

public sealed class TokenEntryTests
{
    [Fact]
    public void PickPasteAddSavesTheTokenForTheChosenDevice() => Sta(() =>
    {
        var client = new MockPrismClient();
        var waiting = client.CurrentState.Devices!.First(d => d.Source == "miio" && d.Status == DeviceStatus.NeedsToken);
        var panel = new TokenImportPanel { Client = client, KnownDevices = () => client.CurrentState.Devices! };
        panel.Open(waiting.DeviceId);
        Assert.StartsWith(waiting.Name, Find<ComboBox>(panel, "Device").SelectedItem as string);
        var add = All(panel).OfType<Button>().Single(b => b.Content as string == "Add");
        Find<PasswordBox>(panel, "Token").Password = "0123 4567";
        Assert.False(add.IsEnabled);
        Assert.Contains(All(panel).OfType<TextBlock>(), t => t.Text == "8 of 32 characters");
        Find<PasswordBox>(panel, "Token").Password = "01234567 89abcdef 01234567 89abcdef";
        Assert.True(add.IsEnabled);
        Click(add);
        Pump(() => All(panel).OfType<TextBlock>().Any(t => t.Text.StartsWith("Saved.", StringComparison.Ordinal) || t.Text.Contains("couldn't", StringComparison.Ordinal)));
        Assert.Contains(All(panel).OfType<TextBlock>(), t => t.Text.StartsWith("Saved.", StringComparison.Ordinal));
        Assert.Equal("", Find<PasswordBox>(panel, "Token").Password);
        Assert.True(client.CurrentState.Devices!.Single(d => d.Id == waiting.Id).Configured);
        client.DisposeAsync().AsTask().GetAwaiter().GetResult();
    });

    private static void Sta(Action body)
    {
        ExceptionDispatchInfo? failure = null;
        var thread = new Thread(() =>
        {
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
            try { body(); } catch (Exception e) { failure = ExceptionDispatchInfo.Capture(e); }
            finally { Dispatcher.CurrentDispatcher.InvokeShutdown(); }
        });
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); thread.Join();
        failure?.Throw();
    }
    private static void Pump(Func<bool> done)
    {
        var until = DateTime.UtcNow.AddSeconds(5);
        while (!done() && DateTime.UtcNow < until)
        {
            var frame = new DispatcherFrame();
            Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background, () => frame.Continue = false);
            Dispatcher.PushFrame(frame); Thread.Sleep(5);
        }
        Assert.True(done());
    }
    private static void Click(ButtonBase button) => button.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent, button));
    private static IEnumerable<DependencyObject> All(DependencyObject root) =>
        LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>().SelectMany(c => All(c).Prepend(c));
    private static T Find<T>(DependencyObject root, string name) where T : FrameworkElement =>
        All(root).OfType<T>().SingleOrDefault(e => AutomationProperties.GetName(e) == name) ?? throw new Xunit.Sdk.XunitException($"No {typeof(T).Name} named '{name}'");
}
