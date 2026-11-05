namespace ControlSpawn.Service;

public sealed partial class ServiceRuntime
{
    private sealed class RgbModule(ServiceRuntime runtime) : HostedModule("rgb", ok => runtime.ModuleHealth("rgb", ok))
    {
        // The loop itself lives on the runtime (pacing, per-part looks, the frame log); the module adds the retry boundary.
        protected override Task ExecuteAsync(CancellationToken token) => runtime.LightingAsync(token);
    }
}
