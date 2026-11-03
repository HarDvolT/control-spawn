using System.Text.Json;
using System.Text.Json.Serialization;

namespace Prism.App.Services;

public sealed class AppSettings
{
    public bool StartWithWindows { get; set; }
    public bool StartMinimizedToTray { get; set; }
    public bool KeepRunningWhenClosed { get; set; } = true;
    public bool ShowWidgetsAtStartup { get; set; } = true;
    public bool ReduceAnimations { get; set; }
    public bool CheckForUpdates { get; set; }
    public string TemperatureUnit { get; set; } = "\u00b0C";
    public string AccentColor { get; set; } = "#FF4D1A";
    public bool NotifyRunningHot { get; set; } = true;
    public bool NotifyCookingFinished { get; set; } = true;
    public bool NotifyDeviceOffline { get; set; } = true;
    public bool SceneAutoGame { get; set; } = true;
    public bool SceneAutoNight { get; set; } = true;
    public bool SceneAutoLock { get; set; }
    public string OpenRgbPath { get; set; } = string.Empty;
    public bool LaunchOpenRgb { get; set; }
    public string RouterHost { get; set; } = "192.168.0.1";
    public Dictionary<string, string> SceneLedPresets { get; set; } = new();
    public bool RouterEnabled { get; set; }
    public string? LastPage { get; set; }
}

public sealed class SettingsStore
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public SettingsStore(string? filePath = null)
    {
        FilePath = string.IsNullOrWhiteSpace(filePath) ? DefaultPath : filePath!;
    }

    public static string DefaultPath => System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Prism", "settings.json");

    public string FilePath { get; }

    /// <summary>The file, else its last good copy (a crash can leave the main file empty), else defaults.</summary>
    public AppSettings Load() => !File.Exists(FilePath) ? new AppSettings() : Read(FilePath) ?? Read(Prism.Core.AtomicFile.Backup(FilePath)) ?? new AppSettings();
    private static AppSettings? Read(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            var json = File.ReadAllText(path);
            return string.IsNullOrWhiteSpace(json) ? null : JsonSerializer.Deserialize<AppSettings>(json, Options);
        }
        catch
        {
            return null;
        }
    }

    public void Save(AppSettings settings)
    {
        Prism.Core.AtomicFile.WriteAllText(FilePath, JsonSerializer.Serialize(settings, Options));
    }
}
