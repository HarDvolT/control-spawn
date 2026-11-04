using Microsoft.Win32;

namespace Prism.App.Services;

/// <summary>Per-user "Start with Windows". Behind an interface so tests never touch the registry.</summary>
public interface IStartupRegistry
{
    bool IsEnabled { get; }
    void SetEnabled(bool enabled, bool minimizedToTray);
}

public static class StartupCommand
{
    public const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    public const string ValueName = "Prism";
    public const string ShortcutName = "Control Spawn.lnk";

    public static string Build(string executablePath, bool minimizedToTray) =>
        $"\"{executablePath}\"" + (minimizedToTray ? " --tray" : string.Empty);
}

/// <summary>A shortcut in the user's Startup folder. Windows 25H2 never ran the Run key entry on the owner's PC, with no
/// policy or block logged, while the same command from this folder started on the first boot (live, 2026-11-04).</summary>
public sealed class ShortcutStartup : IStartupRegistry
{
    private readonly Func<string> _executablePath;
    private readonly string _shortcut;
    private readonly string _runKey;

    public ShortcutStartup(Func<string>? executablePath = null, string? startupFolder = null, string runKey = StartupCommand.RunKey)
    {
        _executablePath = executablePath ?? DefaultExecutablePath;
        _runKey = runKey;
        _shortcut = Path.Combine(startupFolder ?? Environment.GetFolderPath(Environment.SpecialFolder.Startup), StartupCommand.ShortcutName);
    }

    public bool IsEnabled => File.Exists(_shortcut);

    public void SetEnabled(bool enabled, bool minimizedToTray)
    {
        if (enabled)
        {
            var executable = _executablePath();
            dynamic shell = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell")
                ?? throw new IOException("Windows startup preferences are unavailable"))!;
            var link = shell.CreateShortcut(_shortcut);
            link.TargetPath = executable;
            link.Arguments = minimizedToTray ? "--tray" : string.Empty;
            link.WorkingDirectory = Path.GetDirectoryName(executable) ?? string.Empty;
            link.Save();
        }
        else File.Delete(_shortcut);
        // Earlier versions registered here. Left behind it would start a second copy if Windows ever did run it.
        using var key = Registry.CurrentUser.OpenSubKey(_runKey, writable: true);
        key?.DeleteValue(StartupCommand.ValueName, throwOnMissingValue: false);
    }

    private static string DefaultExecutablePath() =>
        Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, Prism.Core.ProductNames.AppExe);
}

/// <summary>In-memory implementation for tests and mock mode.</summary>
public sealed class InMemoryStartupRegistry : IStartupRegistry
{
    public bool IsEnabled { get; private set; }
    public string? LastCommand { get; private set; }

    public void SetEnabled(bool enabled, bool minimizedToTray)
    {
        IsEnabled = enabled;
        LastCommand = enabled ? StartupCommand.Build(@"C:\Apps\ControlSpawn.exe", minimizedToTray) : null;
    }
}
