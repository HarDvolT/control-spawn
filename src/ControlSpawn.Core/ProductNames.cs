namespace ControlSpawn.Core;

/// <summary>
/// Every name a PC shows. Only the source keeps the Control Spawn code name (namespaces, project folders). Setup still knows the
/// 1.0.0–1.0.3 names, but only to move those installs over and remove them.
/// </summary>
public static class ProductNames
{
    public const string AppExe = "ControlSpawn.exe";
    public const string AppDll = "ControlSpawn.dll";
    public const string ServiceExe = "ControlSpawn.Service.exe";
    public const string FanGuardExe = "ControlSpawn.FanGuard.exe";
    /// <summary>The Windows service key. Must match the key setup creates.</summary>
    public const string ServiceKey = "ControlSpawn";
    /// <summary>The folder name under ProgramData, %APPDATA% and %LOCALAPPDATA%. Must match setup.</summary>
    public const string DataFolder = "Control Spawn";
    /// <summary>The Application event log source. Must match the source setup registers.</summary>
    public const string EventSource = "Control Spawn";

    /// <summary>
    /// The fan guard may stop its parent, so it only serves the real service: the service exe in the guard's own folder
    /// (setup installs both into service\, and a build copies the guard beside the service).
    /// </summary>
    public static bool IsServiceBeside(string? parentImage, string guardImage)
    {
        var folder = Path.GetDirectoryName(Path.GetFullPath(guardImage));
        return parentImage is not null && folder is not null &&
            string.Equals(Path.GetFullPath(parentImage), Path.Combine(folder, ServiceExe), StringComparison.OrdinalIgnoreCase);
    }
}
