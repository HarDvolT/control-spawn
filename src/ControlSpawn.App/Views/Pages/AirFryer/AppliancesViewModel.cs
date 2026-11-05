using System.Text.Json;
using ControlSpawn.App.Services;
using ControlSpawn.Core.Ipc;

namespace ControlSpawn.App.ViewModels;

public sealed record FryerPreset(string Name, string Icon, double Temperature, double Minutes);

public sealed class AppliancesViewModel : PageViewModel
{
    public static readonly IReadOnlyList<FryerPreset> BuiltInPresets =
        [new("Fries", "\U0001F35F", 200, 15), new("Chicken wings", "\U0001F357", 180, 22), new("Vegetables", "\U0001F966", 170, 12),
         new("Fish", "\U0001F41F", 180, 10), new("Reheat", "\U0001F355", 160, 6), new("Pastry", "\U0001F950", 160, 9)];
    private readonly AppSettings settings;
    private readonly SettingsStore? store;
    public AppliancesViewModel(IControlSpawnClient client, AppSettings? settings = null, SettingsStore? store = null) : base(client) { PageTitle = "Air fryer"; this.settings = settings ?? new(); this.store = store; }
    public IReadOnlyList<ApplianceState> Appliances => State?.Appliances ?? [];
    public IReadOnlyList<FryerPreset> Presets => [.. BuiltInPresets, .. settings.FryerPresets];
    public bool IsSaved(FryerPreset preset) => settings.FryerPresets.Contains(preset);
    public string? Ip(string applianceId) => State?.Devices?.FirstOrDefault(d => d.Id == applianceId)?.Ip;
    /// <summary>The preset these targets came from, so the page and history can name a cook.</summary>
    public FryerPreset? Match(double? temperature, double? minutes) => Presets.FirstOrDefault(p => p.Temperature == temperature && p.Minutes == minutes);

    // Saved presets live in the app's own settings: the fryer has no slots for them.
    public void SavePreset(double temperature, double minutes)
    {
        if (Match(temperature, minutes) is not null) return;
        int n = 1; while (settings.FryerPresets.Any(p => p.Name == $"My cook {n}")) n++;
        settings.FryerPresets.Add(new($"My cook {n}", "⭐", temperature, minutes));
        Persist();
    }
    public void RemovePreset(FryerPreset preset) { if (settings.FryerPresets.Remove(preset)) Persist(); }
    private void Persist()
    {
        try { store?.Save(settings); }
        catch (Exception e) { _ = RunAsync(() => Task.FromResult(new Response(0, false, "Couldn't save the preset: " + e.Message))); }
        OnPropertyChanged(nameof(Presets));
    }

    public async Task<string?> PrepareAsync(ApplianceState appliance, ApplianceActionState action)
    {
        string? nonce = null;
        await RunAsync(async () => { var r = await Client.SendAsync(Commands.PrepareApplianceStart, new { id = appliance.Id, siid = action.Siid, aiid = action.Aiid, origin = "app" }); if (r.Ok) nonce = r.Data?.GetProperty("confirmation").GetString(); return r; });
        return nonce;
    }
    public Task ConfirmAsync(ApplianceState appliance, ApplianceActionState action, string nonce) => RunAsync(() => Client.SendAsync(Commands.ConfirmApplianceStart,
        new { id = appliance.Id, siid = action.Siid, aiid = action.Aiid, origin = "app", confirmed = true, confirmation = nonce }));
    public static bool CanEdit(ApplianceState appliance) => appliance.Connected && appliance.Status is not ("cooking" or "preheating" or "paused" or "scheduled");
    /// <summary>Temperature and time can change mid-cook, like on the fryer's own panel; that never starts heating.</summary>
    public static bool CanAdjust(ApplianceState appliance) => appliance.Connected && appliance.Status is not ("scheduled" or "fault");
    private static bool Allowed(ApplianceState appliance, IEnumerable<ApplianceProperty> properties) =>
        CanEdit(appliance) || CanAdjust(appliance) && properties.All(p => p.Name is "target-temperature" or "target-time" or "turn-pot-config");
    public Task ActAsync(ApplianceState appliance, ApplianceActionState action) => action.Name is "cancel-cooking" or "pause"
        ? RunAsync(() => Client.SendAsync(Commands.ApplianceAction, new { id = appliance.Id, siid = action.Siid, aiid = action.Aiid }))
        : RunAsync(() => Task.FromResult(new Response(0, false, "Starting requires a second confirmation tap.")));
    public Task SetAsync(ApplianceState appliance, ApplianceProperty property, object value) => SetAsync(appliance, (property, value));
    /// <summary>Several settings go in one command, and quick taps on a stepper collapse into the latest value.</summary>
    public Task SetAsync(ApplianceState appliance, params (ApplianceProperty Property, object Value)[] changes) => !Allowed(Appliances.FirstOrDefault(a => a.Id == appliance.Id) ?? appliance, changes.Select(c => c.Property))
        ? RunAsync(() => Task.FromResult(new Response(0, false, "Settings are locked while cooking.")))
        : RunLatestAsync($"fryer:{appliance.Id}:{string.Join(",", changes.Select(c => c.Property.Piid))}", Commands.ApplianceProperties,
            new { id = appliance.Id, values = changes.Select(c => new { siid = c.Property.Siid, piid = c.Property.Piid, value = JsonSerializer.SerializeToElement(c.Value) }).ToArray() });
}
