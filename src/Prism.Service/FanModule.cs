using Prism.Core.Ipc;
using Prism.Core;
using Prism.Hardware;
namespace Prism.Service;
public sealed partial class ServiceRuntime
{
    private sealed class FanModule(ServiceRuntime runtime) : HostedModule("fans", ok => runtime.ModuleHealth("fans", ok))
    {
        protected override async Task ExecuteAsync(CancellationToken token)
        {
            var previous = runtime.clock.Elapsed;
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
            try
            {
                do
                {
                    HardwareSnapshot next; TimeSpan cycle;
                    lock (runtime.stateGate) { next = runtime.snapshot; cycle = runtime.sensorCycle; }
                    if (cycle < TimeSpan.Zero) continue; // No software writes until sensors and the watchdog are ready.
                    if (runtime.enableFanGuard) runtime.fanGuard ??= new FanGuardProcess();
                    runtime.fanGuard?.EnsureAlive();
                    var value = Volatile.Read(ref runtime.settings);
                    foreach (var group in value.Fans)
                    {
                        int header = group.Key == "Radiator" ? 2 : 6;
                        if (runtime.controls.Headers.Contains(header)) runtime.fans.Configure(header, group.Value.ToSettings());
                    }
                    foreach (var group in value.HeaderFans)
                        if (runtime.controls.Headers.Contains(group.Key) && group.Key != 7) runtime.fans.Configure(group.Key, group.Value.ToSettings());
                    bool fresh = runtime.clock.Elapsed - cycle < TimeSpan.FromSeconds(2);
                    if (!fresh) next = next with { Success = false };
                    runtime.fans.Apply(next, runtime.clock.Elapsed - previous); previous = runtime.clock.Elapsed;
                    if (fresh) runtime.fanGuard?.Beat();
                    var required = next.Temperature(SensorCategory.Cpu).HasValue && value.Fans.Values.All(f => FanCurveEngine.SelectTemperature(next, f.Source).HasValue);
                    lock (runtime.stateGate)
                    {
                        bool failsafe = runtime.safety.Observe(next, required);
                        if (failsafe && !runtime.failsafe) runtime.TriggerLightingEvent(LightingEventKind.FanFailsafe);
                        runtime.failsafe = failsafe;
                    }
                } while (await timer.WaitForNextTickAsync(token));
            }
            finally { runtime.RestoreDefaults(); }
        }
    }
}
