using Prism.App.Common;
using Prism.App.Services;
using Prism.App.ViewModels;
using Prism.App.Widgets;
using Prism.Core.Ipc;

namespace Prism.App.Tests;

/// <summary>App-side fixes from docs/hardening-audit.md.</summary>
public sealed class HardeningUiTests : IDisposable
{
    private readonly string folder = Path.Combine(Path.GetTempPath(), "prism-hardening-ui-" + Guid.NewGuid().ToString("N"));
    public HardeningUiTests() => Directory.CreateDirectory(folder);
    public void Dispose() { try { Directory.Delete(folder, true); } catch (IOException) { } }
    private MainViewModel Shell(IPrismClient client) => new(client, new AppSettings(), new SettingsStore(Path.Combine(folder, "settings.json")),
        new InMemoryStartupRegistry(), new WidgetLayoutStore(Path.Combine(folder, "widgets.json")), new NullWidgetHost());

    // #3: the overheat guard runs every fan at 100%; it is not a BIOS hand-back.
    [Fact]
    public async Task FailsafeSaysAllFansAtFullSpeedNotBiosControl()
    {
        await using var client = new MockPrismClient();
        var vm = Shell(client);
        var state = client.CurrentState;
        state = state with
        {
            Health = state.Health with { Warnings = [], FanFailsafeActive = true, LastFailsafeReason = "CPU reached 91 °C" },
            Fans = state.Fans.Select(f => f.IsPump ? f with { Stalled = true, Rpm = 0 } : f).ToArray()
        };
        vm.ApplyState(state);
        Assert.Equal("Overheat guard · all fans at 100% · CPU reached 91 °C", vm.StatusText);
        Assert.Equal("Overheat guard · all fans at 100%", Format.FailsafeStatus(null));
        var bios = vm.Fans.SafetyLines.Single(l => l.Title == "BIOS hand-back");
        Assert.Equal("Armed", bios.Reading);
        var pump = vm.Fans.SafetyLines.Single(l => l.Title == "Pump");
        Assert.Equal(("Not spinning", Prism.App.Controls.StatusLevel.Critical), (pump.Reading, pump.Level));
    }

    // #7: a crash can leave settings.json or widgets.json empty; the app loads the copy its last save kept.
    [Theory]
    [InlineData("")]
    [InlineData("\0\0\0\0")]
    public void AppSettingsAndWidgetLayoutFallBackToTheirLastGoodCopy(string damaged)
    {
        var settings = new SettingsStore(Path.Combine(folder, "settings.json"));
        settings.Save(new AppSettings { StartWithWindows = true });
        settings.Save(new AppSettings { StartWithWindows = false });
        File.WriteAllText(settings.FilePath, damaged);
        Assert.True(settings.Load().StartWithWindows);

        var widgets = new WidgetLayoutStore(Path.Combine(folder, "widgets.json"));
        widgets.Save(new WidgetLayoutFile { Widgets = [new WidgetLayout { Id = "cpu", Kind = WidgetKind.Cpu }] });
        widgets.Save(new WidgetLayoutFile());
        File.WriteAllText(widgets.FilePath, damaged);
        Assert.Equal("cpu", Assert.Single(widgets.Load().Widgets).Id);
        Assert.Empty(Directory.GetFiles(folder, "*.tmp"));
    }

    // #10: the Fans page smoothing demo only listens to the render loop while it is on screen.
    [Fact]
    public void SmoothingPreviewStopsRenderingWhenHiddenOrMotionIsReduced() => Sta(() =>
    {
        bool reduced = Motion.ReduceAnimations;
        try
        {
            Motion.ReduceAnimations = false;
            var preview = new Prism.App.Controls.SmoothingPreview();
            var window = new System.Windows.Window { Content = preview, Width = 400, Height = 300, ShowActivated = false, WindowStyle = System.Windows.WindowStyle.None };
            window.Show(); Pump();
            bool systemAllowsMotion = !Motion.ReduceAnimations;   // Windows' own animation setting can turn it off
            Assert.Equal(systemAllowsMotion, preview.IsAnimating);
            window.Hide(); Pump();
            Assert.False(preview.IsAnimating);                     // minimized to the tray: no frames
            Motion.ReduceAnimations = true;
            window.Show(); Pump();
            Assert.False(preview.IsAnimating);                     // reduced motion: a still frame only
            window.Close(); Pump();
            Assert.False(preview.IsAnimating);
        }
        finally { Motion.ReduceAnimations = reduced; }
    });

    private static void Pump()
    {
        var frame = new System.Windows.Threading.DispatcherFrame();
        System.Windows.Threading.Dispatcher.CurrentDispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.ApplicationIdle, () => frame.Continue = false);
        System.Windows.Threading.Dispatcher.PushFrame(frame);
    }
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
