namespace Prism.Core;

/// <summary>
/// What Windows shows. The code keeps its Prism code name (namespaces, folders, the pipe, the data folders); these are the
/// shipped file and service names. Setup also accepts the 1.0.0–1.0.2 names so it can move those installs over.
/// </summary>
public static class ProductNames
{
    public const string AppExe = "ControlSpawn.exe";
    public const string AppDll = "ControlSpawn.dll";
    public const string ServiceExe = "ControlSpawn.Service.exe";
    public const string FanGuardExe = "ControlSpawn.FanGuard.exe";
    /// <summary>The Windows service key. Must match the key setup creates.</summary>
    public const string ServiceKey = "ControlSpawn";

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
