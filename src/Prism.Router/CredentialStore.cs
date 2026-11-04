using Prism.Core;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Prism.Router;

/// <summary>
/// Adapter encryption contract. Production uses machine DPAPI with service-owned install entropy; tests inject a fake.
/// </summary>
public interface ISecretProtector
{
    byte[] Protect(byte[] data);

    byte[] Unprotect(byte[] data);
}

/// <summary>Machine DPAPI with the service install entropy.</summary>
public sealed class DpapiSecretProtector : ISecretProtector
{
    private readonly SecretStore store = new();
    public byte[] Protect(byte[] data) => store.ProtectPayload(data);
    public byte[] Unprotect(byte[] data) => store.UnprotectPayload(data);
}

/// <summary>
/// Remembers the router password and session between runs, machine DPAPI-encrypted in the service SecretStore; legacy records in
/// <c>%APPDATA%\Prism\router</c> (credentials.json / session.json).
/// </summary>
public sealed class CredentialStore
{
    public const string AppFolder = ProductNames.DataFolder;
    public const string SubFolder = "router";
    public const string CredentialsFile = "credentials.json";
    public const string SessionFile = "session.json";

    private readonly ISecretProtector _protector;
    private readonly SecretStore? secrets;

    public CredentialStore(string? directory = null, ISecretProtector? protector = null, SecretStore? secrets = null)
    {
        this.secrets = secrets ?? (protector is null ? new SecretStore() : null);
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
            if (secrets is not null)
            {
                var bytes = secrets.Load("router." + name, Path.Combine(Directory, name));
                if (bytes is null) return null;
                try { return Encoding.UTF8.GetString(bytes); } finally { CryptographicOperations.ZeroMemory(bytes); }
            }
            var text = File.ReadAllText(Path.Combine(Directory, name), Encoding.UTF8);
            var record = JsonNode.Parse(text) as JsonObject;
            var encoded = record?["dpapi"]?.GetValue<string>();
            if (string.IsNullOrEmpty(encoded))
            {
                return null;
            }

            var plain = _protector.Unprotect(Convert.FromBase64String(encoded));
            try { var secret = Encoding.UTF8.GetString(plain); return secret.Length > 0 ? secret : null; }
            finally { CryptographicOperations.ZeroMemory(plain); }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException
                                      or FormatException or CryptographicException or InvalidOperationException)
        {
            return null;
        }
    }

    private void Save(string name, string secret)
    {
        var plain = Encoding.UTF8.GetBytes(secret);
        if (secrets is not null) { try { secrets.Save("router." + name, plain); } finally { CryptographicOperations.ZeroMemory(plain); } return; }
        System.IO.Directory.CreateDirectory(Directory);
        byte[] encrypted;
        try { encrypted = _protector.Protect(plain); } finally { CryptographicOperations.ZeroMemory(plain); }
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
            if (secrets is not null) secrets.Remove("router." + name);
            else File.Delete(Path.Combine(Directory, name));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Nothing stored / not deletable: the caller only wanted it gone.
        }
    }
}
