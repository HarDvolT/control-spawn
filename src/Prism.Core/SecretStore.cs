using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text.Json;

namespace Prism.Core;

public interface ISecretEncryption
{
    byte[] Protect(byte[] value, byte[] entropy);
    byte[] Unprotect(byte[] value, byte[] entropy);
    byte[] UnprotectLegacy(byte[] value);
}

public sealed class MachineSecretEncryption : ISecretEncryption
{
    public byte[] Protect(byte[] value, byte[] entropy) => OperatingSystem.IsWindows()
        ? ProtectedData.Protect(value, entropy, DataProtectionScope.LocalMachine) : throw new PlatformNotSupportedException();
    public byte[] Unprotect(byte[] value, byte[] entropy) => OperatingSystem.IsWindows()
        ? ProtectedData.Unprotect(value, entropy, DataProtectionScope.LocalMachine) : throw new PlatformNotSupportedException();
    public byte[] UnprotectLegacy(byte[] value) => OperatingSystem.IsWindows()
        ? ProtectedData.Unprotect(value, null, DataProtectionScope.CurrentUser) : throw new PlatformNotSupportedException();
}

/// <summary>Service-owned machine secrets. All filesystem access is lazy; callers own returned plaintext.</summary>
public sealed class SecretStore(string? directory = null, ISecretEncryption? encryption = null, Action<string>? secureDirectory = null)
{
    public static string DefaultDirectory => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), Prism.Core.ProductNames.DataFolder, "secrets");
    private readonly string directory = directory ?? DefaultDirectory;
    private readonly ISecretEncryption encryption = encryption ?? new MachineSecretEncryption();
    private readonly Action<string> secureDirectory = secureDirectory ?? SecureDirectory;
    private static readonly object Gate = new();
    public static void SecureDirectory(string path)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        RejectReparsePoints(path);
        var acl = CreateSecurity();
        // Supply the DACL at creation, before any entropy or ciphertext can appear.
        new DirectoryInfo(path).Create(acl);
        new DirectoryInfo(path).SetAccessControl(acl);
        foreach (var file in new DirectoryInfo(path).GetFiles())
        {
            if ((file.Attributes & FileAttributes.ReparsePoint) != 0) throw new IOException("Reparse points are not supported in the secret store.");
            var fileAcl = new FileSecurity(); fileAcl.SetAccessRuleProtection(true, false);
            foreach (var sid in new[] { WellKnownSidType.LocalSystemSid, WellKnownSidType.BuiltinAdministratorsSid })
                fileAcl.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(sid, null), FileSystemRights.FullControl, AccessControlType.Allow));
            file.SetAccessControl(fileAcl);
        }
    }
    private static void RejectReparsePoints(string path)
    {
        for (var item = new DirectoryInfo(Path.GetFullPath(path)); item is not null; item = item.Parent)
            if (item.Exists && (item.Attributes & FileAttributes.ReparsePoint) != 0) throw new IOException("Reparse points are not supported in the secret store.");
    }
    public static DirectorySecurity CreateSecurity()
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        var acl = new DirectorySecurity();
        acl.SetAccessRuleProtection(true, false);
        foreach (var sid in new[] { WellKnownSidType.LocalSystemSid, WellKnownSidType.BuiltinAdministratorsSid })
            acl.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(sid, null), FileSystemRights.FullControl,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        return acl;
    }
    private string FilePath(string name)
    {
        if (string.IsNullOrEmpty(name) || name.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '.' or '-')) || name.StartsWith('.')) throw new ArgumentException("Invalid secret key.");
        return Path.Combine(directory, name);
    }
    private byte[] Entropy()
    {
        secureDirectory(directory);
        var path = Path.Combine(directory, "entropy.bin");
        if (!File.Exists(path))
        {
            if (System.IO.Directory.GetFiles(directory).Any(p => !p.EndsWith(".tmp", StringComparison.Ordinal))) throw new CryptographicException("Install entropy is missing.");
            try { AtomicWrite(path, RandomNumberGenerator.GetBytes(32), false); }
            catch (IOException) when (File.Exists(path)) { }
        }
        var entropy = File.ReadAllBytes(path);
        if (entropy.Length != 32) throw new CryptographicException("Invalid install entropy.");
        return entropy;
    }
    private static void AtomicWrite(string path, byte[] value, bool overwrite = true)
    {
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try { File.WriteAllBytes(temporary, value); File.Move(temporary, path, overwrite); }
        finally { File.Delete(temporary); }
    }
    public byte[] ProtectPayload(byte[] value)
    {
        lock (Gate)
        {
            var entropy = Entropy();
            try { var encrypted = encryption.Protect(value, entropy); secureDirectory(directory); return encrypted; }
            finally { CryptographicOperations.ZeroMemory(entropy); }
        }
    }
    public byte[] UnprotectPayload(byte[] value)
    {
        lock (Gate)
        {
            if (!File.Exists(Path.Combine(directory, "entropy.bin"))) throw new CryptographicException("Install entropy is missing.");
            var entropy = Entropy();
            try { return encryption.Unprotect(value, entropy); }
            finally { CryptographicOperations.ZeroMemory(entropy); }
        }
    }
    public void Save(string name, byte[] value)
    {
        lock (Gate) { var path = FilePath(name); var entropy = Entropy();
            try { AtomicWrite(path, encryption.Protect(value, entropy)); secureDirectory(directory); }
            finally { CryptographicOperations.ZeroMemory(entropy); } }
    }
    public byte[]? Load(string name, string? legacyPath = null)
    {
        lock (Gate)
        {
            var path = FilePath(name);
            if (File.Exists(path))
            {
                if (!File.Exists(Path.Combine(directory, "entropy.bin"))) throw new CryptographicException("Install entropy is missing.");
                var entropy = Entropy();
                try { return encryption.Unprotect(File.ReadAllBytes(path), entropy); }
                finally { CryptographicOperations.ZeroMemory(entropy); }
            }
            if (legacyPath is null || !File.Exists(legacyPath)) return null;
            // CurrentUser DPAPI authenticates the identity; LocalSystem cannot migrate another user's record.
            using var json = JsonDocument.Parse(File.ReadAllText(legacyPath));
            var plain = encryption.UnprotectLegacy(Convert.FromBase64String(json.RootElement.GetProperty("dpapi").GetString()!));
            try { Save(name, plain); File.Delete(legacyPath); return plain.ToArray(); }
            finally { CryptographicOperations.ZeroMemory(plain); }
        }
    }
    public void Remove(string name) { lock (Gate) File.Delete(FilePath(name)); }
}
