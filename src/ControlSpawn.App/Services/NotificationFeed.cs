using ControlSpawn.Core.Ipc;

namespace ControlSpawn.App.Services;

/// <summary>Bounded, transition-based notifications from service state. Repeated polls never repeat an alert.</summary>
public sealed class NotificationFeed(AppSettings settings)
{
    private StateMessage? previous;
    private readonly Queue<string> messages = new();
    public IReadOnlyList<string> Messages => messages.Reverse().ToArray();
    public void Observe(StateMessage state)
    {
        bool Hot(StateMessage s) => s.System.CpuTemp >= 85 || s.System.GpuTemp >= 85;
        void Add(string message) { messages.Enqueue(message); while (messages.Count > 50) messages.Dequeue(); }
        if (settings.CheckForUpdates && Version.TryParse(state.Health.Version, out var installed) &&
            installed > typeof(NotificationFeed).Assembly.GetName().Version && previous?.Health.Version != state.Health.Version)
            Add("Control Spawn " + installed + " is installed in the service · update the desktop app");
        if (settings.NotifyRunningHot && Hot(state) && (previous is null || !Hot(previous)))
            Add("Running hot · CPU or GPU passed the warning line");
        if (previous is { } last)
        {
            if (settings.NotifyDeviceOffline)
                foreach (var device in state.Devices ?? [])
                    if (device.Status == DeviceStatus.Offline && last.Devices?.Any(d => d.Id == device.Id && d.Status == DeviceStatus.Ready) == true)
                        Add(device.Name + " went offline");
            if (settings.NotifyCookingFinished)
                foreach (var appliance in state.Appliances ?? [])
                {
                    var before = last.Appliances?.FirstOrDefault(a => a.Id == appliance.Id);
                    if (appliance.Connected && before?.Connected == true)
                    {
                        if (appliance.Status is "finished" or "completed" || appliance.Status == "idle" && appliance.RemainingMinutes == 0)
                        {
                            if (before.Status is "cooking" or "preheating") Add(appliance.Name + " finished cooking");
                        }
                        if (appliance.TurnBasket && !before.TurnBasket) Add(appliance.Name + " · shake the basket");
                    }
                }
        }
        previous = state;
    }
}
