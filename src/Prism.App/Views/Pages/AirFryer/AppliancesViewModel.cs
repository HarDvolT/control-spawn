using System.Text.Json;
using Prism.App.Services;
using Prism.Core.Ipc;

namespace Prism.App.ViewModels;

public sealed class AppliancesViewModel : PageViewModel
{
    public AppliancesViewModel(IPrismClient client) : base(client) { PageTitle = "Air fryer"; }
    public IReadOnlyList<ApplianceState> Appliances => State?.Appliances ?? [];
    public async Task<string?> PrepareAsync(ApplianceState appliance, ApplianceActionState action)
    {
        string? nonce = null;
        await RunAsync(async () => { var r = await Client.SendAsync(Commands.PrepareApplianceStart, new { id = appliance.Id, siid = action.Siid, aiid = action.Aiid, origin = "app" }); if (r.Ok) nonce = r.Data?.GetProperty("confirmation").GetString(); return r; });
        return nonce;
    }
    public Task ConfirmAsync(ApplianceState appliance, ApplianceActionState action, string nonce) => RunAsync(() => Client.SendAsync(Commands.ConfirmApplianceStart,
        new { id = appliance.Id, siid = action.Siid, aiid = action.Aiid, origin = "app", confirmed = true, confirmation = nonce }));
    public Task ActAsync(ApplianceState appliance, ApplianceActionState action) => RunAsync(() => Client.SendAsync(Commands.ApplianceAction, new { id = appliance.Id, siid = action.Siid, aiid = action.Aiid }));
    public Task SetAsync(ApplianceState appliance, ApplianceProperty property, object value) => RunAsync(() => Client.SendAsync(Commands.ApplianceProperties,
        new { id = appliance.Id, values = new[] { new { siid = property.Siid, piid = property.Piid, value = JsonSerializer.SerializeToElement(value) } } }));
}
