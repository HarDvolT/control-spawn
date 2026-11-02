using Prism.Hardware;
using Prism.Core;
using Prism.Core.Ipc;
namespace Prism.Service;
public sealed partial class ServiceRuntime
{
    private sealed class SensorModule(ServiceRuntime runtime) : HostedModule("sensors", ok => runtime.ModuleHealth("sensors", ok))
    {
        protected override async Task ExecuteAsync(CancellationToken token)
        {
            runtime.warnings["sensors"] = "Sensors are starting.";
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
            do
            {
                HardwareSnapshot next;
                var nextProcesses = runtime.mock ? null : runtime.processTelemetry.Sample();
                try { next = new(DateTimeOffset.UtcNow, runtime.sensors.Read()); runtime.warnings.TryRemove("sensors", out _); }
                catch (Exception e) { next = new(DateTimeOffset.UtcNow, [], false); runtime.warnings["sensors"] = "Sensor read failed."; PrismLog.Failure("sensors", e); }
                var value = Volatile.Read(ref runtime.settings);
                lock (runtime.stateGate)
                {
                    runtime.snapshot = next;
                    runtime.processLoads = nextProcesses;
                    runtime.sensorCycle = runtime.clock.Elapsed;
                    runtime.inputs.Temperature = next.Temperature(SensorCategory.Cpu) ?? double.NaN;
                    var system = StateAssembler.Assemble(next, value, runtime.controls.Headers, [], null, null, new(runtime.mock, false, [], runtime.failsafe)).System;
                    if (next.Success && system.ThermalThrottling is { } throttled)
                    {
                        runtime.thermalObservedSince ??= next.Timestamp;
                        if (throttled && runtime.lastThermalThrottle != true) runtime.thermalEpisodes++;
                        runtime.lastThermalThrottle = throttled;
                    }
                    if (next.Success && (runtime.sampleHistory.Count == 0 || runtime.sampleHistory.Last().Timestamp < next.Timestamp))
                    {
                        runtime.sampleHistory.Enqueue(new(next.Timestamp, system.CpuLoad, system.CpuTemp, system.GpuLoad,
                            system.GpuTemp, runtime.routerState?.PingMs, system.NetDownMbps, system.NetUpMbps,
                            FanMap.Discover(next.Sensors, runtime.controls.Headers).ToDictionary(f => f.Header,
                                f => next.Sensors.FirstOrDefault(s => s.Category == SensorCategory.Motherboard && s.Kind == "Fan" && s.Id.EndsWith($"/fan/{f.Header - 1}"))?.Value),
                            system.Disks.FirstOrDefault()?.ReadMBps, runtime.routerState?.Rsrp5g, runtime.routerState?.Sinr5g));
                        while (runtime.sampleHistory.Count > 3600) runtime.sampleHistory.Dequeue();
                    }
                    Volatile.Write(ref runtime.reactive, new(system.CpuLoad ?? 0, system.GpuLoad ?? 0,
                        system.RamTotalGb > 0 ? (system.RamUsedGb ?? 0) / system.RamTotalGb.Value : 0,
                        system.CpuTemp ?? double.NaN, system.GpuTemp ?? double.NaN));
                }
            } while (await timer.WaitForNextTickAsync(token));
        }
    }
}
