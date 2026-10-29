using System.Text.Json;
using Prism.Core;
using Prism.Core.Ipc;
using Prism.Hardware;
using Xunit;

namespace Prism.Service.Tests;

public sealed class TelemetryContractTests
{
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
