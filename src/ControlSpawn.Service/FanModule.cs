using ControlSpawn.Core.Ipc;
using ControlSpawn.Core;
using ControlSpawn.Hardware;
namespace ControlSpawn.Service;
public sealed partial class ServiceRuntime
{
    private sealed class FanModule(ServiceRuntime runtime) : HostedModule("fans", ok => runtime.ModuleHealth("fans", ok))
    {
        private const string ReadOnlyWarning = "Fan control is off: this board hasn't been verified yet, so your BIOS keeps control of the fans.";
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
                    if (runtime.controls.Headers.Count == 0) runtime.warnings["fans-readonly"] = ReadOnlyWarning;
                    else runtime.warnings.TryRemove("fans-readonly", out _);
                    if (runtime.enableFanGuard)
                    {
                        runtime.fanGuard ??= new(() => new FanGuardProcess());
                        try { runtime.fanGuard.Ensure(); runtime.warnings.TryRemove("fanguard", out _); }
                        catch (Exception e) when (e is IOException or InvalidOperationException or System.ComponentModel.Win32Exception)
                        { runtime.warnings["fanguard"] = FanGuardKeeper.Warning; throw; }
                    }
                    var value = Volatile.Read(ref runtime.settings);
                    foreach (var group in value.Fans)
                    {
                        int header = group.Key == "Radiator" ? 2 : 6;
                        if (runtime.controls.Headers.Contains(header)) runtime.fans.Configure(header, group.Value.ToSettings(value.FanProfiles));
                    }
                    foreach (var group in value.HeaderFans)
                        if (runtime.controls.Headers.Contains(group.Key) && group.Key != 7) runtime.fans.Configure(group.Key, group.Value.ToSettings(value.FanProfiles));
                    bool fresh = runtime.clock.Elapsed - cycle < TimeSpan.FromSeconds(2);
                    if (!fresh) next = next with { Success = false };
                    runtime.fans.Apply(next, runtime.clock.Elapsed - previous); previous = runtime.clock.Elapsed;
                    // A guard that died since Ensure: hand back to the BIOS (finally below) and start a new one on the retry.
                    try { if (fresh) runtime.fanGuard?.Beat(); }
                    catch (IOException) { runtime.warnings["fanguard"] = FanGuardKeeper.Warning; throw; }
                    if (fresh) runtime.WatchForStalls(next);
                    var required = next.Temperature(SensorCategory.Cpu).HasValue && value.Fans.Values.All(f => FanCurveEngine.SelectTemperature(next, f.Source).HasValue);
                    lock (runtime.stateGate)
                    {
                        bool failsafe = runtime.safety.Observe(next, required);
                        if (failsafe && !runtime.failsafe) { runtime.TriggerLightingEvent(LightingEventKind.FanFailsafe); runtime.RecordFailsafe(true); }
                        runtime.failsafe = failsafe;
                    }
                } while (await timer.WaitForNextTickAsync(token));
            }
            finally { runtime.RestoreDefaults(); }
        }
    }
}
