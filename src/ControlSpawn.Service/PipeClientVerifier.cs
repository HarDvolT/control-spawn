using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;
using ControlSpawn.Core;

namespace ControlSpawn.Service;

public interface IPipeClientVerifier
{
    VerifiedPipeClient Verify(NamedPipeServerStream pipe);
}
public sealed record VerifiedPipeClient(bool IsApp, string Key);

/// <summary>
/// Fail closed. Installation records hashes under an administrator-only DACL. A development run (console mode, started
/// from the repository's build output) passes <paramref name="devApp"/> instead: the app built beside it is trusted by
/// path, since the live script runs it from src/ControlSpawn.App/bin and no installer has recorded hashes.
/// </summary>
public sealed class PipeClientVerifier(string? devApp = null) : IPipeClientVerifier
{
    /// <summary>The app built from this repository next to a service running from its own build folder, or null when installed.</summary>
    public static string? DevAppBesideBuild(string serviceDirectory)
    {
        // .../src/ControlSpawn.Service/bin/<Configuration>/net10.0-windows/
        var framework = new DirectoryInfo(serviceDirectory.TrimEnd(Path.DirectorySeparatorChar));
        var configuration = framework.Parent; var bin = configuration?.Parent; var project = bin?.Parent;
        if (configuration is null || bin?.Name != "bin" || project?.Name != "ControlSpawn.Service" || project.Parent is null) return null;
        var app = Path.Combine(project.Parent.FullName, "ControlSpawn.App", "bin", configuration.Name, framework.Name, ProductNames.AppExe);
        return File.Exists(app) ? app : null;
    }
    /// <summary>
    /// Installed layout: Control Spawn\service\ and Control Spawn\app\ side by side, because the two ship different builds of the same
    /// runtime DLLs. Both sit under Program Files, so only administrators can change what is there.
    /// </summary>
    public static string InstalledAppDirectory(string serviceDirectory) =>
        Path.GetFullPath(Path.Combine(serviceDirectory.TrimEnd(Path.DirectorySeparatorChar), "..", "app"));
    public static string ManifestPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), ControlSpawn.Core.ProductNames.DataFolder, "install", "app-hashes.json");
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetNamedPipeClientProcessId(SafePipeHandle pipe, out uint pid);
    public VerifiedPipeClient Verify(NamedPipeServerStream pipe)
    {
        if (!OperatingSystem.IsWindows() || !GetNamedPipeClientProcessId(pipe.SafePipeHandle, out var pid)) return new(false, Guid.NewGuid().ToString());
        var key = pid.ToString(System.Globalization.CultureInfo.InvariantCulture);
        try
        {
            using var process = Process.GetProcessById(checked((int)pid));
            var image = process.MainModule?.FileName;
            if (devApp is not null) return new(image is not null && Path.GetFullPath(image).Equals(Path.GetFullPath(devApp), StringComparison.OrdinalIgnoreCase) && !process.HasExited, key);
            var appDirectory = InstalledAppDirectory(AppContext.BaseDirectory);
            var expected = Path.Combine(appDirectory, ProductNames.AppExe);
            if (image is null || !Path.GetFullPath(image).Equals(expected, StringComparison.OrdinalIgnoreCase)) return new(false, key);
            var hashes = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(ManifestPath));
            bool valid = MatchesInstallation(appDirectory, hashes);
            return new(valid && !process.HasExited, key);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidOperationException or System.ComponentModel.Win32Exception or ArgumentException or JsonException or OverflowException)
        { return new(false, key); }
    }
    public static bool MatchesInstallation(string directory, IReadOnlyDictionary<string, string>? hashes)
    {
        if (hashes is null) return false;
        foreach (var name in new[] { ProductNames.AppExe, ProductNames.AppDll })
        {
            if (!hashes.TryGetValue(name, out var hash)) return false;
            using var file = File.OpenRead(Path.Combine(directory, name));
            if (!Convert.ToHexString(SHA256.HashData(file)).Equals(hash, StringComparison.OrdinalIgnoreCase)) return false;
        }
        return true;
    }
}

/// <summary>
/// Open connections per client process. The app needs one and a script one more; without a cap any local program could
/// open hundreds of read-only connections, each pushed a state every second.
/// </summary>
public sealed class ConnectionLimiter(int perClient = ConnectionLimiter.DefaultPerClient)
{
    public const int DefaultPerClient = 4;
    private readonly Dictionary<string, int> open = new();
    public bool TryOpen(string client)
    {
        lock (open)
        {
            int count = open.GetValueOrDefault(client);
            if (count >= perClient) return false;
            open[client] = count + 1; return true;
        }
    }
    public void Close(string client)
    {
        lock (open) { if (open.TryGetValue(client, out var count)) { if (count <= 1) open.Remove(client); else open[client] = count - 1; } }
    }
    public int Open(string client) { lock (open) return open.GetValueOrDefault(client); }
}

/// <summary>Shared across connections for one process. A reconnect cannot reset the budget.</summary>
public sealed class ClientCommandLimiter(Func<TimeSpan>? now = null)
{
    private readonly Func<TimeSpan> now = now ?? (() => TimeSpan.FromSeconds(Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency));
    private readonly Dictionary<string, (TimeSpan Start, int Count)> windows = new();
    public bool Allow(string client)
    {
        lock (windows)
        {
            var time = now();
            foreach (var stale in windows.Where(p => time - p.Value.Start >= TimeSpan.FromMinutes(1)).Select(p => p.Key).ToArray()) windows.Remove(stale);
            (TimeSpan Start, int Count) window = windows.GetValueOrDefault(client, (time, 0));
            if (time - window.Start >= TimeSpan.FromSeconds(1)) window = (time, 0);
            windows[client] = (window.Start, window.Count + 1);
            return window.Count < 20;
        }
    }
}
