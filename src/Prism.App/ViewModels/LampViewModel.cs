using Prism.App.Common;
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

    public LampViewModel(IPrismClient client) : base(client)
    {
        PageTitle = "Lamp";
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

    private string _miHomeToken = string.Empty;
    public string MiHomeToken { get => _miHomeToken; set => Set(ref _miHomeToken, value); }
    public string TokenNote => "Token stays in memory. This service protocol supports Yeelight LAN control; token provisioning is not available.";

    public string BrightnessText => Format.Percent(Brightness);
    public string ColorHex { get => _colorHex; private set => Set(ref _colorHex, value); }
    public string Follow { get => _follow; private set => Set(ref _follow, value); }

    public bool FollowPc => _follow == "pc";
    public bool FollowAverage => _follow == "average";
    public bool FollowOwn => _follow == "own";

    public string StateText => !Connected ? "Not connected" : On ? "On · " + BrightnessText : "Off";
    public string DotColorHex => On && Connected ? ColorHex : "#3F3F46";
    public string FollowText => _follow switch
    {
        "average" => "Average of PC colours",
        "own" => "Own colour",
        _ => "Same as PC"
    };

    public string Subtitle => Connected
        ? $"{Model} · {Transport}"
        : "Plug the lamp in and enable LAN control";

    protected override void Refresh()
    {
        var lamp = State?.Lamp;
        _suppress = true;
        Connected = lamp?.Connected ?? false;
        On = lamp?.On ?? false;
        Brightness = lamp?.Brightness ?? 70;
        ColorHex = lamp?.Color ?? ColorHex;
        Follow = lamp?.Follow ?? "pc";
        Model = lamp?.Model ?? Format.Dash;
        Transport = lamp?.Transport ?? Format.Dash;
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

    private Task PushAsync() => RunAsync(() => Client.SetLampAsync(On, (int)Math.Round(Brightness), Follow, ColorHex));
}
