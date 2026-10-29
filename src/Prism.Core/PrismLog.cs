namespace Prism.Core;

/// <summary>Library diagnostic bridge. Pass only module names and exception types to the host logger.</summary>
public static class PrismLog
{
    public static Action<string, string>? ErrorSink { get; set; }
    public static void Failure(string module, Exception error) => ErrorSink?.Invoke(module, error.GetType().Name);
}
