using Prism.App.Services;
using Prism.Core.Ipc;

namespace Prism.App.ViewModels;

/// <summary>Independent, optimistic controls for one lamp. The tile keeps its identity across state pushes.</summary>
public sealed class LampTileViewModel : PageViewModel
{
    private bool on, connected, suppress;
    private double brightness;
    private string color = "#FFFFFF", follow = "pc", name = "Lamp";
    private string? host;
    public LampTileViewModel(IPrismClient client, string id) : base(client) { Id = id; ToggleCommand = new RelayCommand(() => On = !On); SelectColorCommand = new RelayCommand<string>(hex => { if (hex is not null) ColorEntry = hex; }); SetBrightnessCommand = new RelayCommand<string>(value => { if (double.TryParse(value, out var level)) Brightness = level; }); }
    private bool customColorOpen;
    public bool CustomColorOpen { get => customColorOpen; set => Set(ref customColorOpen, value); }
    public RelayCommand OpenCustomColorCommand => new(() => CustomColorOpen = !CustomColorOpen);
    public RelayCommand FollowMapCommand => new(() => Follow = "map");
    public IReadOnlyList<string> PrimaryFollowModes { get; } = ["own", "pc", "average"];
    public string Id { get; }
    public string Name => name;
    public bool Connected => connected;
    public string ConnectionText => connected ? "Connected · " + host : "Offline";
    public string PowerText => On ? "ON" : "OFF";
    public RelayCommand ToggleCommand { get; }
    public RelayCommand<string> SelectColorCommand { get; }
    public RelayCommand<string> SetBrightnessCommand { get; }
    public IReadOnlyList<string> Swatches { get; } = ["#FF4D1A", "#FF4D4F", "#F5A524", "#3DDC84", "#36D6FF", "#4D6BFF", "#B26BFF", "#FF4DB8"];
    public IReadOnlyList<string> FollowModes { get; } = ["pc", "average", "own", "map"];
    public bool On { get => on; set { if (Set(ref on, value) && !suppress) _ = PushAsync(); } }
    public double Brightness { get => brightness; set { if (Set(ref brightness, Math.Clamp(Math.Round(value), 1, 100)) && !suppress) _ = PushAsync(); } }
    public string ColorEntry { get => color; set { if (Prism.Lighting.LedLayers.Valid(value, false) && Set(ref color, value) && !suppress) _ = PushAsync(); } }
    public string Follow { get => follow; set { if (value is "pc" or "average" or "own" or "map" && Set(ref follow, value) && !suppress) _ = PushAsync(); } }
    protected override void Refresh()
    {
        var lamp = (State?.Lamps ?? (State?.Lamp is { } first ? [first] : [])).FirstOrDefault(l => l.Id == Id);
        if (lamp is null) return;
        suppress = true;
        name = lamp.Name ?? lamp.Model; connected = lamp.Connected; host = lamp.Host;
        On = lamp.On; Brightness = lamp.Brightness; ColorEntry = lamp.Color; Follow = lamp.Follow;
        suppress = false; NotifyAll();
    }
    private Task PushAsync() => RunLatestAsync("lamp:" + Id, Commands.SetLamp,
        new { lamp = Id, on = On, brightness = (int)Brightness, color = ColorEntry, follow = Follow });
}
