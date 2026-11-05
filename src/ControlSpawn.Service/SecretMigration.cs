using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using ControlSpawn.Core;

namespace ControlSpawn.Service;

/// <summary>Explicit console-mode migration, including credentials for currently disabled devices.</summary>
public static class SecretMigration
{
    public static bool Run(string appData, SecretStore store)
    {
        bool success = true;
        var dataFolder = Path.Combine(appData, ProductNames.DataFolder);
        void Migrate(string key, string path)
        {
            try
            {
                var plain = store.Load(key, path);
                if (plain is not null) CryptographicOperations.ZeroMemory(plain);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or CryptographicException or JsonException or FormatException or InvalidOperationException or KeyNotFoundException)
            { success = false; ControlSpawnLog.Failure("secrets", e); }
        }
        Migrate("lamp", Path.Combine(dataFolder, "lamp.json"));
        try
        {
            if (Directory.Exists(dataFolder))
                foreach (var path in Directory.GetFiles(dataFolder, "lamp.json.*"))
                    if (uint.TryParse(Path.GetFileName(path)[10..], NumberStyles.None, CultureInfo.InvariantCulture, out var id) && id is > 0 and < uint.MaxValue)
                        Migrate("lamp." + id.ToString(CultureInfo.InvariantCulture), path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        { success = false; ControlSpawnLog.Failure("secrets", e); }
        foreach (var name in new[] { "credentials.json", "session.json" }) Migrate("router." + name, Path.Combine(dataFolder, "router", name));
        return success;
    }
}
