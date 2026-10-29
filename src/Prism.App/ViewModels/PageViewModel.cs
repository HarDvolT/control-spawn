using Prism.App.Navigation;
using Prism.App.Common;
using Prism.App.Services;
using Prism.Core;
using Prism.Core.Ipc;

namespace Prism.App.ViewModels;

/// <summary>
/// Base for every page. All pages receive the same state snapshot so switching pages is instant;
/// formatting lives in the view-model so it can be unit-tested without a window.
/// </summary>
public abstract class PageViewModel : ObservableObject
{
    protected PageViewModel(IPrismClient client)
    {
        Client = client;
        OpenPageCommand = new RelayCommand<string>(key =>
        {
            if (Enum.TryParse<PageKey>(key, ignoreCase: true, out var page)) RequestNavigate(page);
        });
    }

    public IPrismClient Client { get; }
    public StateMessage? State { get; private set; }
    public string? LastError { get; private set; }
    public bool HasError => !string.IsNullOrWhiteSpace(LastError);
    public string PageTitle { get; protected init; } = string.Empty;

    /// <summary>Cross-page links ("Curves", "5G modem", …) are routed by the shell.</summary>
    public event EventHandler<PageKey>? NavigateRequested;

    public RelayCommand<string> OpenPageCommand { get; }

    protected void RequestNavigate(PageKey page) => NavigateRequested?.Invoke(this, page);

    private int optimisticChanges;
    private readonly Dictionary<string, long> editVersions = new();
    private readonly Dictionary<string, string> editErrors = new();
    protected async Task RunLatestAsync(string key, string command, object args)
    {
        optimisticChanges++;
        long version = editVersions.GetValueOrDefault(key) + 1; editVersions[key] = version;
        string? error;
        try
        {
            var response = await Client.SendLatestAsync(key, command, args);
            error = response.Ok ? null : response.Error ?? "The service refused the command";
        }
        catch (Exception e) { error = e.Message; }
        if (editVersions[key] == version)
        {
            if (error is null) editErrors.Remove(key); else editErrors[key] = error;
            LastError = editErrors.Values.FirstOrDefault();
            OnPropertyChanged(nameof(LastError)); OnPropertyChanged(nameof(HasError));
        }
        if (--optimisticChanges == 0 && editErrors.Count > 0) Refresh();
    }

    public virtual void Apply(StateMessage state)
    {
        State = state;
        if (optimisticChanges == 0) Refresh();
    }

    protected virtual void Refresh() => OnPropertyChanged(string.Empty);

    protected async Task RunAsync(Func<Task<Response>> action)
    {
        try
        {
            var response = await action().ConfigureAwait(true);
            LastError = response.Ok ? null : response.Error ?? "The service refused the command";
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
        }
        OnPropertyChanged(nameof(LastError));
        OnPropertyChanged(nameof(HasError));
    }

    protected void NotifyAll() => OnPropertyChanged(string.Empty);
}

public sealed class GaugeItem : ObservableObject
{
    public GaugeItem(string label, string value, string sub, string colorHex, double percent)
    { Label = label; Value = value; Sub = sub; ColorHex = colorHex; Percent = percent; }
    public string Label { get; private set; }
    public string Value { get; private set; }
    public string Sub { get; private set; }
    public string ColorHex { get; private set; }
    public double Percent { get; private set; }
    public System.Collections.ObjectModel.ObservableCollection<double> History { get; } = new();
    public string LevelText => !double.IsFinite(Percent) ? "Unavailable" : ColorHex switch
    { "#36D6FF" => "Cool", "#3DDC84" => "Normal", "#F5A524" => "Warm", "#FF4D4F" => "Hot", _ => "Live" };
    public string Suffix => Label.Contains("temp") ? "\u00b0" : "%";
    public string ValueFormat => Label.Contains("temp") ? "F1" : "F0";
    public double NumericValue => double.TryParse(Value.TrimEnd('%', '\u00b0'), System.Globalization.NumberStyles.Float,
        System.Globalization.CultureInfo.InvariantCulture,out var value) ? value : double.NaN;
    public void Update(GaugeItem item)
    {
        Label = item.Label; Value = item.Value; Sub = item.Sub; ColorHex = item.ColorHex; Percent = item.Percent;
        if (double.IsFinite(Percent)) { History.Add(Percent); while (History.Count > 24) History.RemoveAt(0); }
        OnPropertyChanged(string.Empty);
    }
}

