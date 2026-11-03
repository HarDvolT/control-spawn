namespace Prism.Service;

/// <summary>
/// Where the elevated service may start OpenRGB from. The service runs as admin/LocalSystem and the app check trusts the
/// connecting process's file path, which any program running as the user can borrow by starting the app and injecting
/// into it. So an OpenRGB.exe the user can replace would let a normal program run code as admin. Only folders that need
/// administrator rights to change are allowed: Program Files and the folder the service itself runs from.
/// %ProgramData% is left out on purpose: ordinary users may create folders there.
/// </summary>
public static class OpenRgbLocation
{
    public const string Rule = "OpenRGB must be inside Program Files or the Control Spawn folder, where only administrators can replace it.";
    public static IReadOnlyList<string> AdminOnlyFolders() =>
        new[] { Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), AppContext.BaseDirectory }
            .Where(f => !string.IsNullOrWhiteSpace(f)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    public static bool IsAllowed(string? path) => IsAllowed(path, AdminOnlyFolders());
    public static bool IsAllowed(string? path, IEnumerable<string> folders)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path)) return false;
        string full;
        try { full = Path.GetFullPath(path); } catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException) { return false; }
        // GetFullPath folds "..", so "Program Files\..\Users\x" can't pass; compare whole folder names, not prefixes.
        return folders.Any(folder => full.StartsWith(Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder)) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase));
    }
    public static string Warning(string path) => $"Not starting OpenRGB from {path}. {Rule} Move OpenRGB there and choose it again in Settings.";
}
