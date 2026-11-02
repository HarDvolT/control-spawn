using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Threading;
using Prism.App.Controls;
using Prism.App.Services;
using Prism.App.ViewModels;
using Prism.App.Views;
using Prism.Core.Ipc;

namespace Prism.App.Tests;

/// <summary>Drives the real sensor panel against the mock service, the way a user would: pick, tweak, apply, stop.</summary>
public sealed class SensorPanelTests
{
    [Fact]
    public void BindTweakApplyThenStopAgainstTheMock() => RunSta(() =>
    {
        var client = new MockPrismClient();
        var model = new LightingViewModel(client); model.Apply(client.CurrentState);
        var panel = new SensorBindingPanel { DataContext = model };

        Click(Find<Button>(panel, "Sensor target RAM 2"));
        Click(Find<Button>(panel, "Sensor CPU temperature"));
        Find<SegmentedControl>(panel, "Sensor style").SelectedIndex = 1;
        Click(Find<Button>(panel, "Palette Traffic"));
        Find<NumberStepper>(panel, "Range low").Value = 40;
        Find<NumberStepper>(panel, "Range high").Value = 90;
        var apply = Find<Button>(panel, "Apply sensor binding");
        Assert.Equal("Apply", apply.Content);
        Click(apply);
        Pump(() => client.CurrentState.Lighting.Bindings?.ContainsKey("ram2") == true);

        var b = client.CurrentState.Lighting.Bindings!["ram2"];
        Assert.Equal(("cpu.temperature", 40d, 90d, SensorStyle.FillBar), (b.Source, b.Min, b.Max, b.Style));
        Assert.Equal(["#3DDC84", "#F5A524", "#FF4D4F"], b.Palette!.Select(p => p.Color));
        Assert.Null(b.Alert);
        Assert.False(client.CurrentState.Lighting.Bindings.ContainsKey("ram"));

        // The saved binding shows on the tile, and Apply rests until something changes.
        model.Apply(client.CurrentState);
        Assert.Equal("Applied", apply.Content); Assert.False(apply.IsEnabled);
        Assert.Contains(Texts(Find<Button>(panel, "Sensor target RAM 2")), t => t.StartsWith("CPU temp · ", StringComparison.Ordinal));
        Find<NumberStepper>(panel, "Range high").Value = 95;
        Assert.True(apply.IsEnabled); Assert.Equal("Apply changes", apply.Content);

        Click(Find<Button>(panel, "Stop using sensor"));
        Pump(() => client.CurrentState.Lighting.Bindings?.ContainsKey("ram2") != true);
        model.Apply(client.CurrentState);
        Assert.Contains("Normal effect", Texts(Find<Button>(panel, "Sensor target RAM 2")));
        client.DisposeAsync().AsTask().GetAwaiter().GetResult();
    });

    [Fact]
    public void BoardHeadersAreTargetsByWhatTheyCarry() => RunSta(() =>
    {
        var client = new MockPrismClient();
        client.SendAsync(Commands.ResizeZone, new { device = "board", zone = 1, size = 48 }).GetAwaiter().GetResult();
        var model = new LightingViewModel(client); model.Apply(client.CurrentState);
        var panel = new SensorBindingPanel { DataContext = model };
        Click(Find<Button>(panel, "Sensor target Pump & radiator"));
        Click(Find<Button>(panel, "Apply sensor binding"));
        Pump(() => client.CurrentState.Lighting.Bindings?.ContainsKey("board/zone/1") == true);
        client.DisposeAsync().AsTask().GetAwaiter().GetResult();
    });

    [Fact]
    public void InvalidRangeIsRefusedWithoutSending() => RunSta(() =>
    {
        var client = new MockPrismClient();
        var model = new LightingViewModel(client); model.Apply(client.CurrentState);
        var panel = new SensorBindingPanel { DataContext = model };
        Find<NumberStepper>(panel, "Range low").Value = 200;   // above the high end
        Click(Find<Button>(panel, "Apply sensor binding"));
        Pump(() => Texts(panel).Contains("The low end must be below the high end."));
        Assert.Empty(client.CurrentState.Lighting.Bindings ?? new Dictionary<string, SensorBinding>());
        client.DisposeAsync().AsTask().GetAwaiter().GetResult();
    });

    private static void RunSta(Action body)
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

    /// <summary>Runs queued dispatcher work (the awaited sends) until the condition holds.</summary>
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
    private static List<string> Texts(DependencyObject root) => All(root).OfType<TextBlock>().Select(t => t.Text).ToList();
}
