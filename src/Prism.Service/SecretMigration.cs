using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using Prism.Core;

namespace Prism.Service;

/// <summary>Explicit console-mode migration, including credentials for currently disabled devices.</summary>
public static class SecretMigration
{
    public static bool Run(string appData, SecretStore store)
    {
        bool success = true;
        var prism = Path.Combine(appData, ProductNames.DataFolder);
        void Migrate(string key, string path)
        {
            try
            {
                var plain = store.Load(key, path);
                if (plain is not null) CryptographicOperations.ZeroMemory(plain);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or CryptographicException or JsonException or FormatException or InvalidOperationException or KeyNotFoundException)
            { success = false; PrismLog.Failure("secrets", e); }
        }
        Migrate("lamp", Path.Combine(prism, "lamp.json"));
        try
        {
            if (Directory.Exists(prism))
                foreach (var path in Directory.GetFiles(prism, "lamp.json.*"))
                    if (uint.TryParse(Path.GetFileName(path)[10..], NumberStyles.None, CultureInfo.InvariantCulture, out var id) && id is > 0 and < uint.MaxValue)
                        Migrate("lamp." + id.ToString(CultureInfo.InvariantCulture), path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        { success = false; PrismLog.Failure("secrets", e); }
        foreach (var name in new[] { "credentials.json", "session.json" }) Migrate("router." + name, Path.Combine(prism, "router", name));
        return success;
    }
}
