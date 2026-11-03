using Prism.Core;
using Prism.Hardware;
using Xunit;

namespace Prism.Hardware.Tests;

public sealed class FanTests
{
    private static HardwareSnapshot Snapshot(double cpu = 45, double gpu = 40, bool success = true) => new(DateTimeOffset.UnixEpoch,
        new[] { new SensorReading("cpu", "Tctl", SensorCategory.Cpu, "Temperature", "°C", cpu),
            new SensorReading("gpu", "GPU Core", SensorCategory.Gpu, "Temperature", "°C", gpu),
            new SensorReading("board", "Board", SensorCategory.Motherboard, "Temperature", "°C", 35) }, success);

    [Theory]
    [InlineData(-10, 26)] [InlineData(20, 26)] [InlineData(35, 30)] [InlineData(50, 45)] [InlineData(55, 52.5)]
    [InlineData(68.5, 90)] [InlineData(100, 100)] [InlineData(130, 100)]
    public void StandardUsesBiosPointsAndLinearInterpolation(double temperature, double expected) =>
        Assert.Equal(expected, FanCurveEngine.Interpolate(FanCurveEngine.Preset(FanMode.Standard), temperature), 8);

    // Audit #15: a hold skips the curve minimum, so it may never stop a fan; Measure's 15% floor still works and the guard still wins.
    [Theory]
    [InlineData(0, 15)] [InlineData(-20, 15)] [InlineData(15, 15)] [InlineData(40, 40)] [InlineData(150, 100)]
    public void HoldIsClampedToFifteenToOneHundredPercent(double asked, double applied)
    {
        var port = new FakeFanControlPort(); using var fans = new FanController(port);
        fans.Hold(2, asked); fans.Apply(Snapshot(), TimeSpan.FromSeconds(1));
        Assert.Equal(applied, port.Speeds[2]);
        Assert.Equal(100, port.Speeds[7]);
        fans.Apply(Snapshot(cpu: 95), TimeSpan.FromSeconds(1));
        Assert.Equal(100, port.Speeds[2]);
    }
    [Fact]
    public void HoldRejectsNonFiniteDutyAndThePump()
    {
        using var fans = new FanController(new FakeFanControlPort());
        Assert.Throws<ArgumentOutOfRangeException>(() => fans.Hold(2, double.NaN));
        Assert.Throws<InvalidOperationException>(() => fans.Hold(7, 50));
        fans.Hold(2, null);
    }
    [Fact]
    public void CurveRejectsWrongCountAndUnorderedPoints()
    {
        Assert.Throws<ArgumentException>(() => new FanCurve(new[] { new FanPoint(20, 20) }));
        Assert.Throws<ArgumentException>(() => new FanCurve(Enumerable.Repeat(new FanPoint(20, 20), 8)));
    }
    [Fact]
    public void SmoothingLimitsBothDirectionsAndHysteresisHoldsTemperature()
    {
        var engine = new FanCurveEngine(); var settings = new FanSettings(FanCurveEngine.Preset(FanMode.Standard));
        Assert.Equal(90, engine.Evaluate(20, settings, TimeSpan.FromSeconds(1)));
        // Below the first point (30 °C) the curve holds its idle speed.
        Assert.Equal(26, engine.Evaluate(20, settings, TimeSpan.FromSeconds(20)));
        Assert.Equal(56, engine.Evaluate(70, settings, TimeSpan.FromSeconds(1)));
        Assert.Equal(100, engine.Evaluate(70, settings, TimeSpan.FromSeconds(2)));
        Assert.Equal(100, engine.Evaluate(69, settings, TimeSpan.FromSeconds(1)));
        Assert.Equal(90, engine.Evaluate(60, settings, TimeSpan.FromSeconds(1)));
    }
    [Fact]
    public void ClampsCurveToMinimumAndMaximum()
    {
        var curve = new FanCurve(Enumerable.Range(0, 8).Select(i => new FanPoint(i * 10, i < 4 ? -30 : 200)));
        var engine = new FanCurveEngine(); var settings = new FanSettings(curve, Minimum: 25);
        Assert.Equal(25, engine.Evaluate(0, settings, TimeSpan.FromSeconds(100)));
        Assert.Equal(100, engine.Evaluate(70, settings, TimeSpan.FromSeconds(100)));
        Assert.Equal(100, engine.Evaluate(double.NaN, settings, TimeSpan.FromSeconds(1)));
    }
    [Theory]
    [InlineData(TemperatureSource.Cpu, 45)] [InlineData(TemperatureSource.Gpu, 80)]
    [InlineData(TemperatureSource.Motherboard, 35)] [InlineData(TemperatureSource.Maximum, 80)]
    public void SelectsSource(TemperatureSource source, double expected) =>
        Assert.Equal((double?)expected, FanCurveEngine.SelectTemperature(Snapshot(gpu: 80), source));

