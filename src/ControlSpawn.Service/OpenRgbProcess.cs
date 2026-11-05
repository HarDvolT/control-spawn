using System.Diagnostics;

namespace ControlSpawn.Service;

public sealed class OpenRgbProcess : IDisposable
{
    private Process? process;
    /// <summary>Explicit opt-in only. Tests never call this method.</summary>
    public void Start(string executable)
    {
        if (process is { HasExited: false }) return;
        var path = Path.GetFullPath(executable);
        if (!File.Exists(path) || !Path.GetFileName(path).Equals("OpenRGB.exe", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Configure a bundled OpenRGB.exe path.");
        process = Process.Start(new ProcessStartInfo(path)
        {
            UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden,
            WorkingDirectory = Path.GetDirectoryName(path)!, ArgumentList = { "--server", "--noautoconnect" }
        });
    }
    // Release only our handle. Never terminate an RGB server that may have other clients.
    public void Dispose() => process?.Dispose();
}
