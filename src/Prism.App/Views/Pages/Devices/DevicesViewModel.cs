using Prism.App.Services;
using Prism.Core.Ipc;

namespace Prism.App.ViewModels;

public sealed record DeviceGroup(string Kind, IReadOnlyList<DeviceState> Devices);
public sealed class DevicesViewModel : PageViewModel
{
    public DevicesViewModel(IPrismClient client) : base(client)
    {
        PageTitle = "Devices";
        FindCommand = new AsyncRelayCommand(() => RunAsync(() => Client.SendAsync(Commands.FindDevices)));
    }
    public IReadOnlyList<DeviceGroup> Groups { get; private set; } = [];
    public IReadOnlyList<DeviceState> NewDevices { get; private set; } = [];
    public string Banner => NewDevices.Count == 0 ? $"{State?.Devices?.Count ?? 0} devices, one place" : $"{NewDevices.Count} new devices found";
    public AsyncRelayCommand FindCommand { get; }
    public bool ImportRequested { get; private set; }
    public void RequestImport() { ImportRequested = true; OnPropertyChanged(nameof(ImportRequested)); }
    public Task DismissAsync(string id) => RunAsync(() => Client.SendAsync(Commands.DismissDevice, new { id }));
    public async Task SetupAsync(DeviceState device)
    {
        await RunAsync(() => Client.SendAsync(Commands.SetupDevice, new { id = device.Id }));
        if (HasError) return;
        if (device.Kind == DeviceKind.RgbController) RequestNavigate(Navigation.PageKey.Lighting);
        if (device.Kind == DeviceKind.FanHeader) RequestNavigate(Navigation.PageKey.Fans);
        if (device.Kind == DeviceKind.Lamp) RequestNavigate(Navigation.PageKey.Lamp);
        if (device.Kind == DeviceKind.Appliance) RequestNavigate(Navigation.PageKey.Appliances);
    }
    protected override void Refresh()
    {
        Groups = (State?.Devices ?? []).GroupBy(d => d.Kind switch { DeviceKind.RgbController or DeviceKind.FanHeader => "PC", DeviceKind.Lamp => "Lamps", DeviceKind.Router or DeviceKind.NetworkDevice => "Network", DeviceKind.Appliance => "Appliances", _ => "Other" }).OrderBy(g => g.Key == "PC" ? 0 : g.Key == "Lamps" ? 1 : g.Key == "Network" ? 2 : 3).Select(g => new DeviceGroup(g.Key, g.OrderBy(d => d.Kind).ThenBy(d => d.Name).ToArray())).ToArray();
        // Phones and laptops on the Wi-Fi are listed, not offered for setup: Prism has nothing to control on them.
        NewDevices = (State?.NewDevices ?? []).Where(d => d.Kind != DeviceKind.NetworkDevice).ToList(); NotifyAll();
    }
}
