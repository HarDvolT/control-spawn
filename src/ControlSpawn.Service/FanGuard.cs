using System.Diagnostics;
using ControlSpawn.Core;

namespace ControlSpawn.Service;

public interface IFanGuard : IDisposable
{
    bool HasExited { get; }
    /// <summary>True when a heartbeat write has been stuck too long: the guard is alive but no longer reading.</summary>
    bool Stalled => false;
    /// <summary>Never blocks. Throws <see cref="IOException"/> when the guard has exited.</summary>
    void Beat();
}

/// <summary>
/// Keeps one live guard without ever blocking the fan loop. A guard starts on a background task. Until it says it is
/// ready, <see cref="Ensure"/> returns false and the caller must leave the fans to the BIOS. A dead or stalled guard is
/// dropped and a new one started. A guard that fails to start is tried again after <see cref="RetryAfterFailure"/>.
/// </summary>
public sealed class FanGuardKeeper(Func<IFanGuard> start, TimeProvider? time = null) : IDisposable
{
    public const string Warning = "The fan guard stopped, so the fans are on BIOS control until it restarts.";
    public static readonly TimeSpan RetryAfterFailure = TimeSpan.FromSeconds(5);
    private readonly TimeProvider time = time ?? TimeProvider.System;
    private readonly object gate = new();
    private IFanGuard? guard;
    private Task<IFanGuard>? starting;
    private long? retryAt;
    private bool disposed;
    public int Restarts { get; private set; }
    public Exception? LastStartError { get; private set; }

    /// <summary>True when a ready guard is in place. False means no software fan writes this tick.</summary>
    public bool Ensure()
    {
        lock (gate)
        {
            if (disposed) return false;
            if (guard is { } current && (current.HasExited || current.Stalled))
            {
                guard = null; Restarts++;
                // Dropping a stalled guard can wait on its process. Never on the fan loop.
                _ = Task.Run(() => { try { current.Dispose(); } catch (Exception e) { ControlSpawnLog.Failure("fans", e); } });
            }
            if (guard is not null) return true;
            if (starting is null)
            {
                if (retryAt is { } at && time.GetTimestamp() < at) return false;
                starting = Task.Run(start);
            }
            if (!starting.IsCompleted) return false;
            var done = starting; starting = null;
            if (done.IsCompletedSuccessfully) { guard = done.Result; LastStartError = null; retryAt = null; return true; }
            LastStartError = done.Exception?.InnerException ?? done.Exception;
            retryAt = time.GetTimestamp() + (long)(RetryAfterFailure.TotalSeconds * time.TimestampFrequency);
            return false;
        }
    }

    public void Beat() { lock (gate) guard?.Beat(); }

    public void Dispose()
    {
        IFanGuard? current; Task<IFanGuard>? pending;
        lock (gate) { disposed = true; current = guard; guard = null; pending = starting; starting = null; }
        current?.Dispose();
        // A guard still starting is stopped as soon as it is up, so it cannot outlive the service.
        pending?.ContinueWith(t => { if (t.IsCompletedSuccessfully) t.Result.Dispose(); }, TaskScheduler.Default);
    }
}

/// <summary>ControlSpawn.FanGuard.exe as a child process (src/ControlSpawn.FanGuard). No shared locks or thread pool with the controller.
/// It is not a Windows service: it lives only while this service runs, and a dead service closes its stdin.
/// Heartbeats are written by their own thread, so a guard that stops reading can never block the fan loop.</summary>
public sealed class FanGuardProcess : IFanGuard
{
    public static readonly TimeSpan StallAfter = TimeSpan.FromSeconds(2);
    public bool HasExited => process.HasExited;
    public bool Stalled
    {
        get
        {
            if (Volatile.Read(ref writeFailed)) return true;
            long since = Volatile.Read(ref writeStartedAt);
            return since != 0 && Stopwatch.GetElapsedTime(since) > StallAfter;
        }
    }
    private readonly Process process;
    private readonly SemaphoreSlim pending = new(0, 1);
    private readonly Thread writer;
    private long writeStartedAt;
    private bool writeFailed;
    private volatile bool disposed;

    public FanGuardProcess()
    {
        var executable = Path.Combine(AppContext.BaseDirectory, ProductNames.FanGuardExe);
        process = Process.Start(new ProcessStartInfo(executable, Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture))
        { UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true })
            ?? throw new IOException("Cannot start fan guard.");
        // No fan writes are allowed until the independent watchdog says it is ready. This runs on the keeper's
        // background task, not on the fan loop.
        var ready = process.StandardOutput.ReadLineAsync();
        if (!ready.Wait(TimeSpan.FromSeconds(5)) || ready.Result != "ready")
        {
            try { process.Kill(); } catch (InvalidOperationException) { }
            process.Dispose(); throw new IOException("Fan guard did not become ready.");
        }
        writer = new Thread(WriteBeats) { IsBackground = true, Name = "Control Spawn fan guard heartbeat" };
        writer.Start();
    }

    public void Beat()
    {
        if (process.HasExited) throw new IOException("Fan guard exited.");
        try { if (pending.CurrentCount == 0) pending.Release(); } catch (SemaphoreFullException) { }
    }

    private void WriteBeats()
    {
        while (true)
        {
            pending.Wait();
            if (disposed) return;
            Volatile.Write(ref writeStartedAt, Stopwatch.GetTimestamp());
            try { process.StandardInput.WriteLine("beat"); process.StandardInput.Flush(); }
            catch (Exception e) when (e is IOException or ObjectDisposedException or InvalidOperationException) { Volatile.Write(ref writeFailed, true); return; }
            finally { Volatile.Write(ref writeStartedAt, 0); }
        }
    }

    public void Dispose()
    {
        if (disposed) return;
        bool stalled = Stalled;
        disposed = true;
        try { pending.Release(); } catch (SemaphoreFullException) { }
        try
        {
            if (!process.HasExited && !stalled)
            {
                // Orderly exit: the service has already restored the fans. Bounded, in case the guard stops reading.
                var stop = Task.Run(() => { if (writer.Join(1000)) { process.StandardInput.WriteLine("stop"); process.StandardInput.Flush(); } });
                if (stop.Wait(2000)) process.WaitForExit(3000);
            }
            if (!process.HasExited) process.Kill();
        }
        catch (Exception e) when (e is IOException or InvalidOperationException or AggregateException or System.ComponentModel.Win32Exception) { }
        finally { process.Dispose(); }
    }
}
