using ControlSpawn.Core;

namespace ControlSpawn.Service;

public sealed partial class ServiceRuntime
{
    private sealed class RgbModule(ServiceRuntime runtime) : HostedModule("rgb", ok => runtime.ModuleHealth("rgb", ok))
    {
        // The loop itself lives on the runtime (pacing, per-part looks, the frame log); the module adds the retry boundary.
        protected override async Task ExecuteAsync(CancellationToken token)
        {
            try { await runtime.LightingAsync(token); }
            finally
            {
                // The runtime releases the process handle after its modules have stopped.
                if (runtime.rgbLaunch is { } launch)
                {
                    runtime.rgbLaunch = null;
                    try { await launch; }
                    catch (OperationCanceledException) when (token.IsCancellationRequested) { }
                    catch (Exception e) { ControlSpawnLog.Failure("rgb", e); }
                }
            }
        }
    }
}
