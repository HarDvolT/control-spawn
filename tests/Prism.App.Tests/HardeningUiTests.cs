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
}
