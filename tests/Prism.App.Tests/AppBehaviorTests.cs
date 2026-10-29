using Prism.App.Common;
using Prism.App.Navigation;
using Prism.App.Services;
using Prism.App.ViewModels;
using Prism.App.Widgets;
using Prism.Core;
using Prism.Core.Ipc;

namespace Prism.App.Tests;

public sealed class AppBehaviorTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "PrismAppTests", Guid.NewGuid().ToString("N"));
    private SettingsStore Store => new(Path.Combine(_folder, "settings.json"));
    private WidgetLayoutStore Widgets => new(Path.Combine(_folder, "widgets.json"));
    public void Dispose() { if (Directory.Exists(_folder)) Directory.Delete(_folder, true); Motion.ReduceAnimations = false; }

    [Theory]
    [InlineData("--mock", true, false)]
    [InlineData("--tray", false, true)]
    [InlineData("--MOCK --tray", true, true)]
    [InlineData("unrelated", false, false)]
    public void Launch_flags_are_exact_and_case_insensitive(string arguments, bool mock, bool tray)
    {
        Assert.Equal(new LaunchOptions(mock,tray), LaunchOptions.Parse(arguments.Split(' ')));
    }

    [Fact]
    public void Preferences_round_trip_and_startup_only_calls_the_injected_fake()
    {
        var fake = new InMemoryStartupRegistry();
        var vm = new SettingsViewModel(new RecordingClient(), Store, fake, new AppSettings());
        Assert.Null(fake.LastCommand); // Constructing preferences performs no registry operation.
        vm.StartMinimizedToTray = true;
        Assert.Null(fake.LastCommand);
        vm.StartWithWindows = true;
        Assert.EndsWith(" --tray", fake.LastCommand);
        vm.OpenRgbPath = @"C:\Program Files\OpenRGB\OpenRGB.exe";
        vm.RouterEnabled = true;
        var saved = Store.Load();
        Assert.True(saved.StartWithWindows);
        Assert.True(saved.StartMinimizedToTray);
        Assert.True(saved.RouterEnabled);
        Assert.Equal(vm.OpenRgbPath, saved.OpenRgbPath);
        vm.ResetCommand.Execute(null);
        Assert.False(fake.IsEnabled);
        Assert.False(Store.Load().StartWithWindows);
    }

    [Fact]
    public void Shell_initial_search_navigation_and_commands_use_only_the_client()
    {
        var client = new RecordingClient(); var host = new NullWidgetHost();
        var vm = new MainViewModel(client, new AppSettings(), Store, new InMemoryStartupRegistry(), Widgets, host);
        Assert.NotEmpty(vm.SearchResults);
        Assert.True(vm.ShowBanner);
        vm.NavigateCommand.Execute(PageKey.Lamp);
        Assert.Same(vm.Lamp, vm.CurrentPage);
        vm.IsSearchOpen = true; vm.SearchQuery = "scene night";
        vm.ExecuteResult(vm.SelectedResult);
        Assert.False(vm.IsSearchOpen);
        Assert.Contains(client.Calls, c => c.Command == Commands.ApplyScene);
        vm.SearchQuery = "hide widgets"; vm.ExecuteResult(vm.SelectedResult);
        Assert.False(host.IsVisible);
    }

    [Fact]
    public async Task Shell_reports_command_rejection_and_exceptions()
    {
        var client = new RecordingClient { Reject = true };
        var vm = new MainViewModel(client,new AppSettings(),Store,new InMemoryStartupRegistry(),Widgets,new NullWidgetHost());
        await vm.ApplySceneAsync("Night");
        Assert.Equal("Rejected by fake",vm.StatusText);
        client.Throw = true;
        await vm.LightsOffAsync();
        Assert.Equal("Fake transport failure",vm.StatusText);
    }

    [Fact]
    public void Widget_preferences_persist_and_opacity_uses_percentage_units()
    {
        var client = new RecordingClient(); var host = new NullWidgetHost();
        var settings = new SettingsViewModel(client,Store,new InMemoryStartupRegistry(),new AppSettings());
        var vm = new WidgetsViewModel(client,Widgets,host,settings);
        vm.AddCommand.Execute(vm.Gallery.Single(g => g.Kind == WidgetKind.Ping));
        Assert.Equal(WidgetKind.Ping,vm.Selected!.Kind);
        vm.Selected.OpacityPercent = 55;
        vm.Selected.ClickThrough = true;
        vm.Selected.Locked = true;
        vm.SetSizeCommand.Execute("Large");
        var saved = Widgets.Load().Widgets.Single(w => w.Id == vm.Selected.Id);
        Assert.Equal(.55,saved.Opacity,3);
        Assert.True(saved.ClickThrough);
        Assert.True(saved.Locked);
        Assert.Equal(WidgetSize.Large,saved.Size);
        vm.RemoveCommand.Execute(null);
        Assert.DoesNotContain(Widgets.Load().Widgets,w => w.Id == saved.Id);
    }

    [Fact]
    public void Restore_clamps_after_snapping_and_handles_removed_monitors_and_nonfinite_coordinates()
    {
        var area = new PixelRect(-1919,0,1919,1079);
        var point = WidgetPlacement.Restore(new WidgetLayout { X = -1, Y = 99999 },250,200,area);
        Assert.InRange(point.X,area.X,-250);
        Assert.InRange(point.Y,0,879);
        point = WidgetPlacement.Restore(new WidgetLayout { X = double.NaN,Y = double.PositiveInfinity },250,200,area);
        Assert.True(double.IsFinite(point.X)); Assert.True(double.IsFinite(point.Y));
        Assert.Equal((0d,0d),WidgetPlacement.Restore(new WidgetLayout(),2000,2000,new PixelRect(0,0,800,600)));
    }

    [Fact]
    public async Task Widget_controls_preserve_lamp_state_and_send_effect_commands()
    {
        await using var mock = new MockPrismClient();
        await mock.SetLampAsync(true,42,"own","#3DDC84");
        var client = new RecordingClient(); var widget = new WidgetViewModel(new WidgetLayout { Kind = WidgetKind.Lamp },client);
        widget.Apply(mock.CurrentState);
        widget.LampOn = false;
        var call = client.Calls.Last();
        Assert.Equal(Commands.SetLamp,call.Command);
        var args = JsonLineProtocol.ToArgs(call.Args)!.Value;
        Assert.Equal(42,args.GetProperty("brightness").GetInt32());
        Assert.Equal("own",args.GetProperty("follow").GetString());
        widget.SelectedEffectName = "RainbowWave";
        Assert.Equal(Commands.SetLighting,client.Calls.Last().Command);
    }

    [Fact]
    public async Task Unavailable_readings_and_modem_details_are_never_invented()
    {
        await using var mock = new MockPrismClient();
        var state = mock.CurrentState with { System = mock.CurrentState.System with { CpuLoad = null, GpuLoad = null, CpuTemp = null, GpuTemp = null, RamUsedGb = null }, Router = null };
        var client = new RecordingClient();
        var dashboard = new DashboardViewModel(client); dashboard.Apply(state);
        Assert.Equal(Format.Dash,dashboard.CpuNowText);
        Assert.Equal(4, dashboard.Gauges.Count);
        Assert.All(dashboard.Gauges, gauge => {
            Assert.Equal(Format.Dash, gauge.Value);
            Assert.True(double.IsNaN(gauge.Percent));
            Assert.Equal("Unavailable", gauge.LevelText);
            Assert.Empty(gauge.History);
        });
        var monitor = new MonitorViewModel(client); monitor.Apply(state);
        Assert.StartsWith(Format.Dash, monitor.RamText);
        Assert.Empty(dashboard.CpuHistory);
        var widget = new WidgetViewModel(new WidgetLayout(),client); widget.Apply(state);
        Assert.Equal(Format.Dash,widget.CpuText); Assert.Equal(Format.Dash,widget.CpuTempText);
        var modem = new ModemViewModel(client);
        Assert.Empty(modem.Pings); Assert.Empty(modem.Neighbors);
        Assert.Equal("0 online",modem.DevicesOnlineText);
        Assert.Equal(Format.Dash,modem.SimpleRows[0].Text.Split(' ')[0]);
    }

    [Fact]
    public async Task Pump_selection_cannot_send_a_curve_or_a_mode_and_curve_minimum_is_enforced()
    {
        var client = new RecordingClient(); await using var mock = new MockPrismClient();
        var vm = new FansViewModel(client); vm.Apply(mock.CurrentState);
        vm.SelectFanCommand.Execute(vm.Fans.Single(f => f.IsPump));
        vm.SelectedModeName = "Turbo"; vm.ApplyCurveCommand.Execute(null);
        Assert.Empty(client.Calls);
        vm.SelectFanCommand.Execute(vm.Fans.Single(f => f.Name == "Case fans"));
        vm.MinSpeed = 45; vm.ApplyCurveCommand.Execute(null);
        var call = Assert.Single(client.Calls);
        var args = JsonLineProtocol.ToArgs(call.Args)!.Value;
        Assert.Equal("Case fans",args.GetProperty("group").GetString());
        Assert.All(args.GetProperty("points").EnumerateArray(),p => Assert.InRange(p[1].GetDouble(),45,100));
    }

    [Fact]
    public async Task Fan_cards_report_actual_percent_and_rotation_tracks_live_rpm()
    {
        await using var mock = new MockPrismClient();
        var vm = new FansViewModel(new RecordingClient()); vm.Apply(mock.CurrentState);
        Assert.Equal("33%", vm.MinSpeedText);
        var changed = new List<string?>(); vm.PropertyChanged += (_, e) => changed.Add(e.PropertyName);
        vm.MinSpeed = 20;
        Assert.Equal("20%", vm.MinSpeedText);
        Assert.Contains(nameof(vm.MinSpeedText), changed);
        var radiator = vm.Fans.Single(f => f.Name == "Radiator");
        var slow = vm.Fans.Single(f => f.Name == "Case fans");
        var pump = vm.Fans.Single(f => f.IsPump);
        Assert.True(pump.SpinSeconds < radiator.SpinSeconds);
        Assert.True(radiator.SpinSeconds < slow.SpinSeconds);
        Assert.Equal(100, pump.Percent);
        Assert.Contains("Header", pump.Sub);
        Assert.True(radiator.IsSelected);
        vm.SelectFanCommand.Execute(pump);
        Assert.True(pump.IsSelected);
        Assert.False(radiator.IsSelected);
        Assert.StartsWith("Fixed curve", vm.CurveSubtitle);
        Assert.All(vm.Curve, p => Assert.Equal(100, p.Percent));
    }

    [Fact]
    public void Scenes_do_not_claim_success_when_the_service_refuses()
    {
        var vm = new ScenesViewModel(new RecordingClient { Reject = true });
        vm.ApplyCommand.Execute("Night");
        Assert.Equal("Unknown",vm.ActiveScene);
        Assert.Equal("Rejected by fake",vm.StatusText);
    }

    [Fact]
    public void First_run_widgets_have_distinct_nonoverlapping_slots()
    {
        var kinds = new[] { WidgetKind.SystemMonitor,WidgetKind.Fans,WidgetKind.Lighting,WidgetKind.Modem,WidgetKind.Lamp };
        var layouts = kinds.Select((kind,index) => WidgetDefaults.Create(kind,new PixelRect(0,0,1920,1080),index)).ToArray();
        for (var a = 0; a < layouts.Length; a++) for (var b = a + 1; b < layouts.Length; b++)
        {
            var first = layouts[a]; var second = layouts[b];
            var overlaps = first.X < second.X + WidgetSizes.Width(second.Size) && first.X + WidgetSizes.Width(first.Size) > second.X
                && first.Y < second.Y + WidgetSizes.Height(second.Kind,second.Size) && first.Y + WidgetSizes.Height(first.Kind,first.Size) > second.Y;
            Assert.False(overlaps);
        }
    }

    [Fact]
    public void Metric_history_filters_ranges_deduplicates_timestamps_and_ignores_missing_samples()
    {
        var history = new MetricHistory(); var now = DateTimeOffset.UtcNow;
        history.Add(now - TimeSpan.FromHours(2),4);
        history.Add(now - TimeSpan.FromMinutes(4),20);
        history.Add(now - TimeSpan.FromSeconds(30),40);
        history.Add(now - TimeSpan.FromSeconds(30),45);
        history.Add(now,null); history.Add(now,double.NaN);
        Assert.Equal(new[] {45d},history.Read(now,TimeSpan.FromMinutes(1)));
        Assert.Equal(new[] {20d,45d},history.Read(now,TimeSpan.FromMinutes(5)));
    }

    [Fact]
    public void Tray_visibility_changes_are_reflected_in_the_widgets_page()
    {
        var client = new RecordingClient(); var host = new NullWidgetHost();
        var vm = new WidgetsViewModel(client,Widgets,host,new SettingsViewModel(client,Store,new InMemoryStartupRegistry(),new AppSettings()));
        host.HideAll(); Assert.False(vm.WidgetsVisible);
        host.ShowAll(); Assert.True(vm.WidgetsVisible);
    }

    [Fact]
    public void Secondary_instance_signals_the_primary_without_starting_another_shell()
    {
        var name = "Prism.Test." + Guid.NewGuid().ToString("N");
        using var activated = new ManualResetEventSlim();
        using var first = new SingleInstance(name, () => activated.Set());
        using var second = new SingleInstance(name, () => throw new InvalidOperationException());
        Assert.True(first.IsPrimary); Assert.False(second.IsPrimary);
        Assert.True(activated.Wait(TimeSpan.FromSeconds(3)));
    }

    [Fact]
    public void Saved_empty_layout_remains_empty_at_next_startup()
    {
        Widgets.Save(new WidgetLayoutFile());
        var client = new RecordingClient();
        var vm = new WidgetsViewModel(client,Widgets,new NullWidgetHost(),
            new SettingsViewModel(client,Store,new InMemoryStartupRegistry(),new AppSettings()));
        Assert.Empty(vm.Widgets);
    }

    [Fact]
    public void Duplicate_widget_ids_are_repaired_before_window_creation()
    {
        Widgets.Save(new WidgetLayoutFile { Widgets = new() { new() { Id = "same" }, new() { Id = "same" } } });
        var saved = Widgets.Load();
        Assert.Equal(2,saved.Widgets.Select(w => w.Id).Distinct().Count());
    }

    [Fact]
    public void Startup_failure_is_reported_without_saving_an_incorrect_enabled_preference()
    {
        var vm = new SettingsViewModel(new RecordingClient(),Store,new FailingStartup(),new AppSettings());
        vm.StartWithWindows = true;
        Assert.False(vm.StartWithWindows);
        Assert.Contains("could not be applied",vm.LastSavedText);
        Assert.False(Store.Load().StartWithWindows);
    }

    [Theory]
    [InlineData("{\"type\":\"state\"}")]
    [InlineData("{\"type\":\"state\",\"system\":{}}")]
    public void Incomplete_state_messages_are_rejected_at_the_protocol_boundary(string json)
    {
        Assert.False(JsonLineProtocol.TryParse(json,out var state,out _));
        Assert.Null(state);
    }

    private sealed class FailingStartup : IStartupRegistry
    {
        public bool IsEnabled => false;
        public void SetEnabled(bool enabled,bool minimizedToTray) => throw new IOException("Fake denied");
    }

    [Fact]
    public void Startup_command_quotes_paths_and_only_adds_tray_when_requested()
    {
        Assert.Equal("\"C:\\Program Files\\Prism.exe\" --tray",StartupCommand.Build(@"C:\Program Files\Prism.exe",true));
        Assert.DoesNotContain("--tray",StartupCommand.Build("Prism.exe",false));
    }
}

internal sealed class RecordingClient : PrismClientBase
{
    public List<(string Command,object? Args)> Calls { get; } = new();
    public bool Reject { get; set; }
    public bool Throw { get; set; }
    public override Task StartAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    public override Task<Response> SendAsync(string command, object? args = null,CancellationToken cancellationToken = default)
    {
        Calls.Add((command,args));
        if (Throw) throw new IOException("Fake transport failure");
        return Task.FromResult(new Response(1,!Reject,Reject ? "Rejected by fake" : null));
    }
    public override ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
