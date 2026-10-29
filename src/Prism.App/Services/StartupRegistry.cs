using Microsoft.Win32;

namespace Prism.App.Services;

/// <summary>Per-user "Start with Windows" (HKCU Run). Behind an interface so tests never touch the registry.</summary>
public interface IStartupRegistry
{
    bool IsEnabled { get; }
    void SetEnabled(bool enabled, bool minimizedToTray);
}

public static class StartupCommand
{
    public const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    public const string ValueName = "Prism";

    public static string Build(string executablePath, bool minimizedToTray) =>
        $"\"{executablePath}\"" + (minimizedToTray ? " --tray" : string.Empty);
}

public sealed class RegistryStartup : IStartupRegistry
{
    private readonly Func<string> _executablePath;

    public RegistryStartup(Func<string>? executablePath = null)
    {
        _executablePath = executablePath ?? DefaultExecutablePath;
    }

    public bool IsEnabled
    {
        get
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(StartupCommand.RunKey, writable: false);
                return key?.GetValue(StartupCommand.ValueName) is string value && value.Length > 0;
            }
            catch
            {
                return false;
            }
        }
    }

    public void SetEnabled(bool enabled, bool minimizedToTray)
    {
        if (enabled)
        {
            using var key = Registry.CurrentUser.CreateSubKey(StartupCommand.RunKey, writable: true)
                ?? throw new IOException("Windows startup preferences are unavailable");
            key.SetValue(StartupCommand.ValueName, StartupCommand.Build(_executablePath(), minimizedToTray), RegistryValueKind.String);
        }
        else
        {
            using var key = Registry.CurrentUser.OpenSubKey(StartupCommand.RunKey, writable: true);
            key?.DeleteValue(StartupCommand.ValueName, throwOnMissingValue: false);
        }
    }

    private static string DefaultExecutablePath() =>
        Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "Prism.App.exe");
}

/// <summary>In-memory implementation for tests and mock mode.</summary>
public sealed class InMemoryStartupRegistry : IStartupRegistry
{
    public bool IsEnabled { get; private set; }
    public string? LastCommand { get; private set; }

    public void SetEnabled(bool enabled, bool minimizedToTray)
    {
        IsEnabled = enabled;
        LastCommand = enabled ? StartupCommand.Build(@"C:\Apps\Prism.App.exe", minimizedToTray) : null;
    }
}