public sealed record FanRow(string Name, string Sub, string RpmText, double Percent, bool IsPump);

public sealed record StatRow(string Key, string Value, string ColorHex);

public sealed record TempRow(string Name, string Value, double Percent, string ColorHex);

public sealed record BarItem(double Height, string ColorHex);

public sealed record ThreadItem(double Percent, string ColorHex);

public sealed record SimpleNetworkRow(string Net, IReadOnlyList<BarItem> Bars, string Text);

public sealed record MetricRow(string Net, string Value, string Rating, string ColorHex, double Position)
{
    public string Description => Net switch { "RSRP" => "Signal strength", "SINR" => "Signal vs noise", "RSRQ" => "Signal quality", "RSSI" => "Total received power", _ => Rating };
    public string Number => Value.Contains(' ') ? Value[..Value.LastIndexOf(' ')] : Value;
    public string Unit => Value.Contains(' ') ? Value[(Value.LastIndexOf(' ') + 1)..] : "";
}

public sealed record MetricGroup(string Title, string Code, IReadOnlyList<MetricRow> Rows);

public sealed record LaneData(string Title, string Now, string ColorHex, IReadOnlyList<double> Values, double Maximum);

public sealed record NeighborRow(string Band, string Pci, string Rsrp, string Sinr);

public sealed record SpeedStat(string Key, string Unit, string Value);

public sealed record PingBar(double Height, string ColorHex);

public sealed record SpikeItem(string Ms, string Cause, string Tip, string TagBackground, string TagForeground);

public sealed record DeviceRow(string Name, string Connection, string DotColorHex, bool IsThisPc);

public sealed record WidgetGalleryItem(Widgets.WidgetKind Kind, string Name, string Subtitle);

public sealed class SceneOption : ObservableObject
{
    private bool _isActive;

    public SceneOption(string name, string colorHex)
    {
        Name = name;
        ColorHex = colorHex;
    }

