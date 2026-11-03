using Prism.App.Services;
using Prism.App.ViewModels;
using Prism.Core.Ipc;

namespace Prism.App.Tests;

public sealed class TelemetryPresentationTests
{
    [Fact]
    public async Task Dashboard_uses_service_history_and_keeps_router_samples_separate()
    {
        await using var client = new MockPrismClient();
        var now = client.CurrentState.Timestamp;
        var state = client.CurrentState with { History = [new(now.AddSeconds(-1), 12, 42, 23, 46, 18, 1, 2), new(now, null, 43, 24, 47, 31, 2, 3)] };
        var model = new DashboardViewModel(client);
        model.Apply(state); model.Apply(state);
        Assert.Equal(new double[] { 12 }, model.Gauges[0].History);
        Assert.Equal(new double[] { 42, 43 }, model.Gauges[1].History);
        Assert.Equal(new double[] { 18, 31 }, model.PingHistory);
    }

    [Fact]
    public async Task Monitor_preserves_missing_telemetry_and_formats_real_uptime()
    {
        await using var client = new MockPrismClient();
        var model = new MonitorViewModel(client);
        var state = client.CurrentState;
        model.Apply(state with { System = state.System with { GpuLoad = null, UptimeSeconds = 309600, TopProcesses = [new(123, "measured", 12.5)] } });
        Assert.True(double.IsNaN(model.GpuLoad));
        Assert.Equal("3d 14h", model.UptimeText);
        Assert.Equal("Unavailable", model.ThrottlingText);
        Assert.Single(model.TopProcesses);
    }

    [Fact]
    public async Task Fan_history_and_nudges_preserve_the_pump_and_ordering()
    {
        await using var client = new MockPrismClient();
        var state = client.CurrentState;
        var model = new FansViewModel(client);
        model.Apply(state with { History = [new(state.Timestamp, null, null, null, null, null, null, null, new Dictionary<int, double?> { [2] = 1234 })] });
        Assert.Equal("1,234", model.Fans.First(f => f.Id == "fan:2").MinimumText);
        model.SelectedCurvePoint = 2;
        for (int i = 0; i < 30; i++) model.NudgePointCommand.Execute("hotter");
        Assert.True(model.Curve[2].Temperature < model.Curve[3].Temperature);
        model.SelectFanCommand.Execute(model.Fans.First(f => f.IsPump));
        var before = model.Curve.ToArray();
        model.NudgePointCommand.Execute("down");
        Assert.Equal(before, model.Curve);
    }
}
