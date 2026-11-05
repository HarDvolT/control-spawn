namespace ControlSpawn.Core;

/// <summary>Library diagnostic bridge. Pass only module names and exception types to the host logger.</summary>
public static class ControlSpawnLog
{
    public static Action<string, string>? ErrorSink { get; set; }
    public static void Failure(string module, Exception error) => ErrorSink?.Invoke(module, error.GetType().Name);
    /// <summary>A fixed sentence the service wrote itself (never request data or exception text), for things the owner must act on.</summary>
    public static Action<string, string>? WarningSink { get; set; }
    public static void Warning(string module, string text) => WarningSink?.Invoke(module, text);
}
