using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;

namespace Prism.Service;

public interface IPipeClientVerifier
{
    VerifiedPipeClient Verify(NamedPipeServerStream pipe);
}
public sealed record VerifiedPipeClient(bool IsApp, string Key);

/// <summary>Fail closed. Installation records hashes under an administrator-only DACL.</summary>
public sealed class PipeClientVerifier : IPipeClientVerifier
{
    public static string ManifestPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Prism", "install", "app-hashes.json");
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
            var expected = Path.Combine(AppContext.BaseDirectory, "Prism.App.exe");
            if (image is null || !Path.GetFullPath(image).Equals(Path.GetFullPath(expected), StringComparison.OrdinalIgnoreCase)) return new(false, key);
            var hashes = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(ManifestPath));
            bool valid = MatchesInstallation(AppContext.BaseDirectory, hashes);
            return new(valid && !process.HasExited, key);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidOperationException or System.ComponentModel.Win32Exception or ArgumentException or JsonException or OverflowException)
        { return new(false, key); }
    }
    public static bool MatchesInstallation(string directory, IReadOnlyDictionary<string, string>? hashes)
    {
        if (hashes is null) return false;
        foreach (var name in new[] { "Prism.App.exe", "Prism.App.dll" })
        {
            if (!hashes.TryGetValue(name, out var hash)) return false;
            using var file = File.OpenRead(Path.Combine(directory, name));
            if (!Convert.ToHexString(SHA256.HashData(file)).Equals(hash, StringComparison.OrdinalIgnoreCase)) return false;
        }
        return true;
    }
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
