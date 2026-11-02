using System.Globalization;
using System.Text.Json;
using Prism.Core;
using Prism.Core.Ipc;
using Prism.Hardware;
using Prism.Lighting;

namespace Prism.Service;

public sealed record FanConfiguration(FanMode Mode = FanMode.Standard, TemperatureSource Source = TemperatureSource.Cpu,
    FanPoint[]? Points = null)
{
    public FanSettings ToSettings() => new(Mode == FanMode.Custom ? new FanCurve(Points ?? []) : FanCurveEngine.Preset(Mode), Source);
}
public sealed record LightingConfiguration(LightingEffect Effect = LightingEffect.Static, string Color = "#FF4D1A", double Brightness = 1, double Speed = 1, EffectParameters? Parameters = null)
{
    public EffectParameters GetParameters() => Parameters ?? new() { Palette = Palettes.ForEffect(Effect) };
    public Scene ToScene() => new("Current", Effect, ParseColor(Color), Speed, Brightness);
    public static RgbColor ParseColor(string color)
    {
        if (color is null || color.Length != 7 || color[0] != '#' || !int.TryParse(color.AsSpan(1), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var rgb))
            throw new ArgumentException("Color must be #RRGGBB.");
        return new((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);
    }
}
public sealed record LampConfiguration(string? Host = null, bool On = true, int Brightness = 60,
    string Color = "#FF4D1A", string Follow = "own", int? Kelvin = null, string Id = "unresolved", string Name = "Lamp", uint? DeviceId = null, string? LastHost = null, MapPoint? MapPoint = null, string Transport = "miio");
public sealed record SceneConfiguration(FanMode Fans, LightingConfiguration Lighting, LampConfiguration Lamp);
public sealed record LedConfiguration(string[]? Custom = null, string[]? Overlay = null, bool CustomEnabled = false);
public sealed record ServiceSettings
{
    public IReadOnlyList<DeviceState> Devices { get; init; } = [];
    public DiscoveryOptions Discovery { get; init; } = new();
    public Dictionary<int, FanConfiguration> HeaderFans { get; init; } = new();
    public Dictionary<string, FanConfiguration> Fans { get; init; } = new() { ["Radiator"] = new(), ["Case fans"] = new() };
    public LightingConfiguration Lighting { get; init; } = new();
    public Dictionary<string, SensorBinding> Bindings { get; init; } = new();
    public LightingMap Map { get; init; } = new([]);
    public EventEffectOptions Events { get; init; } = new();
    public Dictionary<string, bool> SyncedDevices { get; init; } = new();
    public Dictionary<string, LedConfiguration> LedLayers { get; init; } = new();
    /// <summary>Parts with their own effect, keyed like "Applies to": a device id or "device/zone/N".</summary>
    public Dictionary<string, PartLook> PartLooks { get; init; } = new();
    public Dictionary<string, Dictionary<int, int>> ZoneSizes { get; init; } = new();
    public LampConfiguration Lamp { get; init; } = new();
    public IReadOnlyList<LampConfiguration>? Lamps { get; init; }
    [System.Text.Json.Serialization.JsonIgnore]
    public IReadOnlyList<LampConfiguration> ConfiguredLamps => Lamps ?? [Lamp];
    public string? OpenRgbPath { get; init; }
    public bool LaunchOpenRgb { get; init; }
    public bool RouterEnabled { get; init; }
    public string RouterHost { get; init; } = "192.168.0.1";
    public AutomationSettings Automations { get; init; } = new();
    public Dictionary<string, SceneConfiguration> Scenes { get; init; } = new()
    {
        ["Gaming"] = new(FanMode.Turbo, new(LightingEffect.Smart), new(Brightness: 60)),
        ["Work"] = new(FanMode.Standard, new(Color: "#E4E4E7"), new(Brightness: 90, Color: "#FFE4BD", Kelvin: 3500)),
        ["Night"] = new(FanMode.Silent, new(LightingEffect.Breathing, "#6366F1", 0.15, 0.5), new(On: false)),
        ["Movie"] = new(FanMode.Silent, new(LightingEffect.Adaptive, "#F5A524", 0.4), new(Follow: "average")),
        ["Focus"] = new(FanMode.Standard, new(Color: "#36D6FF", Brightness: 0.7), new(Brightness: 80, Color: "#DCEEFF", Kelvin: 5500)),
        ["Away"] = new(FanMode.Silent, new(LightingEffect.Off), new(On: false))
    };

