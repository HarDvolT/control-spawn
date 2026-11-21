using System.Diagnostics;
using System.Text.Json;
using ControlSpawn.Hardware;

namespace ControlSpawn.Service;

internal static class GpuSensorHost
{
    public static async Task RunAsync(int parentId)
    {
        using var parent = FindParent(parentId);
        if (parent is null) return;
        using var stop = new CancellationTokenSource();
        var parentExit = parent.WaitForExitAsync(stop.Token);
        if (parentExit.IsCompleted) return;

        // Keep watching the parent while the command loop is inside a driver call.
        var commands = Task.Run(RunCommands);
        await Task.WhenAny(parentExit, commands);
        // EOF normally lets the loop close LHM. If the driver is stuck, Main must still return.
        try { await commands.WaitAsync(TimeSpan.FromMilliseconds(250)); }
        catch (TimeoutException) { }
        finally { stop.Cancel(); }
    }

    private static Process? FindParent(int id)
    {
        try { return Process.GetProcessById(id); }
        catch (ArgumentException) { return null; }
    }

    private static void RunCommands()
    {
        var reply = Console.Out;
        Console.SetOut(TextWriter.Null); // Reserve stdout for protocol lines even if a library writes to the console.
        using var sensors = new LhmGpuSensorSource();
        try
        {
            reply.WriteLine("ready");
            reply.Flush();
            while (Console.ReadLine() is { } command && command != "stop")
            {
                if (command != "read") continue;
                string line;
                try { line = JsonSerializer.Serialize(sensors.Read()); }
                catch (Exception ex) { line = JsonSerializer.Serialize(new { error = ex.GetType().Name }); }
                reply.WriteLine(line);
                reply.Flush();
            }
        }
        catch (IOException) { } // The parent closed the pipe.
    }
}
