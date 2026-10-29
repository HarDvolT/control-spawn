using System.Text.Json;
using Prism.Core.Ipc;

namespace Prism.App.Services;

public sealed record LedPreset(string Device, string Name, string Layer, string[] Colors);
public sealed class LedPresetStore(string? path = null)
{
    private readonly string path = path ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Prism", "led-presets.json");
    public List<LedPreset> Load()
    {
        var result = File.Exists(path) ? JsonSerializer.Deserialize<List<LedPreset>>(File.ReadAllText(path), PipeContract.Json) ?? [] : [];
        foreach (var preset in result) Validate(preset);
        return result;
    }
    private static void Validate(LedPreset preset)
    {
        if (preset is null || string.IsNullOrWhiteSpace(preset.Name) || preset.Name.Length > 80 ||
            string.IsNullOrWhiteSpace(preset.Device) || preset.Layer is not ("custom" or "overlay") || preset.Colors is null ||
            preset.Colors.Length > ushort.MaxValue || preset.Colors.Any(c => c is null || c.Length != (preset.Layer == "custom" ? 7 : 9) || c[0] != '#' || !c.Skip(1).All(Uri.IsHexDigit)))
            throw new InvalidDataException("Invalid LED layout.");
    }
    public void Save(LedPreset preset)
    {
        if (string.IsNullOrWhiteSpace(preset.Name) || preset.Name.Length > 80) throw new ArgumentException("Enter a preset name (up to 80 characters).");
        Validate(preset);
        var presets = Load(); presets.RemoveAll(p => p.Device == preset.Device && p.Name == preset.Name); presets.Add(preset);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        File.WriteAllText(path + ".tmp", JsonSerializer.Serialize(presets, PipeContract.Json));
        File.Move(path + ".tmp", path, true);
    }
}
