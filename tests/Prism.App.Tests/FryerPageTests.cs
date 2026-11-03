using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Threading;
using Prism.App.Services;
using Prism.App.ViewModels;
using Prism.App.Views;

namespace Prism.App.Tests;

public sealed class FryerPageTests
{
    [Fact]
    public void StartAsksOnThePageBeforeAnythingHeats() => Sta(() =>
    {
        var (client, vm, page) = Open();
        Click(Button(page, "Start cooking"));
        var yes = Button(page, "Yes, start cooking");
        Pump(() => Shown(yes));
        Assert.Equal("idle", client.CurrentState.Appliances![0].Status);
        Assert.Contains(All(page).OfType<TextBlock>(), t => t.Text.StartsWith("Start cooking ·", StringComparison.Ordinal));
        Click(yes);
        Pump(() => client.CurrentState.Appliances![0].Status == "cooking");
        vm.Apply(client.CurrentState);
        Assert.False(Shown(yes));
        Assert.True(Shown(Button(page, "Stop cooking")));
        client.DisposeAsync().AsTask().GetAwaiter().GetResult();
    });

    [Fact]
    public void NotYetLeavesTheFryerIdle() => Sta(() =>
    {
        var (client, _, page) = Open();
        Click(Button(page, "Start cooking"));
        Pump(() => Shown(Button(page, "Yes, start cooking")));
        Click(Button(page, "Not yet"));
        Assert.False(Shown(Button(page, "Yes, start cooking")));
        Assert.Equal("idle", client.CurrentState.Appliances![0].Status);
        client.DisposeAsync().AsTask().GetAwaiter().GetResult();
    });

    [Fact]
    public void APresetSetsTemperatureAndTimeAndLightsUp() => Sta(() =>
    {
        var (client, vm, page) = Open();
        var fries = All(page).OfType<Button>().First(b => All(b).OfType<TextBlock>().Any(t => t.Text == "Fries"));
        Click(fries);
        Pump(() => client.CurrentState.Appliances![0] is { TargetTemperature: 200, TargetMinutes: 15 });
        vm.Apply(client.CurrentState);
        Assert.Equal("on", fries.Tag);
        Assert.Contains(All(page).OfType<TextBlock>(), t => t.Text == "15:00");
        client.DisposeAsync().AsTask().GetAwaiter().GetResult();
    });

    [Fact]
    public void PauseThenResumeAsksOnceMoreAndCooksAgain() => Sta(() =>
    {
        var (client, vm, page) = Open();
        Click(Button(page, "Start cooking")); Pump(() => Shown(Button(page, "Yes, start cooking")));
        Click(Button(page, "Yes, start cooking")); Pump(() => client.CurrentState.Appliances![0].Status == "cooking"); vm.Apply(client.CurrentState);
        var pause = Button(page, "Pause");
        Click(pause);
        Assert.True(pause.Content as string is "Pausing…" or "Pause"); // The mock answers at once, so it may already be done.
        // Once the fryer accepts the pause the page says so, before the next reading arrives.
        Pump(() => All(page).OfType<TextBlock>().Any(t => t.Text == "Paused"));
        Pump(() => client.CurrentState.Appliances![0].Status == "paused"); vm.Apply(client.CurrentState);
        Click(Button(page, "Resume"));
        var yes = All(page).OfType<Button>().First(b => b.Content as string is "Yes, start cooking" or "Yes, resume");
        Pump(() => { vm.Apply(client.CurrentState); return Shown(yes); });
        Assert.Contains(All(page).OfType<TextBlock>(), t => t.Text == "Resume cooking?");
        Assert.Equal("Yes, resume", yes.Content);
        Click(yes);
        Pump(() => client.CurrentState.Appliances![0].Status == "cooking");
        client.DisposeAsync().AsTask().GetAwaiter().GetResult();
    });

    [Fact]
    public void TimeCanChangeWhileCooking() => Sta(() =>
    {
        var (client, vm, page) = Open();
        Click(Button(page, "Start cooking")); Pump(() => Shown(Button(page, "Yes, start cooking")));
        Click(Button(page, "Yes, start cooking")); Pump(() => client.CurrentState.Appliances![0].Status == "cooking"); vm.Apply(client.CurrentState);
        var before = client.CurrentState.Appliances![0].TargetMinutes;
        var plus = All(page).OfType<Button>().Where(b => b.Content is TextBlock { Text: "+" }).Last();
        Assert.True(plus.IsEnabled);
        Click(plus);
        Pump(() => client.CurrentState.Appliances![0].TargetMinutes == before + 1);
        client.DisposeAsync().AsTask().GetAwaiter().GetResult();
    });

    /// <summary>Each visit to the page left a view behind that kept updating every second.</summary>
    [Fact]
    public void APageLeftBehindStopsUpdatingAndCatchesUpWhenShownAgain() => Sta(() =>
    {
        var (client, vm, page) = Open();
        var fryer = client.CurrentState.Appliances![0];
        page.RaiseEvent(new RoutedEventArgs(FrameworkElement.UnloadedEvent));
        vm.Apply(client.CurrentState with { Appliances = [fryer with { Status = "paused" }] });
        Assert.DoesNotContain(All(page).OfType<TextBlock>(), t => t.Text == "Paused");
        page.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));
        Assert.Contains(All(page).OfType<TextBlock>(), t => t.Text == "Paused");
        client.DisposeAsync().AsTask().GetAwaiter().GetResult();
    });

    [Fact]
    public void SavedPresetsNeverShareAName()
    {
        var vm = new AppliancesViewModel(new MockPrismClient());
        vm.SavePreset(190, 11); vm.SavePreset(190, 12);
        vm.RemovePreset(vm.Presets.Single(p => p.Name == "My cook 1"));
        vm.SavePreset(190, 13);
        var mine = vm.Presets.Where(vm.IsSaved).Select(p => p.Name).ToArray();
        Assert.Equal(mine.Length, mine.Distinct().Count());
    }

    [Fact]
    public void TheShakeReminderCanChangeMidCookButPresetsWait()
    {
        var fryer = new MockPrismClient().CurrentState.Appliances![0] with { Status = "cooking" };
        Assert.True(AppliancesViewModel.CanAdjust(fryer));
        Assert.False(AppliancesViewModel.CanEdit(fryer));
    }

    [Fact]
    public void HistoryAndModelReadLikePeopleSayThem()
    {
        var now = new DateTime(2026, 11, 3, 20, 0, 0);
        Assert.Equal("Today · 13:10", AppliancesWorkspace.When(new DateTime(2026, 11, 3, 13, 10, 0), now));
        Assert.Equal("Yesterday · 19:42", AppliancesWorkspace.When(new DateTime(2026, 11, 2, 19, 42, 0), now));
        Assert.Equal("Careli MAF10A", AppliancesWorkspace.ModelName("careli.fryer.maf10a"));
    }

    private static (MockPrismClient, AppliancesViewModel, AppliancesWorkspace) Open()
    {
        var client = new MockPrismClient();
        var vm = new AppliancesViewModel(client); vm.Apply(client.CurrentState);
        return (client, vm, new AppliancesWorkspace { DataContext = vm });
    }
    private static Button Button(DependencyObject root, string text) => All(root).OfType<Button>().First(b => b.Content as string == text);
    private static bool Shown(FrameworkElement element)
    {
        for (DependencyObject? e = element; e is not null; e = LogicalTreeHelper.GetParent(e))
            if (e is UIElement { Visibility: not Visibility.Visible }) return false;
        return true;
    }
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
}
