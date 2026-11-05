using System.IO;
using System.Windows.Threading;

namespace ControlSpawn.App.Services;

/// <summary>
/// Keeps the app running through an unexpected error in a page or a button, and writes it to
/// %LocalAppData%\Control Spawn\app.log so a problem can be found later. Only errors that repeat fast (a loop)
/// are allowed to close the app.
/// </summary>
public static class CrashLog
{
    private static readonly string Folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), ControlSpawn.Core.ProductNames.DataFolder);
    public static string FilePath => Path.Combine(Folder, "app.log");
    private static readonly Queue<DateTime> Recent = new();

    public static void Install(System.Windows.Application app)
    {
        app.DispatcherUnhandledException += (_, e) => e.Handled = Record("UI", e.Exception);
        TaskScheduler.UnobservedTaskException += (_, e) => { Record("Background task", e.Exception); e.SetObserved(); };
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Record("Fatal", e.ExceptionObject as Exception);
    }

    /// <returns>True when the app should carry on: fewer than five errors in ten seconds.</returns>
    public static bool Record(string source, Exception? error)
    {
        try
        {
            Directory.CreateDirectory(Folder);
            if (File.Exists(FilePath) && new FileInfo(FilePath).Length > 1_000_000) File.Move(FilePath, FilePath + ".old", overwrite: true);
            File.AppendAllText(FilePath, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {source}: {error}\n\n");
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        lock (Recent)
        {
            var now = DateTime.UtcNow;
            Recent.Enqueue(now);
            while (Recent.Count > 0 && now - Recent.Peek() > TimeSpan.FromSeconds(10)) Recent.Dequeue();
            return Recent.Count < 5;
        }
    }
}
