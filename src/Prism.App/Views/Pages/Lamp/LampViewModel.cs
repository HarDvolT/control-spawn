using Prism.App.Common;
using System.Text.Json;
using Prism.App.Services;
using Prism.Core.Ipc;

namespace Prism.App.ViewModels;

/// <summary>The Yeelight page: power, brightness, colour and follow mode.</summary>
public sealed class LampViewModel : PageViewModel
{
    private bool _connected;
    private bool _on;
    private double _brightness = 70;
    private string _colorHex = "#FF4D1A";
    private string _follow = "pc";
    private string _model = Format.Dash;
    private string _transport = Format.Dash;
    private bool _suppress;
    private LampState? selectedLamp;
    private string? editLampId;
    public System.Collections.ObjectModel.ObservableCollection<LampTileViewModel> Tiles { get; } = new();
    public RelayCommand<string> SetBothCommand { get; }
    public double BothBrightness
    {
        get => Tiles.Count > 0 ? Tiles.Average(l => l.Brightness) : 1;
        set { foreach (var tile in Tiles) tile.Brightness = value; OnPropertyChanged(nameof(BothBrightness)); }
    }
    public string Headline => Tiles.Count == 2 ? "Two lamps, right here" : $"{Tiles.Count} lamps, right here";
    public IReadOnlyList<LampState> Lamps { get; private set; } = [];
    public LampState? SelectedLamp { get => selectedLamp; set { if (Set(ref selectedLamp, value)) Refresh(); } }
    public IReadOnlyList<DiscoveredLamp> Discovered { get; private set; } = [];
    public RelayCommand FindCommand { get; }
    public RelayCommand RemoveCommand { get; }
    public RelayCommand RenameCommand { get; }
    public string LampName { get; set; } = "Lamp";
    public string DeviceIdText { get; set; } = "";
    public string Host => selectedLamp?.Host ?? "Unresolved";
    public string? LampError => selectedLamp?.Error;
    public IReadOnlyList<string> FollowModes { get; } = ["pc", "average", "own", "map"];
    public string SelectedFollow { get => Follow; set { if (!_suppress && value != Follow) _ = SetFollowAsync(value); } }
    private DiscoveredLamp? selectedFound;
    public DiscoveredLamp? SelectedFound { get => selectedFound; set { if (Set(ref selectedFound, value) && value is not null) { DeviceIdText = value.DeviceId.ToString(); OnPropertyChanged(nameof(DeviceIdText)); } } }
    public sealed record DiscoveredLamp(string Ip, uint DeviceId, bool AlreadyConfigured)
    { public string Label => $"{Ip} | {DeviceId} | {(AlreadyConfigured ? "configured" : "new")}"; }


    public LampViewModel(IPrismClient client) : base(client)
    {
        PageTitle = "Lamps";
        SetBothCommand = new RelayCommand<string>(value => { foreach (var tile in Tiles) tile.On = value == "on"; });
        FindCommand = new RelayCommand(() => _ = FindAsync());
        RemoveCommand = new RelayCommand(() => _ = RunAsync(() => Client.SendAsync(Commands.RemoveLamp, new { lamp = selectedLamp?.Id })));
        RenameCommand = new RelayCommand(() => _ = RunAsync(() => Client.SendAsync(Commands.SetLamp, new { lamp = selectedLamp?.Id, name = LampName })));

        ToggleCommand = new RelayCommand(() => On = !On);
        SetFollowCommand = new RelayCommand<string>(follow => _ = SetFollowAsync(follow ?? "pc"));
        SelectColorCommand = new RelayCommand<string>(hex => _ = SetColorAsync(hex ?? ColorHex));
        Refresh();
    }

    public IReadOnlyList<string> Swatches { get; } = ["#FF4D1A", "#F4F4F5", "#3DDC84", "#5AA7FF", "#B26BFF", "#F5A524"];

    public RelayCommand ToggleCommand { get; }
    public RelayCommand<string> SetFollowCommand { get; }
    public RelayCommand<string> SelectColorCommand { get; }

    public bool Connected { get => _connected; private set => Set(ref _connected, value); }
    public string Model { get => _model; private set => Set(ref _model, value); }
    public string Transport { get => _transport; private set => Set(ref _transport, value); }

