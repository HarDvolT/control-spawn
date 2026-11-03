using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Threading;
using Prism.App.Navigation;
using Prism.App.Services;
using Prism.App.Views;
using Prism.Core.Ipc;
using Prism.Lighting;

namespace Prism.App.Tests;

public sealed class AddDeviceTests
{
    private sealed class Account(params ExtractedToken[] devices) : IXiaomiTokenProvider
    {
        public string? Region;
        public Task<XiaomiSignInResult> SignInAsync(string username, string password, string region, CancellationToken token)
        { Region = region; return Task.FromResult(new XiaomiSignInResult(devices)); }
    }
    private static ExtractedToken Device(uint id, string name, string model) => new() { DeviceId = id, Name = name, Model = model, Ip = "192.0.2." + id % 200, Token = "0123456789abcdef0123456789abcdef" };

    /// <summary>The air fryer took an extractor tool and a pasted token; signing in once now adds it (2026-11-03).</summary>
    [Fact]
    public void SigningInOnceAddsTheFryerAndLeavesWhatIsAddedOrUnsupported() => Sta(() =>
    {
        var client = new MockPrismClient();
        var account = new Account(Device(685455924, "Air Fryer", "careli.fryer.maf10a"), Device(470875761, "Room lamp", "yeelink.light.color5"), Device(9, "Plug", "chuangmi.plug.m3"));
        var known = new[] { new DeviceState("lamp:470875761", DeviceKind.Lamp, "Room lamp", "miio", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, DeviceStatus.Ready, [], DeviceId: 470875761, Configured: true) };
        var panel = new AddDevicePanel { Client = client, Provider = account, KnownDevices = () => known };
        PageKey? opened = null; panel.OpenPageRequested += (_, key) => opened = key;
        panel.Open();
        Find<TextBox>(panel, "Xiaomi account").Text = "me@example.com";
        Find<PasswordBox>(panel, "Password").Password = "not-a-real-password";
        Click(Find<Button>(panel, "Sign in"));
        Pump(() => All(panel).OfType<CheckBox>().Any());
        Assert.Equal(XiaomiTokenProvider.AutoRegion, account.Region);
        Assert.Equal((true, true), (Find<CheckBox>(panel, "Air Fryer").IsChecked == true, Find<CheckBox>(panel, "Air Fryer").IsEnabled));
        Assert.False(Find<CheckBox>(panel, "Room lamp").IsChecked);   // added already
        Assert.False(Find<CheckBox>(panel, "Plug").IsEnabled);         // nothing to control on it yet
        Click(All(panel).OfType<Button>().Single(b => b.Content as string == "Add 1 device"));
        Pump(() => client.CurrentState.Devices?.Any(d => d.DeviceId == 685455924 && d.Kind == DeviceKind.Appliance) == true);
        Click(Find<Button>(panel, "Open Air fryer"));
        Assert.Equal(PageKey.Appliances, opened);
        Assert.Equal(Visibility.Collapsed, panel.Visibility);
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
