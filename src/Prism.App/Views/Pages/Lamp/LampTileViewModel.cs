using Prism.App.Services;
using Prism.Core.Ipc;

namespace Prism.App.ViewModels;

/// <summary>Independent, optimistic controls for one lamp. The tile keeps its identity across state pushes.</summary>
public sealed class LampTileViewModel : PageViewModel
{
    private bool on, connected, suppress;
    private double brightness;
    private string color = "#FFFFFF", follow = "pc", name = "Lamp";
    public LampTileViewModel(IPrismClient client, string id) : base(client) { Id = id; ToggleCommand = new RelayCommand(() => On = !On); }
    public string Id { get; }
    public string Name => name;
    public bool Connected => connected;
    public string ConnectionText => connected ? "Connected" : "Offline";
    public RelayCommand ToggleCommand { get; }
    public IReadOnlyList<string> FollowModes { get; } = ["pc", "average", "own", "map"];
    public bool On { get => on; set { if (Set(ref on, value) && !suppress) _ = PushAsync(); } }
    public double Brightness { get => brightness; set { if (Set(ref brightness, Math.Clamp(Math.Round(value), 1, 100)) && !suppress) _ = PushAsync(); } }
    public string ColorEntry { get => color; set { if (Prism.Lighting.LedLayers.Valid(value, false) && Set(ref color, value) && !suppress) _ = PushAsync(); } }
    public string Follow { get => follow; set { if (Set(ref follow, value) && !suppress) _ = PushAsync(); } }
    protected override void Refresh()
    {
        var lamp = (State?.Lamps ?? (State?.Lamp is { } first ? [first] : [])).FirstOrDefault(l => l.Id == Id);
        if (lamp is null) return;
        suppress = true;
        name = lamp.Name ?? lamp.Model; connected = lamp.Connected;
        On = lamp.On; Brightness = lamp.Brightness; ColorEntry = lamp.Color; Follow = lamp.Follow;
        suppress = false; NotifyAll();
    }
    private Task PushAsync() => RunLatestAsync("lamp:" + Id, Commands.SetLamp,
        new { lamp = Id, on = On, brightness = (int)Brightness, color = ColorEntry, follow = Follow });
}
