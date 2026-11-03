using System.Text.Json;
using Prism.Core;
using Prism.Core.Ipc;
using Prism.Hardware;
using Xunit;

namespace Prism.Service.Tests;

public sealed class TelemetryContractTests
{
    [Fact]
    public void Process_rates_require_two_samples_and_reject_reused_pids()
    {
        var rates = new ProcessCpuRates();
        Assert.Null(rates.Sample([new(1, 10, "worker", 10), new(2, 20, "exiting", 50)], 1, 4));
        var result = rates.Sample([new(1, 10, "worker", 12), new(2, 30, "reused", 100), new(3, 1, "new", 50)], 3, 4);
        var measured = Assert.Single(result!);
        Assert.Equal("worker", measured.Name);
        Assert.Equal(25, measured.CpuPercent);
        Assert.Empty(rates.Sample([new(1, 10, "reset", 1)], 4, 4)!);
    }

    [Fact]
    public void Router_details_project_the_signal_endpoint_and_preserve_missing_values()
    {
        var details = RouterProjection.Details(new Dictionary<string, object?> {
            ["currentband"] = "3+20", ["PCI"] = "123", ["CELL_ID"] = "4F2A1C", ["FREQ"] = "1800",
            ["currentband_5g"] = "78", ["RSRP_5G"] = "-97", ["PCI_5G"] = "412", ["FREQ_5G"] = "636666",
            ["RSSI_5G"] = "-72", ["lte_info"] = "1800,12,-95,-11,15", ["uptime"] = "9000"
        }, "192.0.2.1");
        var wire = JsonSerializer.Deserialize<RouterDetails>(JsonSerializer.Serialize(details, PipeContract.Json), PipeContract.Json)!;
        Assert.Equal(-72, wire.Nr!.Rssi);
        Assert.Equal("636666", wire.Nr.Arfcn);
        Assert.Equal("B3+B20", wire.Lte!.Band);
        Assert.Equal("4F2A1C", wire.Lte.CellId);
        Assert.Equal(9000, wire.UptimeSeconds);
        Assert.Single(wire.Neighbors!);
        Assert.Null(wire.MonthlyBytes);
        Assert.Null(wire.TowerDistanceKm);
        Assert.Null(wire.Model);
        var missing = RouterProjection.Details(new Dictionary<string, object?>(), "192.0.2.1");
        Assert.Null(missing.Nr);
        Assert.Null(missing.Lte!.Rssi);
        Assert.Null(missing.UptimeSeconds);
    }

    [Fact]
    public void Cooking_history_ignores_disconnects_and_records_only_observed_sessions()
    {
        var history = new CookingHistory();
        var state = new ApplianceState("fryer", "Fryer", "model", true, "idle", [], [], TargetTemperature: 180, TargetMinutes: 20);
        var now = DateTimeOffset.UnixEpoch;
        Assert.Empty(history.Observe(state, now));
        Assert.Empty(history.Observe(state with { Status = "cooking" }, now.AddMinutes(1)));
        Assert.Empty(history.Observe(state with { Connected = false, Status = "offline" }, now.AddMinutes(2)));
        var cook = Assert.Single(history.Observe(state with { RemainingMinutes = 0 }, now.AddMinutes(21)));
        Assert.Equal(now.AddMinutes(1), cook.ObservedSince);
        Assert.Equal("Finished", cook.Outcome);
        Assert.Equal(180, cook.TargetTemperature);
        Assert.Single(history.Observe(state, now.AddMinutes(22)));
    }

    [Fact]
    public void Fan_configuration_is_the_engine_configuration_and_round_trips_over_IPC()
    {
        var settings = new ServiceSettings { HeaderFans = new() { [2] = new(FanMode.Silent, TemperatureSource.Gpu) } };
        var state = StateAssembler.Assemble(new(DateTimeOffset.UnixEpoch, []), settings, [2, 6, 7], [], null, null, new(true, false, [], false));
        var copy = JsonSerializer.Deserialize<StateMessage>(JsonSerializer.Serialize(state, PipeContract.Json), PipeContract.Json)!;
        var config = Assert.IsType<FanControlState>(copy.Fans.Single(f => f.Header == 2).Configuration);
        Assert.Equal(TemperatureSource.Gpu, config.Source);
        Assert.Equal(FanCurveEngine.Preset(FanMode.Silent).Points, config.Points);
        Assert.Equal(20, config.Minimum);
        Assert.Null(copy.Fans.Single(f => f.IsPump).Configuration);
        Assert.Null(copy.System.UptimeSeconds);
        Assert.Null(copy.System.TopProcesses);
        Assert.Null(copy.System.ThermalThrottling);
        Assert.Null(copy.System.ThrottlingEvents);
    }

    [Fact]
    public void Thermal_throttling_and_hardware_identity_require_explicit_reported_data()
    {
        var snapshot = new HardwareSnapshot(DateTimeOffset.UnixEpoch,
            [new("/cpu/throttle/0", "AMD Ryzen: Thermal Throttling", SensorCategory.Cpu, "Level", "", 1)]);
        var state = StateAssembler.Assemble(snapshot, new(), [], [], null, null, new(true, false, [], false));
        Assert.True(state.System.ThermalThrottling);
        Assert.Equal("AMD Ryzen", state.System.CpuName);
        Assert.Null(state.System.GpuName);
        Assert.Null(state.System.BoardName);
    }
}