    [Theory]
    [InlineData(90, 40)] [InlineData(45, 90)] [InlineData(110, 100)]
    public void HotCpuOrGpuOverridesAllCurvesImmediately(double cpu, double gpu)
    {
        var port = new FakeFanControlPort(); using var controller = new FanController(port);
        controller.Apply(Snapshot(), TimeSpan.FromSeconds(100));
        controller.Apply(Snapshot(cpu, gpu), TimeSpan.Zero);
        Assert.All(port.Speeds.Values, value => Assert.Equal(100f, value));
    }
    [Theory]
    [InlineData(95, false)] [InlineData(104, false)] [InlineData(105, true)]
    public void GpuHotspotHasItsOwnHigherLimit(double hotspot, bool expected)
    {
        var snapshot = new HardwareSnapshot(DateTimeOffset.UnixEpoch, new[] {
            new SensorReading("cpu", "Tctl", SensorCategory.Cpu, "Temperature", "°C", 45),
            new SensorReading("gpu", "GPU Core", SensorCategory.Gpu, "Temperature", "°C", 70),
            new SensorReading("hot", "GPU Hot Spot", SensorCategory.Gpu, "Temperature", "°C", hotspot) });
        Assert.Equal(expected, new FanSafety().Observe(snapshot));
    }
    [Fact]
    public void ThreeReadFailuresTriggerFailsafeAndSuccessResetsCounter()
    {
        var safety = new FanSafety();
        Assert.False(safety.Observe(Snapshot(success: false)));
        Assert.False(safety.Observe(Snapshot(success: false)));
        Assert.True(safety.Observe(Snapshot(success: false)));
        Assert.False(safety.Observe(Snapshot()));
        Assert.Equal(0, safety.ConsecutiveFailures);
        Assert.False(safety.Observe(Snapshot(), false));
        Assert.False(safety.Observe(Snapshot(), false));
        Assert.True(safety.Observe(Snapshot(), false));
    }
    [Fact]
    public void PumpCannotBeConfiguredAndStaysFullAndShutdownRestoresEveryHeader()
    {
        var port = new FakeFanControlPort(); var controller = new FanController(port);
        Assert.Throws<InvalidOperationException>(() => controller.Configure(7, new(FanCurveEngine.Preset(FanMode.Silent))));
        controller.Apply(Snapshot(), TimeSpan.FromSeconds(100));
        Assert.Equal(100f, port.Speeds[7]);
        controller.Dispose();
        Assert.Equal(port.Headers, port.Restored);
    }
    [Theory]
    [InlineData(45)] [InlineData(52)] [InlineData(55)]
    public void SilentIsBelowTheBiosIdleDutyAtIdleTemperatures(double cpu)
    {
        // The BIOS idles the radiator fans at about 36% duty on this board (measured 2026-10-29).
        Assert.True(FanCurveEngine.Interpolate(FanCurveEngine.Preset(FanMode.Silent), cpu) <= 28);
        Assert.True(FanCurveEngine.Interpolate(FanCurveEngine.Preset(FanMode.Silent), cpu) <
                    FanCurveEngine.Interpolate(FanCurveEngine.Preset(FanMode.Standard), cpu));
    }
    [Fact]
    public void MissingSelectedSourceCountsAsFailureAndControlsGoFull()
    {
        var port = new FakeFanControlPort(); using var controller = new FanController(port);
        controller.Configure(2, new(FanCurveEngine.Preset(FanMode.Silent), TemperatureSource.Gpu));
        var snapshot = new HardwareSnapshot(DateTimeOffset.UnixEpoch, Snapshot().Sensors.Where(s => s.Category != SensorCategory.Gpu).ToArray());
        for (int i = 0; i < 3; i++) controller.Apply(snapshot, TimeSpan.FromSeconds(100));
        Assert.All(port.Speeds.Values, value => Assert.Equal(100f, value));
    }
    [Fact]
    public void RestoreContinuesAfterOneControlFails()
    {
        var port = new FaultPort(); using var controller = new FanController(port);
        Assert.Throws<IOException>(() => controller.Apply(Snapshot(), TimeSpan.FromSeconds(1)));
        Assert.Equal(new[] { 1, 2, 7 }, port.Restored);
    }
    private sealed class FaultPort : IFanControlPort
    {
        public IReadOnlyList<int> Headers => new[] { 1, 2, 7 };
        public List<int> Restored { get; } = new();
        public void SetSoftware(int header, float percent) => throw new IOException("Fake failure");
        public void SetDefault(int header) { Restored.Add(header); if (header == 1) throw new IOException("Fake cleanup failure"); }
    }
    [Fact]
    public void FanMapKeepsKnownHeadersAndDiscoversOnlyNonzeroEmptyHeaders()
    {
        var sensors = new[] {
            new SensorReading("/nct6701d/0/fan/0", "Fan #1", SensorCategory.Motherboard, "Fan", "rpm", 500),
            new SensorReading("/nct6701d/0/fan/2", "Fan #3", SensorCategory.Motherboard, "Fan", "rpm", 0) };
        var fans = FanMap.Discover(sensors, Enumerable.Range(1, 7).ToArray());
        Assert.Equal(new[] { 1, 2, 6, 7 }, fans.Select(f => f.Header));
        Assert.Equal("Fan header 1", fans[0].Name); Assert.False(fans[^1].CanControl);
    }
    [Fact]
    public void MemoryTotalIsDerivedFromPhysicalUsedAndAvailableAndMissingStaysNull()
    {
        var readings = new[] {
            new SensorReading("/ram/data/0", "Used", SensorCategory.Memory, "Data", "GiB", 12),
            new SensorReading("/ram/data/1", "Available", SensorCategory.Memory, "Data", "GiB", 20) };
        Assert.Equal((double?)32, SensorProjection.WithMemoryTotal(readings)[^1].Value);
        Assert.Null(SensorProjection.WithMemoryTotal(Array.Empty<SensorReading>())[^1].Value);
    }
    [Fact]
    public void MockCompositionUsesNoRealAdaptersAndServiceCheckOnlyQueriesFake()
    {
        var parts = HardwareComposition.Create(new[] { "--mock" });
        Assert.IsType<FakeSensorSource>(parts.Sensors); Assert.IsType<FakeFanControlPort>(parts.Controls);
        var check = new AsusServiceCheck(new FakeServiceQuery { States = new[] {
            new ServiceState("LightingService", true), new ServiceState("iCUEUpdateService", true), new ServiceState("Other", true) } });
        Assert.Equal(2, check.Read().Count);
    }
    [Fact]
    public async Task MonitorPublishesOneSnapshotOnEachPollWithoutHardware()
    {
        using var cancel = new CancellationTokenSource();
        await using var monitor = new LhmHardwareMonitor(new FakeSensorSource());
        int count = 0;
        monitor.Snapshot += (_, snapshot) => { Assert.True(snapshot.Success); count++; cancel.Cancel(); };
        await monitor.RunAsync(cancel.Token);
        Assert.Equal(1, count);
    }
}
