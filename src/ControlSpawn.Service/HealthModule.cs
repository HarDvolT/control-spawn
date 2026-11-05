using System.Net.Sockets;
using ControlSpawn.Core;
using ControlSpawn.Core.Ipc;
using ControlSpawn.Hardware;
using ControlSpawn.Lighting;
using ControlSpawn.Router;

namespace ControlSpawn.Service;

public sealed partial class ServiceRuntime
{
    private sealed class HealthModule(ServiceRuntime runtime) : HostedModule("health", ok => runtime.ModuleHealth("health", ok))
    {
        protected override Task ExecuteAsync(CancellationToken token) => HealthAsync(token);
        private async Task HealthAsync(CancellationToken token)
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(10));
            do
            {
                try
                {
                    var health = runtime.healthProbe.Read(); lock (runtime.stateGate) runtime.elevated = health.Elevated;
                    foreach (var key in runtime.warnings.Keys.Where(k => k.StartsWith("health:"))) runtime.warnings.TryRemove(key, out _);
                    foreach (var warning in health.Warnings) runtime.warnings["health:" + warning] = warning;
                }
                catch (Exception) { runtime.warnings["health:query"] = "Cannot query service health."; }
            } while (await timer.WaitForNextTickAsync(token));
        }

    }
}
