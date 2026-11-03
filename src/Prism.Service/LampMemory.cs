using System.Text.Json;
using Prism.Core;
using Prism.Core.Ipc;
using Prism.Lighting;

namespace Prism.Service;

/// <summary>What one bulb was left showing; <see cref="Moving"/> means its colour was streaming, so it isn't compared.</summary>
public sealed record LeftLamp(LampReading Reading, bool Moving);

/// <summary>
/// What Control Spawn last left each bulb showing, kept across restarts so a start can tell a bulb was set from Mi Home
/// in the meantime. A streaming colour is stored without its rgb, so following the PC doesn't rewrite the file.
/// </summary>
public sealed class LampMemory(string? path)
{
    public static string DefaultPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Prism", "lamps-left.json");
    private readonly object gate = new();
    private Dictionary<string, LeftLamp>? left;

    public LeftLamp? Get(string id) { lock (gate) return Loaded().GetValueOrDefault(id); }

    public void Remember(string id, LampReading reading, bool moving)
    {
        var entry = new LeftLamp(moving ? reading with { Rgb = 0 } : reading, moving);
        lock (gate)
        {
            var all = Loaded();
            if (all.GetValueOrDefault(id) == entry) return;
            all[id] = entry;
            if (path is null) return;
            try { Directory.CreateDirectory(Path.GetDirectoryName(path)!); AtomicFile.WriteAllText(path, JsonSerializer.Serialize(all, PipeContract.Json)); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { PrismLog.Failure("lamps", e); }
        }
    }

    private Dictionary<string, LeftLamp> Loaded()
    {
        if (left is not null) return left;
        try { left = path is not null && File.Exists(path) ? JsonSerializer.Deserialize<Dictionary<string, LeftLamp>>(File.ReadAllText(path), PipeContract.Json) : null; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException) { PrismLog.Failure("lamps", e); }
        return left ??= new();
    }
}
