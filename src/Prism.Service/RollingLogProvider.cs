using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Prism.Service;

public sealed class RollingLogProvider(string? directory = null, Func<DateTimeOffset>? now = null, Action<string>? eventError = null) : ILoggerProvider, ISupportExternalScope
{
    private readonly string directory = directory ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Prism", "logs");
    private readonly Func<DateTimeOffset> now = now ?? (() => DateTimeOffset.UtcNow);
    private readonly Action<string> eventError = eventError ?? WriteEvent;
    private readonly object gate = new();
    private IExternalScopeProvider scopes = new LoggerExternalScopeProvider();
    public void SetScopeProvider(IExternalScopeProvider scopeProvider) => scopes = scopeProvider;
    public ILogger CreateLogger(string categoryName) => new FileLogger(this, categoryName);
    public void Dispose() { }
    private static void WriteEvent(string message)
    {
        if (!OperatingSystem.IsWindows()) return;
        try { EventLog.WriteEntry("Prism", message, EventLogEntryType.Error); }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException or System.Security.SecurityException) { }
    }
    private void Write(string category, LogLevel level, string message)
    {
        var time = now();
        var activeScopes = new List<string>(); scopes.ForEachScope((s, list) => list.Add(s?.ToString() ?? ""), activeScopes);
        // Deliberately omit exception objects; arbitrary transport messages can carry credentials.
        var line = JsonSerializer.Serialize(new { time, level = level.ToString(), module = category, scopes = activeScopes, message }) + "\n";
        lock (gate)
        {
            try
            {
                Directory.CreateDirectory(directory);
                foreach (var file in Directory.GetFiles(directory, "prism-*.jsonl"))
                    if (DateTime.TryParseExact(Path.GetFileNameWithoutExtension(file)[6..], "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture,
                        System.Globalization.DateTimeStyles.None, out var date) && date.Date < time.UtcDateTime.Date.AddDays(-6)) File.Delete(file);
                var path = Path.Combine(directory, $"prism-{time:yyyy-MM-dd}.jsonl");
                int bytes = Encoding.UTF8.GetByteCount(line);
                if (bytes <= 10 * 1024 * 1024 && (!File.Exists(path) || new FileInfo(path).Length + bytes <= 10 * 1024 * 1024)) File.AppendAllText(path, line, new UTF8Encoding(false));
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
        if (level >= LogLevel.Error) eventError(line);
    }
    private sealed class FileLogger(RollingLogProvider owner, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => owner.scopes.Push(state);
        public bool IsEnabled(LogLevel level) => level >= LogLevel.Information;
        public void Log<TState>(LogLevel level, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        { if (IsEnabled(level)) owner.Write(category, level, formatter(state, null)); }
    }
}
