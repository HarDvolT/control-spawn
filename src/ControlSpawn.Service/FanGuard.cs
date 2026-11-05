using System.Diagnostics;
using ControlSpawn.Core;

namespace ControlSpawn.Service;

public interface IFanGuard : IDisposable
{
    bool HasExited { get; }
    void Beat();
}

/// <summary>
/// Keeps one live guard. A dead guard used to stay in place, so every tick failed and the curves stopped applying until the
/// service restarted. Now it is disposed and a new one started; no fan write happens until the new one says it is ready.
/// </summary>
public sealed class FanGuardKeeper(Func<IFanGuard> start) : IDisposable
{
    public const string Warning = "The fan guard stopped, so the fans are on BIOS control until it restarts.";
    private readonly object gate = new();
    private IFanGuard? guard;
    public int Restarts { get; private set; }
    /// <summary>Throws (leaving no guard) when one can't be started; the caller must not write fans then.</summary>
    public void Ensure()
    {
        lock (gate)
        {
            if (guard is { HasExited: true } dead) { dead.Dispose(); guard = null; Restarts++; }
            guard ??= start();
        }
    }
    public void Beat() { lock (gate) guard?.Beat(); }
    public void Dispose() { lock (gate) { guard?.Dispose(); guard = null; } }
}

/// <summary>ControlSpawn.FanGuard.exe as a child process (src/ControlSpawn.FanGuard); no shared locks or thread pool with the controller.
/// It is not a Windows service: it lives only while this service runs, and a dead service closes its stdin.</summary>
public sealed class FanGuardProcess : IFanGuard
{
    public bool HasExited => process.HasExited;
    private readonly Process process;
    private readonly object gate = new();
    public FanGuardProcess()
    {
        var executable = Path.Combine(AppContext.BaseDirectory, ProductNames.FanGuardExe);
        process = Process.Start(new ProcessStartInfo(executable, Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture))
        { UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true })
            ?? throw new IOException("Cannot start fan guard.");
        // No fan writes are allowed until the independent watchdog acknowledges readiness.
        var ready = process.StandardOutput.ReadLineAsync();
        if (!ready.Wait(TimeSpan.FromSeconds(5)) || ready.Result != "ready")
        { process.Kill(); process.Dispose(); throw new IOException("Fan guard did not become ready."); }
    }
    public void Beat()
    {
        lock (gate)
        {
            if (process.HasExited) throw new IOException("Fan guard exited.");
            process.StandardInput.WriteLine("beat"); process.StandardInput.Flush();
        }
    }
    public void Dispose()
    {
        lock (gate)
        {
            try { if (!process.HasExited) { process.StandardInput.WriteLine("stop"); process.StandardInput.Flush(); process.WaitForExit(5000); } }
            catch (IOException) { }
            finally { process.Dispose(); }
        }
    }
}
