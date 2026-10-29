namespace Prism.App.Navigation;

public enum PageKey
{
    Dashboard,
    Monitor,
    Widgets,
    Lighting,
    Fans,
    Lamp,
    Scenes,
    Modem,
    Settings,
    Devices,
    Appliances
}

public sealed record PageDefinition(PageKey Key, string Title, string Group, string Glyph, string Subtitle);

public static class Pages
{
    public static IReadOnlyList<PageDefinition> All { get; } =
    [
        new(PageKey.Dashboard, "Dashboard", "GENERAL", "▦", "Overview of everything"),
        new(PageKey.Monitor, "Monitor", "GENERAL", "⌁", "CPU, GPU, memory, storage and network"),
        new(PageKey.Devices, "Devices", "GENERAL", "\u25C7", "Find and set up devices"),
        new(PageKey.Appliances, "Air fryer", "TOOLS", "\u25F4", "Cooking status and controls"),
        new(PageKey.Widgets, "Widgets", "GENERAL", "▤", "Desktop widgets and startup"),
        new(PageKey.Lighting, "Lighting", "TOOLS", "◐", "RGB effects and devices"),
        new(PageKey.Fans, "Fans", "TOOLS", "❋", "Fan modes and curves"),
        new(PageKey.Lamp, "Lamp", "TOOLS", "▽", "Yeelight colour bulb"),
        new(PageKey.Scenes, "Scenes", "TOOLS", "✦", "One click for lighting, fans and lamp"),
        new(PageKey.Modem, "Modem", "INSIGHTS", "▮", "5G signal, ping and speed"),
        new(PageKey.Settings, "Settings", "OTHER", "⚙", "Prism preferences")
    ];

    public static PageDefinition Get(PageKey key) => All.First(p => p.Key == key);

    public static IReadOnlyList<string> Groups { get; } = ["GENERAL", "TOOLS", "INSIGHTS", "OTHER"];
}
