using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Prism.Router;

/// <summary>
/// Protects the stored secrets. The default implementation is Windows DPAPI (CurrentUser), so
/// only this Windows user on this PC can decrypt them; tests inject a fake.
/// </summary>
public interface ISecretProtector
{
    byte[] Protect(byte[] data);

    byte[] Unprotect(byte[] data);
}

/// <summary>DPAPI, scoped to the current Windows user (CryptProtectData with no entropy).</summary>
public sealed class DpapiSecretProtector : ISecretProtector
{
    public byte[] Protect(byte[] data) =>
        ProtectedData.Protect(data, optionalEntropy: null, DataProtectionScope.CurrentUser);

    public byte[] Unprotect(byte[] data) =>
        ProtectedData.Unprotect(data, optionalEntropy: null, DataProtectionScope.CurrentUser);
}

/// <summary>
/// Remembers the router password and session between runs, DPAPI-encrypted in
/// <c>%APPDATA%\Prism\router</c> (credentials.json / session.json).
/// </summary>
public sealed class CredentialStore
{
    public const string AppFolder = "Prism";
    public const string SubFolder = "router";
    public const string CredentialsFile = "credentials.json";
    public const string SessionFile = "session.json";

    private readonly ISecretProtector _protector;

    public CredentialStore(string? directory = null, ISecretProtector? protector = null)
    {
        Directory = directory ?? DefaultDirectory();
        _protector = protector ?? new DpapiSecretProtector();
    }

    public string Directory { get; }

    public static string DefaultDirectory()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        if (string.IsNullOrEmpty(appData))
        {
            appData = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        }

        return Path.Combine(appData, AppFolder, SubFolder);
    }

    public string? LoadPassword() => Load(CredentialsFile);

    public void SavePassword(string password) => Save(CredentialsFile, password);

    public void ForgetPassword()
    {
        Remove(CredentialsFile);
        ForgetSession();
    }

    public string? LoadSession() => Load(SessionFile);

    public void SaveSession(string? sessionId)
    {
        if (!string.IsNullOrEmpty(sessionId))
        {
            Save(SessionFile, sessionId);
        }
        else
        {
            ForgetSession();
        }
    }

    public void ForgetSession() => Remove(SessionFile);

    private string? Load(string name)
    {
        try
        {
            var text = File.ReadAllText(Path.Combine(Directory, name), Encoding.UTF8);
            var record = JsonNode.Parse(text) as JsonObject;
            var encoded = record?["dpapi"]?.GetValue<string>();
            if (string.IsNullOrEmpty(encoded))
            {
                return null;
            }

            var plain = _protector.Unprotect(Convert.FromBase64String(encoded));
            var secret = Encoding.UTF8.GetString(plain);
            return secret.Length > 0 ? secret : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException
                                      or FormatException or CryptographicException or InvalidOperationException)
        {
            return null;
        }
    }

    private void Save(string name, string secret)
    {
        System.IO.Directory.CreateDirectory(Directory);
        var encrypted = _protector.Protect(Encoding.UTF8.GetBytes(secret));
        var json = JsonSerializer.Serialize(new Dictionary<string, string>
        {
            ["dpapi"] = Convert.ToBase64String(encrypted),
        });
        File.WriteAllText(Path.Combine(Directory, name), json, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    }

    private void Remove(string name)
    {
        try
        {
            File.Delete(Path.Combine(Directory, name));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Nothing stored / not deletable: the caller only wanted it gone.
        }
    }
}
