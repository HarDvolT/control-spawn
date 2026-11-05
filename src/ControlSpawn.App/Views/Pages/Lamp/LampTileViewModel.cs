using ControlSpawn.App.Services;
using ControlSpawn.Core.Ipc;

namespace ControlSpawn.App.ViewModels;

/// <summary>Independent, optimistic controls for one lamp. The tile keeps its identity across state pushes.</summary>
public sealed class LampTileViewModel : PageViewModel
{
    private bool on, connected, suppress, handedOff;
    private int? kelvin;
    private double brightness;
    private string color = "#FFFFFF", follow = "pc", name = "Lamp";
    private string? host;
    public LampTileViewModel(IControlSpawnClient client, string id) : base(client) { Id = id; ToggleCommand = new RelayCommand(() => On = !On); SelectColorCommand = new RelayCommand<string>(hex => { if (hex is not null) ColorEntry = hex; }); SetBrightnessCommand = new RelayCommand<string>(value => { if (double.TryParse(value, out var level)) Brightness = level; });
        SetWhiteCommand = new RelayCommand<string>(value => { if (int.TryParse(value, out var k)) WhiteKelvin = k; });
        TakeOverCommand = new RelayCommand(() => _ = RunAsync(() => Client.SendAsync(Commands.SetLamp, new { lamp = Id }))); }
    private bool customColorOpen;
    public bool CustomColorOpen { get => customColorOpen; set => Set(ref customColorOpen, value); }
    public RelayCommand OpenCustomColorCommand => new(() => CustomColorOpen = !CustomColorOpen);
    public RelayCommand FollowMapCommand => new(() => Follow = "map");
    public IReadOnlyList<string> PrimaryFollowModes { get; } = ["own", "pc", "average"];
    public string Id { get; }
    public string Name => name;
    public bool Connected => connected;
    public string ConnectionText => connected ? (string.IsNullOrWhiteSpace(host) ? "Connected" : "Connected · " + host) : "Offline";
    public string PowerText => On ? "ON" : "OFF";
    public RelayCommand ToggleCommand { get; }
    public RelayCommand<string> SelectColorCommand { get; }
    public RelayCommand<string> SetBrightnessCommand { get; }
    public RelayCommand<string> SetWhiteCommand { get; }
    public RelayCommand TakeOverCommand { get; }
    /// <summary>The bulb was changed in Mi Home (or at the wall), so the service leaves it alone until it's changed here.</summary>
    public bool HandedOff => handedOff;
    /// <summary>The white the bulb's own white LEDs give, or null while it shows a colour.</summary>
    public int? Kelvin => kelvin;
    public string WhiteText => kelvin is { } k ? $"{k:0} K" : "Off";
    /// <summary>Picking a white makes it the lamp's own light, as picking a colour does.</summary>
    public double WhiteKelvin
    {
        get => kelvin ?? 4000;
        set
        {
            var k = (int)Math.Clamp(Math.Round(value / 100) * 100, 1700, 6500);
            if (suppress || (kelvin == k && follow == "own")) return;
            kelvin = k; color = ControlSpawn.Lighting.LampWhite.Hex(k); follow = "own";
            NotifyAll(); _ = PushAsync();
        }
    }
    public IReadOnlyList<string> Swatches { get; } = ["#FF4D1A", "#FF4D4F", "#F5A524", "#3DDC84", "#36D6FF", "#4D6BFF", "#B26BFF", "#FF4DB8"];
    public IReadOnlyList<string> FollowModes { get; } = ["pc", "average", "own", "map"];
    public bool On { get => on; set { if (Set(ref on, value) && !suppress) _ = PushAsync(); } }
    public double Brightness { get => brightness; set { if (Set(ref brightness, Math.Clamp(Math.Round(value), 1, 100)) && !suppress) _ = PushAsync(); } }
    // A colour picked here is the lamp's own; it used to be ignored while the lamp followed the PC.
    public string ColorEntry { get => color; set { if (ControlSpawn.Lighting.LedLayers.Valid(value, false) && Set(ref color, value) && !suppress) { kelvin = null; follow = "own"; NotifyAll(); _ = PushAsync(); } } }
    public string Follow { get => follow; set { if (value is "pc" or "average" or "own" or "map" && Set(ref follow, value) && !suppress) _ = PushAsync(); } }
    protected override void Refresh()
    {
        var lamp = (State?.Lamps ?? (State?.Lamp is { } first ? [first] : [])).FirstOrDefault(l => l.Id == Id);
        if (lamp is null) return;
        suppress = true;
        name = lamp.Name ?? lamp.Model; connected = lamp.Connected; host = lamp.Host;
        On = lamp.On; Brightness = lamp.Brightness; ColorEntry = lamp.Color; Follow = lamp.Follow; kelvin = lamp.Kelvin; handedOff = lamp.HandedOff;
        suppress = false; NotifyAll();
    }
    private Task PushAsync() => RunLatestAsync("lamp:" + Id, Commands.SetLamp,
        new { lamp = Id, on = On, brightness = (int)Brightness, color = ColorEntry, follow = Follow, kelvin });
}
