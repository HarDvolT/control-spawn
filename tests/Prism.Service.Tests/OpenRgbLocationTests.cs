using System.Text.Json;
using Prism.Core.Ipc;
using Prism.Hardware;
using Prism.Lighting;
using Prism.Router;
using Prism.Service;
using Xunit;

namespace Prism.Service.Tests;

public sealed class OpenRgbLocationTests
{
    private static readonly string Root = OperatingSystem.IsWindows() ? @"C:\Program Files" : "/opt/programs";
    private static string In(params string[] parts) => Path.Combine([Root, .. parts]);
    private static string Outside => OperatingSystem.IsWindows() ? @"C:\Users\Hakim\AppData\Local\Temp\orgb\OpenRGB.exe" : "/home/hakim/orgb/OpenRGB.exe";

    [Fact]
    public void OnlyPathsInsideAdminOnlyFoldersAreAllowed()
    {
        string[] folders = [Root];
        Assert.True(OpenRgbLocation.IsAllowed(In("OpenRGB", "OpenRGB.exe"), folders));
        Assert.False(OpenRgbLocation.IsAllowed(Outside, folders));
        Assert.False(OpenRgbLocation.IsAllowed(In("..", "Users", "OpenRGB.exe"), folders));   // ".." is folded first
        Assert.False(OpenRgbLocation.IsAllowed(Root + "Evil" + Path.DirectorySeparatorChar + "OpenRGB.exe", folders));   // a prefix is not a folder
        Assert.False(OpenRgbLocation.IsAllowed("OpenRGB.exe", folders));
        Assert.False(OpenRgbLocation.IsAllowed(null, folders));
        Assert.True(OpenRgbLocation.IsAllowed(Path.Combine(AppContext.BaseDirectory, "OpenRGB.exe")));   // the service folder
        Assert.DoesNotContain(OpenRgbLocation.AdminOnlyFolders(), f => f.Equals(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), StringComparison.OrdinalIgnoreCase));
    }

    private sealed class Runtime : IServiceRuntime
    {
        public StateMessage GetState(ServiceSettings settings) => throw new NotSupportedException();
        public Task ApplyAsync(ServiceSettings settings, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task<SpeedTestResult?> StartSpeedTestAsync(CancellationToken cancellationToken) => Task.FromResult<SpeedTestResult?>(null);
    }
    private static Request Options(string path, bool launch) => new(1, Commands.SetServiceOptions,
        JsonSerializer.SerializeToElement(new { openRgbPath = path, launchOpenRgb = launch, routerEnabled = false, routerHost = "192.168.0.1" }, PipeContract.Json));

    [Fact]
    public async Task CommandRefusesALaunchPathOutsideThemButStillSavesOtherOptionsWithLaunchingOff()
    {
        var store = new MemorySettingsStore(); var processor = new CommandProcessor(store, new Runtime());
        var refused = await processor.HandleAsync(Options(Outside, launch: true));
        Assert.False(refused.Ok); Assert.Equal(OpenRgbLocation.Rule, refused.Error);
        Assert.False(store.Load().LaunchOpenRgb);
        // The app sends its saved path even with launching off; that is harmless and must keep working.
        Assert.True((await processor.HandleAsync(Options(Outside, launch: false))).Ok);
        Assert.Equal(Outside, store.Load().OpenRgbPath);
    }

    [Fact]
    public async Task SavedPathOutsideThemIsKeptNotStartedAndShownAsAWarning()
    {
        var logged = new List<string>(); var previous = Prism.Core.PrismLog.WarningSink;
        Prism.Core.PrismLog.WarningSink = (_, text) => logged.Add(text);
        try
        {
            await using var runtime = new ServiceRuntime(true, new FakeSensorSource(), new FakeFanControlPort(), new FakeHealthProbe(), new FakeRgbBackend(), null, new FakeRouterBackend());
            var saved = new ServiceSettings { OpenRgbPath = Outside, LaunchOpenRgb = true };   // written by an older version
            saved.Validate();   // still loads: the folder rule is not part of the file format
            await runtime.ApplyAsync(saved, default); await runtime.ApplyAsync(saved, default);
            var warning = Assert.Single(runtime.GetState(saved).Health.Warnings, w => w.StartsWith("Not starting OpenRGB", StringComparison.Ordinal));
            Assert.Contains(Outside, warning);
            Assert.Equal([OpenRgbLocation.Warning(Outside)], logged);   // once, not on every apply
            Assert.Equal(Outside, saved.OpenRgbPath);
            await runtime.ApplyAsync(saved with { LaunchOpenRgb = false }, default);
            Assert.DoesNotContain(runtime.GetState(saved).Health.Warnings, w => w.StartsWith("Not starting OpenRGB", StringComparison.Ordinal));
        }
        finally { Prism.Core.PrismLog.WarningSink = previous; }
    }
}
