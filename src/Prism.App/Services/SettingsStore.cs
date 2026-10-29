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

    public AppSettings Load()
    {
        try
        {
            if (!File.Exists(FilePath)) return new AppSettings();
            var json = File.ReadAllText(FilePath);
            if (string.IsNullOrWhiteSpace(json)) return new AppSettings();
            return JsonSerializer.Deserialize<AppSettings>(json, Options) ?? new AppSettings();
        }
        catch
        {
            return new AppSettings();
        }
    }

    public void Save(AppSettings settings)
    {
        var folder = System.IO.Path.GetDirectoryName(FilePath);
        if (!string.IsNullOrEmpty(folder)) Directory.CreateDirectory(folder);
        var temporary = FilePath + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(settings, Options));
        File.Move(temporary, FilePath, overwrite: true);
    }
}
