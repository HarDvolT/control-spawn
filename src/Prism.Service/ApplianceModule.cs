using Prism.Core.Ipc;
using Prism.Core;
namespace Prism.Service;
public sealed partial class ServiceRuntime
{
    private sealed class ApplianceModule(ServiceRuntime runtime) : HostedModule("appliances", ok => runtime.ModuleHealth("appliances", ok))
    {
        protected override async Task ExecuteAsync(CancellationToken token)
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(5));
            do
            {
                if (!runtime.mock && !runtime.Observe)
                {
                    var before = runtime.appliances.States;
                    await runtime.appliances.PollAsync(Volatile.Read(ref runtime.settings).Devices, token);
                    if (Volatile.Read(ref runtime.settings).Discovery.CookingLightFlash && runtime.appliances.States.Any(a => a.Status == "done" && before.Any(b => b.Id == a.Id && b.Status is "cooking" or "preheating"))) runtime.TriggerLightingEvent(LightingEventKind.Notification);
                }

            } while (await timer.WaitForNextTickAsync(token));
        }
    }
}