    public void Validate()
    {
        if (Devices is null || Discovery is null || HeaderFans is null || Devices.Select(d => d.Id).Distinct().Count() != Devices.Count) throw new ArgumentException("Invalid device registry.");
        foreach (var d in Devices) if (d.UserName is not null) _ = DeviceRegistry.Rename(d, d.UserName);
        foreach (var h in HeaderFans)
        {
            if (h.Key is < 1 or > 7 or 7 || !Enum.IsDefined(h.Value.Mode) || !Enum.IsDefined(h.Value.Source) ||
                h.Value.Points?.Any(p => p.Temperature < 0 || p.Temperature > 120 || p.Percent < 0 || p.Percent > 100) == true) throw new ArgumentException("Invalid fan header.");
            _ = h.Value.ToSettings();
        }
        if (Fans is null || Lighting is null || Lamp is null || Scenes is null || SyncedDevices is null || Automations is null) throw new ArgumentException("Settings cannot be null.");
        if (!AutomationEngine.TryTime(Automations.NightStart, out _) || !AutomationEngine.TryTime(Automations.NightEnd, out _)) throw new ArgumentException("Night hours must look like 23:00.");
        if (Fans.Count != 2 || !Fans.ContainsKey("Radiator") || !Fans.ContainsKey("Case fans")) throw new ArgumentException("Only Radiator and Case fans are configurable.");
        foreach (var fan in Fans.Values)
        {
            if (fan is null || !Enum.IsDefined(fan.Mode) || !Enum.IsDefined(fan.Source)) throw new ArgumentException("Invalid fan mode/source.");
            _ = fan.ToSettings();
            if (fan.Points?.Any(p => p.Temperature < 0 || p.Temperature > 120 || p.Percent < 0 || p.Percent > 100) == true)
                throw new ArgumentException("Fan points must be 0..120 C and 0..100%.");
        }
        if (LedLayers is null || ZoneSizes is null) throw new ArgumentException("LED settings cannot be null.");
        foreach (var pair in LedLayers)
        {
            if (string.IsNullOrWhiteSpace(pair.Key) || pair.Value is null ||
                pair.Value.Custom?.Any(c => !Prism.Lighting.LedLayers.Valid(c, false)) == true ||
                pair.Value.Overlay?.Any(c => !Prism.Lighting.LedLayers.Valid(c, true)) == true)
                throw new ArgumentException("Invalid LED layer.");
        }
        if (ZoneSizes.Any(d => string.IsNullOrWhiteSpace(d.Key) || d.Value is null || d.Value.Any(z => z.Key < 0 || z.Value < 0 || z.Value > ushort.MaxValue)))
            throw new ArgumentException("Invalid zone size.");
        ValidateOptions(OpenRgbPath, LaunchOpenRgb, RouterHost);
        if (Bindings is null) throw new ArgumentException("Missing sensor bindings.");
        foreach (var binding in Bindings) { if (string.IsNullOrWhiteSpace(binding.Key) || binding.Value is null) throw new ArgumentException("Invalid sensor target."); SensorSources.Validate(binding.Value); }
        LightingValidation.Map(Map);
        if (Events is null) throw new ArgumentException("Missing event options.");
        ValidateLighting(Lighting); ValidateLamp(Lamp);
        if (PartLooks is null || PartLooks.Any(p => string.IsNullOrWhiteSpace(p.Key) || p.Key.Length > 256 || p.Value is null)) throw new ArgumentException("Invalid part look.");
        foreach (var look in PartLooks.Values) ValidateLighting(new(look.Effect, look.Color, look.Brightness, look.Speed));
        foreach (var item in ConfiguredLamps) ValidateLamp(item);
        if (ConfiguredLamps.Select(l => l.Id).Distinct().Count() != ConfiguredLamps.Count) throw new ArgumentException("Duplicate lamp id.");
        foreach (var scene in Scenes.Values)
        {
            if (scene is null || !Enum.IsDefined(scene.Fans) || scene.Fans == FanMode.Custom) throw new ArgumentException("Invalid scene fan preset.");
            ValidateLighting(scene.Lighting); ValidateLamp(scene.Lamp);
        }
        if (SyncedDevices.Any(d => string.IsNullOrWhiteSpace(d.Key) || d.Key.Length > 256)) throw new ArgumentException("Invalid device id.");
    }
    public static void ValidateHost(string host)
    {
        if (string.IsNullOrWhiteSpace(host) || host.Length > 253 || Uri.CheckHostName(host) == UriHostNameType.Unknown)
            throw new ArgumentException("Invalid host.");
    }
    public static void ValidateOptions(string? path, bool launch, string host)
    {
        ValidateHost(host);
        if (path is { Length: > 1024 } || (launch && (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path) || !path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))))
            throw new ArgumentException("Provide an absolute OpenRGB executable path.");
    }
    public static void ValidateLighting(LightingConfiguration value)
    {
        if (value is null || !Enum.IsDefined(value.Effect) || !double.IsFinite(value.Brightness) || value.Brightness is < 0 or > 1 ||
            !double.IsFinite(value.Speed) || value.Speed is < 0 or > 2) throw new ArgumentException("Invalid lighting values.");
        LightingValidation.Parameters(value.Parameters ?? new());
        _ = LightingConfiguration.ParseColor(value.Color);
    }
    public static void ValidateLamp(LampConfiguration value)
    {
        if (value is null || value.Brightness is < 1 or > 100 || value.Follow is not ("pc" or "average" or "own" or "map") ||
            value.Kelvin is < 1700 or > 6500) throw new ArgumentException("Invalid lamp values.");
        _ = LightingConfiguration.ParseColor(value.Color);
        if (string.IsNullOrWhiteSpace(value.Id) || value.Id.Length > 256 || value.DeviceId is { } id && value.Id != $"lamp:{id}" || string.IsNullOrWhiteSpace(value.Name) || value.Name.Length > 100 ||
            value.DeviceId is 0 or uint.MaxValue || value.MapPoint is { } p && (!double.IsFinite(p.X) || !double.IsFinite(p.Y) || p.X < 0 || p.X > 1 || p.Y < 0 || p.Y > 1)) throw new ArgumentException("Invalid lamp identity/map point.");
        if (value.Host is not null) ValidateHost(value.Host);
        if (value.LastHost is not null) ValidateHost(value.LastHost);
    }
}

