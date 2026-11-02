using System.Globalization;
using Prism.Core.Ipc;

namespace Prism.Service;

/// <summary>Decides which scene the automation rules want right now. Pure logic: the caller supplies the clock, lock state and running programs.</summary>
public static class AutomationEngine
{
    // Where game stores install games. Launchers, redistributables and tools inside these folders are excluded below.
    private static readonly string[] GameFolders =
        [@"\steamapps\common\", @"\epic games\", @"\riot games\", @"\xboxgames\", @"\gog galaxy\games\", @"\gog games\", @"\ubisoft game launcher\games\", @"\ea games\", @"\battle.net\"];
    private static readonly string[] NotGames =
        [@"\epic games\launcher\", @"\riot client\", @"\steamworks shared\", @"\_commonredist\", @"\redist\", @"\directx\", @"\battle.net\battle.net", @"crashreport", @"crashhandler", @"easyanticheat", @"battleye", @"unitycrashhandler"];

    public sealed record Decision(string Rule, string Scene, string Detail);

    public static Decision? Evaluate(AutomationSettings rules, IReadOnlyCollection<string> sceneNames, DateTime localNow, bool locked, IEnumerable<string> programPaths)
    {
        bool Known(string scene) => sceneNames.Contains(scene, StringComparer.OrdinalIgnoreCase);
        if (rules.LockEnabled && locked && Known(rules.LockScene)) return new("lock", rules.LockScene, "The PC is locked");
        if (rules.GameEnabled && Known(rules.GameScene) && FindGame(programPaths, rules.ExtraGames) is { } game)
            return new("game", rules.GameScene, game + " is running");
        if (rules.NightEnabled && Known(rules.NightScene) && InNight(localNow.TimeOfDay, rules.NightStart, rules.NightEnd))
            return new("night", rules.NightScene, $"Night hours, {rules.NightStart}–{rules.NightEnd}");
        return null;
    }

    /// <summary>The first running program that looks like a game, as a friendly name ("Cyberpunk2077"), or null.</summary>
    public static string? FindGame(IEnumerable<string> programPaths, IReadOnlyList<string>? extraGames)
    {
        var extras = (extraGames ?? []).Select(g => Path.GetFileNameWithoutExtension(g.Trim())).Where(g => g.Length > 0).ToArray();
        foreach (var path in programPaths)
        {
            if (string.IsNullOrWhiteSpace(path)) continue;
            var name = Path.GetFileNameWithoutExtension(path);
            if (extras.Contains(name, StringComparer.OrdinalIgnoreCase)) return name;
            var lower = path.Replace('/', '\\').ToLowerInvariant();
            if (GameFolders.Any(lower.Contains) && !NotGames.Any(lower.Contains)) return name;
        }
        return null;
    }

    /// <summary>True when the time is inside the window; a window like 23:00–07:00 wraps past midnight.</summary>
    public static bool InNight(TimeSpan now, string start, string end)
    {
        if (!TryTime(start, out var from) || !TryTime(end, out var to) || from == to) return false;
        return from < to ? now >= from && now < to : now >= from || now < to;
    }

    public static bool TryTime(string? text, out TimeSpan time) =>
        TimeSpan.TryParseExact(text?.Trim(), [@"h\:mm", @"hh\:mm"], CultureInfo.InvariantCulture, out time) && time < TimeSpan.FromDays(1);
}
