using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Prism.Hardware;

namespace Prism.Service;

/// <summary>Monotonic, fakeable watchdog. Only successful sensor/fan cycles renew its lease.</summary>
public sealed class FanHeartbeat(Action restore, TimeSpan started)
{
    private TimeSpan last = started;
    private bool expired;
    public void Beat(TimeSpan now) { if (!expired) last = now; }
    public bool Check(TimeSpan now)
    {
        if (expired || now - last < TimeSpan.FromSeconds(3)) return expired;
        expired = true; restore(); return true;
    }
}

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

/// <summary>Separate service executable process; no shared locks or thread pool with the controller.</summary>
public sealed class FanGuardProcess : IFanGuard
{
    public bool HasExited => process.HasExited;
    private readonly Process process;
    private readonly object gate = new();
    public FanGuardProcess()
    {
        var executable = Path.Combine(AppContext.BaseDirectory, "Prism.Service.exe");
        process = Process.Start(new ProcessStartInfo(executable, $"--fan-guard {Environment.ProcessId}")
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
    public static async Task RunGuardAsync(int parentId, ILogger? logger = null)
    {
        using var scope = logger?.BeginScope("{Module}", "fans");
        using var parent = Process.GetProcessById(parentId);
        if (!string.Equals(parent.MainModule?.FileName, Environment.ProcessPath, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Fan guard requires its service parent.");
        var clock = Stopwatch.StartNew();
        var watchdog = new FanHeartbeat(() =>
        {
            logger?.LogError("Fan heartbeat expired; stopping the controller and restoring BIOS fan defaults");
            // Prevent a hung controller from resuming software writes after the BIOS handoff.
            try { if (!parent.HasExited) { parent.Kill(); parent.WaitForExit(1000); } }
            finally { LhmFanReset.RestoreAll(); }
        }, clock.Elapsed);
        Console.Out.WriteLine("ready"); Console.Out.Flush();
        Task<string?> read = Task.Run(Console.ReadLine);
        while (true)
        {
            await Task.WhenAny(read, Task.Delay(100));
            if (read.IsCompleted)
            {
                var line = await read;
                if (line == "stop") return;
                if (line is null) { logger?.LogError("Service heartbeat stream closed; restoring BIOS fan defaults"); LhmFanReset.RestoreAll(); return; }
                if (line == "beat") watchdog.Beat(clock.Elapsed);
                read = Task.Run(Console.ReadLine);
            }
            if (watchdog.Check(clock.Elapsed)) return;
        }
    }
}