public interface ISettingsStore
{
    ServiceSettings Load();
    void Save(ServiceSettings settings);
}
public sealed class JsonSettingsStore(string? path = null) : ISettingsStore
{
    private readonly string path = path ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Prism", "settings.json");
    public ServiceSettings Load()
    {
        if (!File.Exists(path)) return new();
        var settings = JsonSerializer.Deserialize<ServiceSettings>(File.ReadAllText(path), Prism.Core.Ipc.PipeContract.Json)
            ?? throw new InvalidDataException("Empty settings.");
        // Scenes added in later versions appear for existing users without touching ones they already have.
        var defaults = new ServiceSettings().Scenes;
        if (settings.Scenes is not null && defaults.Keys.Any(k => !settings.Scenes.ContainsKey(k)))
            settings = settings with { Scenes = settings.Scenes.Concat(defaults.Where(d => !settings.Scenes.ContainsKey(d.Key))).ToDictionary(s => s.Key, s => s.Value) };
        settings.Validate(); return settings;
    }
    public void Save(ServiceSettings settings)
    {
        settings.Validate();
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(settings, Prism.Core.Ipc.PipeContract.Json));
            File.Move(temporary, path, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
public sealed class MemorySettingsStore : ISettingsStore
{
    private ServiceSettings settings = new();
    public ServiceSettings Load() => settings;
    public void Save(ServiceSettings value) => settings = value;
}