    public bool On
    {
        get => _on;
        set
        {
            if (!Set(ref _on, value) || _suppress) return;
            OnPropertyChanged(nameof(StateText));
            OnPropertyChanged(nameof(DotColorHex));
            _ = PushAsync();
        }
    }

    public double Brightness
    {
        get => _brightness;
        set
        {
            var clamped = Math.Clamp(Math.Round(value), 1, 100);
            if (!Set(ref _brightness, clamped) || _suppress) return;
            OnPropertyChanged(nameof(BrightnessText));
            _ = PushAsync();
        }
    }

    public string TokenNote => "Saved tokens are encrypted by the service under its Windows identity.";

    public string BrightnessText => Format.Percent(Brightness);
    public string ColorHex { get => _colorHex; private set => Set(ref _colorHex, value); }
    public string Follow { get => _follow; private set => Set(ref _follow, value); }

    public bool FollowPc => _follow == "pc";
    public bool FollowAverage => _follow == "average";
    public bool FollowOwn => _follow == "own";

    public string StateText => !Connected ? "Not connected" : On ? "On · " + BrightnessText : "Off";
    public string DotColorHex => On && Connected ? ColorHex : "#3F3F46";
    public string ColorEntry { get => ColorHex; set { if (Prism.Lighting.LedLayers.Valid(value, false) && value != ColorHex) _ = SetColorAsync(value); } }
    public string FollowText => _follow switch
    {
        "average" => "Average of PC colours",
        "map" => "Lighting map point",
        "own" => "Own colour",
        _ => "Same as PC"
    };

    public string Subtitle => Connected
        ? $"{Model} · {Transport}"
        : "Find the Mi Home lamp and add its token";

    protected override void Refresh()
    {
        Lamps = State?.Lamps ?? (State?.Lamp is { } first ? [first] : []);
        foreach (var lampState in Lamps)
        {
            var id = lampState.Id ?? "";
            var tile = Tiles.FirstOrDefault(t => t.Id == id);
            if (tile is null) { tile = new LampTileViewModel(Client, id); Tiles.Add(tile); }
            if (State is { } snapshot) tile.Apply(snapshot);
        }
        foreach (var removed in Tiles.Where(t => !Lamps.Any(l => (l.Id ?? "") == t.Id)).ToArray()) Tiles.Remove(removed);
        selectedLamp = Lamps.FirstOrDefault(l => l.Id == selectedLamp?.Id) ?? Lamps.FirstOrDefault();
        var lamp = selectedLamp;
        if (editLampId != lamp?.Id)
        {
            LampName = lamp?.Name ?? "Lamp";
            DeviceIdText = lamp?.DeviceId?.ToString() ?? "";
            editLampId = lamp?.Id;
        }
        _suppress = true;
        Connected = lamp?.Connected ?? false;
        On = lamp?.On ?? false;
        Brightness = lamp?.Brightness ?? 70;
        ColorHex = lamp?.Color ?? ColorHex;
        Follow = lamp?.Follow ?? "pc";
        Model = lamp?.Name ?? "Smart lamp";
        Transport = Controls.FriendlyLabelConverter.Label(lamp?.Transport);
        _suppress = false;

        OnPropertiesChanged(nameof(StateText), nameof(DotColorHex), nameof(BrightnessText),
            nameof(FollowPc), nameof(FollowAverage), nameof(FollowOwn), nameof(FollowText), nameof(Subtitle));
        NotifyAll();
    }

    private async Task SetFollowAsync(string follow)
    {
        Follow = follow;
        OnPropertiesChanged(nameof(FollowPc), nameof(FollowAverage), nameof(FollowOwn), nameof(FollowText));
        await PushAsync();
    }

    private async Task SetColorAsync(string hex)
    {
        ColorHex = hex;
        OnPropertyChanged(nameof(DotColorHex));
        await PushAsync();
    }

    private async Task FindAsync()
    {
        await RunAsync(async () => {
            var response = await Client.SendAsync(Commands.DiscoverLamps);
            if (response.Ok && response.Data is { } data) { Discovered = data.Deserialize<DiscoveredLamp[]>(PipeContract.Json) ?? []; OnPropertyChanged(nameof(Discovered)); }
            return response;
        });
    }
    private Task PushAsync() => RunLatestAsync("lamp:" + selectedLamp?.Id, Commands.SetLamp, new { lamp = selectedLamp?.Id, on = On, brightness = (int)Math.Round(Brightness), follow = Follow, color = ColorHex });
}
