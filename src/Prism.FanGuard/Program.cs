using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Prism.Core;
using Prism.Hardware;
using Prism.Service;

namespace Prism.FanGuard;

/// <summary>
/// Started by the service as <c>ControlSpawn.FanGuard.exe &lt;service pid&gt;</c>. Says "ready", then expects "beat" on stdin.
/// Three seconds without one stops the controller and restores BIOS fan defaults; a closed stdin (the service died) restores
/// them at once; "stop" is the service's orderly exit after it has restored them itself.
/// </summary>
public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        using var logs = new RollingLogProvider();
        var logger = logs.CreateLogger("fans");
        if (args.Length != 1 || !int.TryParse(args[0], out var parentId))
        { logger.LogError("Fan guard started without its service"); return 2; }
        try { await RunAsync(parentId, logger); return 0; }
        catch (Exception e) { logger.LogError("Fan guard failed ({ErrorType})", e.GetType().Name); throw; }
    }

    private static async Task RunAsync(int parentId, ILogger logger)
    {
        using var scope = logger.BeginScope("{Module}", "fans");
        using var parent = Process.GetProcessById(parentId);
        if (!ProductNames.IsServiceBeside(parent.MainModule?.FileName, Environment.ProcessPath ?? ""))
            throw new InvalidOperationException("Fan guard requires its service parent.");
        var clock = Stopwatch.StartNew();
        var watchdog = new FanHeartbeat(() =>
        {
            logger.LogError("Fan heartbeat expired; stopping the controller and restoring BIOS fan defaults");
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
                if (line is null) { logger.LogError("Service heartbeat stream closed; restoring BIOS fan defaults"); LhmFanReset.RestoreAll(); return; }
                if (line == "beat") watchdog.Beat(clock.Elapsed);
                read = Task.Run(Console.ReadLine);
            }
            if (watchdog.Check(clock.Elapsed)) return;
        }
    }
}