    public string Name { get; }
    public string ColorHex { get; }
    public System.Windows.Media.Brush Background
    {
        get
        {
            var colors = Name switch { "Gaming" => ("#FF4D1A", "#7A1408"), "Work" => ("#4D6BFF", "#14213F"), "Night" => ("#6A4DFF", "#160F33"), _ => ("#F5A524", "#3A2405") };
            var bright = (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(colors.Item1);
            var dark = (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(colors.Item2);
            var group = new System.Windows.Media.DrawingGroup();
            var rect = new System.Windows.Media.RectangleGeometry(new System.Windows.Rect(0, 0, 1, 1));
            var baseGradient = new System.Windows.Media.LinearGradientBrush { StartPoint = new(0, 0), EndPoint = new(1, 1) };
            baseGradient.GradientStops.Add(new(dark, 0)); baseGradient.GradientStops.Add(new(System.Windows.Media.Color.FromRgb(15, 16, 20), .8));
            group.Children.Add(new System.Windows.Media.GeometryDrawing(baseGradient, null, rect));
            var glow = new System.Windows.Media.RadialGradientBrush { Center = new(.85, .15), GradientOrigin = new(.85, .15), RadiusX = .55, RadiusY = 1.2 };
            glow.GradientStops.Add(new(System.Windows.Media.Color.FromArgb(204, bright.R, bright.G, bright.B), 0)); glow.GradientStops.Add(new(System.Windows.Media.Color.FromArgb(0, bright.R, bright.G, bright.B), 1));
            group.Children.Add(new System.Windows.Media.GeometryDrawing(glow, null, rect));
            return new System.Windows.Media.DrawingBrush(group);

        }
    }
    public string StateText => IsActive ? "Active" : "Apply";
    public string Description => Name switch { "Gaming" => "Smart lighting · Turbo fans", "Work" => "Static white · Balanced", "Night" => "Dim breathing · Silent", "Movie" => "Screen ambient · Silent", _ => Name };

    public bool IsActive
    {
        get => _isActive;
        set { if (Set(ref _isActive, value)) OnPropertyChanged(nameof(StateText)); }
    }
}

public sealed class EffectOption : ObservableObject
{
    private bool _isSelected;

    public EffectOption(LightingEffect effect, string name, string subtitle, string colorHex)
    {
        Effect = effect;
        Name = name;
        Subtitle = subtitle;
        ColorHex = colorHex;
    }

    public LightingEffect Effect { get; }
    public string Name { get; }
    public string Subtitle { get; }
    public string ColorHex { get; }

    public bool IsSelected
    {
        get => _isSelected;
        set => Set(ref _isSelected, value);
    }
}

public sealed class DeviceOption : ObservableObject
{
    private bool _synced;

    public DeviceOption(string id, string name, string sub, bool synced)
    {
        Id = id;
        Name = name;
        Sub = sub;
        _synced = synced;
    }

    public string Id { get; }
    public string Name { get; }
    public string Sub { get; }

    public bool Synced
    {
        get => _synced;
        set => Set(ref _synced, value);
    }
}

public sealed class FanGroupItem : ObservableObject
{
    private bool _isSelected;

    public FanGroupItem(string name, string sub, string rpmText, double percent, bool isPump, double spinSeconds, int header = 0)
    {
        Name = name;
        Sub = sub;
        RpmText = rpmText;
        Percent = percent;
        IsPump = isPump;
        SpinSeconds = spinSeconds;
        Id = $"fan:{header}";
    }

    public string Name { get; private set; }
    public string Sub { get; private set; }
    public string RpmText { get; private set; }
    public double Percent { get; private set; }
    public bool IsPump { get; private set; }
    public double SpinSeconds { get; private set; }
    public string Id { get; private set; }

    public IReadOnlyList<double> History { get; private set; } = [];
    public string MinimumText => History.Count > 0 ? History.Min().ToString("N0") : Format.Dash;
    public string MaximumText => History.Count > 0 ? History.Max().ToString("N0") : Format.Dash;
    public void SetHistory(IEnumerable<double> values) { History = values.ToArray(); OnPropertyChanged(string.Empty); }

    public void Update(FanGroupItem item)
    { Name = item.Name; Sub = item.Sub; RpmText = item.RpmText; Percent = item.Percent; IsPump = item.IsPump; SpinSeconds = item.SpinSeconds; OnPropertyChanged(string.Empty); }

    public bool IsSelected
    {
        get => _isSelected;
        set => Set(ref _isSelected, value);
    }
}

public sealed class ChoiceOption : ObservableObject
{
    private bool _isActive;

    public ChoiceOption(string key, string name)
    {
        Key = key;
        Name = name;
    }

    public string Key { get; }
    public string Name { get; }

    public bool IsActive
    {
        get => _isActive;
        set => Set(ref _isActive, value);
    }
}

public sealed class ToggleOption : ObservableObject
{
    private bool _isOn;

    public ToggleOption(string key, string name, string subtitle, bool isOn)
    {
        Key = key;
        Name = name;
        Subtitle = subtitle;
        _isOn = isOn;
    }

    public string Key { get; }
    public string Name { get; }
    public string Subtitle { get; }

    public bool IsOn
    {
        get => _isOn;
        set => Set(ref _isOn, value);
    }
}

public sealed class SceneCardItem : ObservableObject
{
    private bool _isActive;

    public SceneCardItem(Models.SceneDefinition definition, bool isActive)
    {
        Definition = definition;
        _isActive = isActive;
    }

    public Models.SceneDefinition Definition { get; }
    public string Name => Definition.Name;
    public string ColorHex => Definition.Color;
    public System.Windows.Media.Brush Background => new SceneOption(Name, ColorHex).Background;
    public string Trigger => Definition.Trigger;
    public string LightingText => Definition.LightingText;
    public string FansText => Definition.FansText;
    public string LampText => Definition.LampText;
    public double Noise => Definition.Noise;
    public string NoiseText => Definition.Noise < 30 ? "Quiet" : Definition.Noise < 55 ? "Normal" : "Loud";

    public bool IsActive
    {
        get => _isActive;
        set
        {
            if (Set(ref _isActive, value)) OnPropertyChanged(nameof(ButtonLabel));
        }
    }

    public string ButtonLabel => IsActive ? "Active" : "Apply";
}

public sealed record LampRow(string Id, string Name, string Color, string Status)
{
    public static IReadOnlyList<LampRow> From(StateMessage state) => (state.Lamps ?? (state.Lamp is { } first ? [first] : []))
        .Select(l => new LampRow(l.Id ?? "unresolved", l.Name ?? "Lamp", l.Connected && l.On ? l.Color : "#3F3F46",
            !l.Connected ? "Offline" : l.On ? $"{l.Brightness}% \u00b7 {Controls.FriendlyLabelConverter.Label(l.Transport)}" : "Off")).ToArray();
}
