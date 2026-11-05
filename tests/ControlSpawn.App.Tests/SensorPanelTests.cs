using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Threading;
using ControlSpawn.App.Controls;
using ControlSpawn.App.Services;
using ControlSpawn.App.ViewModels;
using ControlSpawn.App.Views;
using ControlSpawn.Core.Ipc;

namespace ControlSpawn.App.Tests;

/// <summary>Drives the real sensor panel against the mock service, the way a user would: pick, tweak, apply, stop.</summary>
public sealed class SensorPanelTests
{
    [Fact]
    public void BindTweakApplyThenStopAgainstTheMock() => RunSta(() =>
    {
        var client = new MockControlSpawnClient();
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
        var client = new MockControlSpawnClient();
        client.SendAsync(Commands.ResizeZone, new { device = "board", zone = 1, size = 48 }).GetAwaiter().GetResult();
        var model = new LightingViewModel(client); model.Apply(client.CurrentState);
        var panel = new SensorBindingPanel { DataContext = model };
        // The AIO header is two targets: the pump ring and the radiator fans after it.
        Find<Button>(panel, "Sensor target Radiator");
        Click(Find<Button>(panel, "Sensor target Pump"));
        Click(Find<Button>(panel, "Apply sensor binding"));
        var pump = ControlSpawn.Lighting.CaseMap.Pieces(client.CurrentState.Lighting.Devices.First(d => d.Id == "board"), 1)[0].Key;
        Pump(() => client.CurrentState.Lighting.Bindings?.ContainsKey(pump) == true);
        client.DisposeAsync().AsTask().GetAwaiter().GetResult();
    });

    [Fact]
    public void EachPartChipShowsItsDriverAndClicksDoWhatItSays() => RunSta(() =>
    {
        var client = new MockControlSpawnClient();
        client.SendAsync(Commands.ResizeZone, new { device = "board", zone = 1, size = 48 }).GetAwaiter().GetResult();
        var model = new LightingViewModel(client); model.Apply(client.CurrentState);
        var workspace = new LightingWorkspace { DataContext = model };
        var board = client.CurrentState.Lighting.Devices.First(d => d.Id == "board");
        var pump = ControlSpawn.Lighting.CaseMap.Pieces(board, 1)[0].Key;
        string Driver(string part) => AutomationProperties.GetHelpText(Find<Button>(workspace, "Applies to " + part));
        void Refresh() { int ticks = 0; Pump(() => ++ticks > 3); model.Apply(client.CurrentState); }
        Assert.Equal("Case", Driver("Pump"));

        // Following the case: a click keeps the pump as it is (its own look), never dark.
        Click(Find<Button>(workspace, "Applies to Pump"));
        Pump(() => client.CurrentState.Lighting.PartLooks?.ContainsKey(pump) == true);
        Refresh();
        Assert.Equal("Own", Driver("Pump"));
        Assert.NotEqual(false, client.CurrentState.Lighting.Parameters?.Devices?.GetValueOrDefault(pump));
        // With its own look: a click hands it back to the case.
        Click(Find<Button>(workspace, "Applies to Pump"));
        Pump(() => client.CurrentState.Lighting.PartLooks?.ContainsKey(pump) != true);
        Refresh();
        Assert.Equal("Case", Driver("Pump"));

        // On a sensor: the chip says so, and a click opens the Sensor tab without dropping the sensor.
        client.SendAsync(Commands.SetSensorBinding, new { target = "gpu", source = "gpu.temperature", min = 30, max = 90 }).GetAwaiter().GetResult();
        Refresh();
        Assert.Equal("Sensor", Driver("Graphics card"));
        Assert.Contains(Texts(Find<Button>(workspace, "Applies to Graphics card")), t => t.Contains("GPU", StringComparison.OrdinalIgnoreCase));
        Click(Find<Button>(workspace, "Applies to Graphics card"));
        Refresh();
        Assert.True(client.CurrentState.Lighting.Bindings?.ContainsKey("gpu"));
        Assert.Equal(2, Find<SegmentedControl>(workspace, "Lighting mode").SelectedIndex);
        client.DisposeAsync().AsTask().GetAwaiter().GetResult();
    });

    [Fact]
    public void FreeingThePumpFromAHeaderSensorLeavesTheRadiatorOnIt() => RunSta(() =>
    {
        var client = new MockControlSpawnClient();
        client.SendAsync(Commands.ResizeZone, new { device = "board", zone = 1, size = 48 }).GetAwaiter().GetResult();
        var header = ControlSpawn.Lighting.SensorSources.ZoneTarget("board", 1);
        client.SendAsync(Commands.SetSensorBinding, new { target = header, source = "cpu.temperature", min = 30, max = 90 }).GetAwaiter().GetResult();
        var model = new LightingViewModel(client); model.Apply(client.CurrentState);
        var board = client.CurrentState.Lighting.Devices.First(d => d.Id == "board");
        var (pump, radiator) = (ControlSpawn.Lighting.CaseMap.Pieces(board, 1)[0].Key, ControlSpawn.Lighting.CaseMap.Pieces(board, 1)[1].Key);
        Assert.Equal(PartDriver.Sensor, model.DriverOf(board, 1, pump).Driver);

        // The header-wide sensor used to keep winning, so "follow the case" did nothing for the pump.
        model.FollowCaseAsync(board, 1, pump).GetAwaiter().GetResult();
        model.Apply(client.CurrentState);
        Assert.Equal(PartDriver.Case, model.DriverOf(board, 1, pump).Driver);
        Assert.Equal(PartDriver.Sensor, model.DriverOf(board, 1, radiator).Driver);
        Assert.Equal("cpu.temperature", client.CurrentState.Lighting.Bindings![radiator].Source);
        Assert.False(client.CurrentState.Lighting.Bindings.ContainsKey(header));
        client.DisposeAsync().AsTask().GetAwaiter().GetResult();
    });

    [Fact]
    public void InvalidRangeIsRefusedWithoutSending() => RunSta(() =>
    {
        var client = new MockControlSpawnClient();
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
