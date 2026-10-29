using Prism.App.Models;
using Prism.Core;

namespace Prism.App.Navigation;

public enum SearchAction
{
    Navigate,
    FanMode,
    Scene,
    LightsOff,
    ToggleWidgets,
    StartSpeedTest,
    MeasureFans,
    FindDevices,
    ImportTokens
}

/// <summary>One thing the command search can do.</summary>
public sealed record SearchEntry(
    string Title,
    string Subtitle,
    string Category,
    SearchAction Action,
    string Key,
    string Keywords);

public sealed record SearchResult(string Title, string Subtitle, string Category, SearchAction Action, string Key);

/// <summary>
/// Filters pages and commands for the top-bar search ("Fan mode Turbo", "Scene Night", "Lights off").
/// Pure data, so the matching rules are unit-tested without a window.
/// </summary>
public sealed class CommandSearch
{
    private readonly List<SearchEntry> _entries = new();

    public CommandSearch()
    {
        _entries.Add(new SearchEntry("Find devices", "Discover new hardware", "COMMAND", SearchAction.FindDevices, "find", "discover detect lamps rgb fans network"));
        _entries.Add(new SearchEntry("Import tokens", "Paste or drop extractor output", "COMMAND", SearchAction.ImportTokens, "import", "xiaomi token file json csv"));
        foreach (var page in Pages.All)
            _entries.Add(new SearchEntry(page.Title, page.Subtitle, page.Group, SearchAction.Navigate,
                page.Key.ToString(), page.Group + " page open " + page.Title));

        _entries.Add(new SearchEntry("Edit LEDs", "Paint individual device LEDs", "COMMAND", SearchAction.Navigate,
            PageKey.Lighting.ToString(), "lighting rgb custom overlay brush editor"));
        foreach (var mode in new[] { FanMode.Silent, FanMode.Standard, FanMode.Turbo, FanMode.Full })
            _entries.Add(new SearchEntry($"Fan mode · {Common.Format.FanModeName(mode)}",
                "Set every controllable fan", "COMMAND", SearchAction.FanMode, mode.ToString(),
                "fans fan speed mode " + mode));

        foreach (var scene in SceneCatalog.All)
            _entries.Add(new SearchEntry($"Scene · {scene.Name}", scene.Trigger, "COMMAND",
                SearchAction.Scene, scene.Name, "scenes scene apply lighting profile"));

        _entries.Add(new SearchEntry("Lights off", "Turn every RGB device and the lamp off", "COMMAND",
            SearchAction.LightsOff, "off", "lighting rgb lamp dark black out"));
        _entries.Add(new SearchEntry("Show widgets", "Pin the desktop widgets", "COMMAND",
            SearchAction.ToggleWidgets, "show", "widgets desktop show hide tray"));
        _entries.Add(new SearchEntry("Hide widgets", "Remove the desktop widgets", "COMMAND",
            SearchAction.ToggleWidgets, "hide", "widgets desktop show hide tray"));
        _entries.Add(new SearchEntry("Start speed test", "Measure ping, download and upload", "COMMAND",
            SearchAction.StartSpeedTest, "start", "modem router internet speed test cloudflare"));
        _entries.Add(new SearchEntry("Measure fan speeds", "Learn the min and max RPM of each fan", "COMMAND",
            SearchAction.MeasureFans, "measure", "fans rpm calibration measure"));
    }

    public IReadOnlyList<SearchResult> Search(string? query, int limit = 8)
    {
        if (limit <= 0) return [];
        var text = (query ?? string.Empty).Trim().ToLowerInvariant();
        if (text.Length == 0)
            return Pages.All.Take(limit)
                .Select(p => new SearchResult(p.Title, p.Subtitle, p.Group, SearchAction.Navigate, p.Key.ToString()))
                .ToList();

        var tokens = text.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var scored = new List<(SearchEntry Entry, int Score)>();

        foreach (var entry in _entries)
        {
            var title = entry.Title.ToLowerInvariant();
            var subtitle = entry.Subtitle.ToLowerInvariant();
            var keywords = entry.Keywords.ToLowerInvariant();
            var haystack = title + " " + subtitle + " " + keywords;
            if (tokens.Any(t => !haystack.Contains(t, StringComparison.Ordinal))) continue;

            var score = 0;
            if (title.Contains(text, StringComparison.Ordinal)) score += 120;
            if (title.StartsWith(text, StringComparison.Ordinal)) score += 80;
            foreach (var token in tokens)
            {
                if (title.StartsWith(token, StringComparison.Ordinal)) score += 70;
                else if (title.Contains(token, StringComparison.Ordinal)) score += 40;
                if (subtitle.Contains(token, StringComparison.Ordinal)) score += 15;
                if (keywords.Contains(token, StringComparison.Ordinal)) score += 10;
            }
            // Prefer exact command wording over the page that merely mentions it.
            if (entry.Category == "COMMAND") score += 5;
            scored.Add((entry, score));
        }

        return scored
            .OrderByDescending(x => x.Score)
            .ThenBy(x => x.Entry.Title, StringComparer.OrdinalIgnoreCase)
            .Take(limit)
            .Select(x => new SearchResult(x.Entry.Title, x.Entry.Subtitle, x.Entry.Category, x.Entry.Action, x.Entry.Key))
            .ToList();
    }
}
