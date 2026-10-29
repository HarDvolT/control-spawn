namespace Prism.App.Services;
public sealed record LaunchOptions(bool Mock, bool Tray)
{
    public static LaunchOptions Parse(IEnumerable<string> args)
    {
        var flags = new HashSet<string>(args, StringComparer.OrdinalIgnoreCase);
        return new(flags.Contains("--mock"), flags.Contains("--tray"));
    }
}
