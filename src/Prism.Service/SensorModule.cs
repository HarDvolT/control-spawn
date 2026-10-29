using Prism.Core;
using Prism.Core.Ipc;
namespace Prism.Service;
public sealed partial class ServiceRuntime
{
    private sealed class SensorModule(ServiceRuntime runtime) : HostedModule("sensors", ok => runtime.ModuleHealth("sensors", ok))
    {
        protected override async Task ExecuteAsync(CancellationToken token)
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
            do
            {
                HardwareSnapshot next;
                try { next = new(DateTimeOffset.UtcNow, runtime.sensors.Read()); runtime.warnings.TryRemove("sensors", out _); }
                catch (Exception e) { next = new(DateTimeOffset.UtcNow, [], false); runtime.warnings["sensors"] = "Sensor read failed."; PrismLog.Failure("sensors", e); }
                var value = Volatile.Read(ref runtime.settings);
                lock (runtime.stateGate)
                {
                    runtime.snapshot = next;
                    runtime.sensorCycle = runtime.clock.Elapsed;
                    runtime.inputs.Temperature = next.Temperature(SensorCategory.Cpu) ?? double.NaN;
                    var system = StateAssembler.Assemble(next, value, runtime.controls.Headers, [], null, null, new(runtime.mock, false, [], runtime.failsafe)).System;
                    Volatile.Write(ref runtime.reactive, new(system.CpuLoad ?? 0, system.GpuLoad ?? 0,
                        system.RamTotalGb > 0 ? (system.RamUsedGb ?? 0) / system.RamTotalGb.Value : 0,
                        system.CpuTemp ?? double.NaN, system.GpuTemp ?? double.NaN));
                }
            } while (await timer.WaitForNextTickAsync(token));
        }
    }
}
