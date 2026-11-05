namespace ControlSpawn.App.Navigation;

public enum PageKey { Dashboard, Monitor, Widgets, Lighting, Fans, Lamp, Scenes, Modem, Settings, Devices, Appliances }
public sealed record PageDefinition(PageKey Key, string Title, string Group, string Glyph, string Subtitle);
public static class Pages
{
    public static IReadOnlyList<PageDefinition> All { get; } =
    [
        new(PageKey.Dashboard, "Dashboard", "GENERAL", "M3 3h7v9H3zM14 3h7v5h-7zM14 12h7v9h-7zM3 16h7v5H3z", "Dashboard"),
        new(PageKey.Monitor, "Monitor", "GENERAL", "M3 12h4l3-8 4 16 3-8h4", "Monitor"),
        new(PageKey.Devices, "Devices", "GENERAL", "M4 4h7v7H4zM13 4h7v7h-7zM4 13h7v7H4zM13 13h7v7h-7z", "Devices"),
        new(PageKey.Widgets, "Widgets", "GENERAL", "M3 3h8v8H3zM13 3h8v5h-8zM13 10h8v11h-8zM3 13h8v8H3z", "Widgets"),
        new(PageKey.Lighting, "Lighting", "CONTROL", "M9 18h6M10 21h4M12 3a6 6 0 0 0-4 10.5c.8.8 1 1.5 1 2.5h6c0-1 .2-1.7 1-2.5A6 6 0 0 0 12 3z", "Lighting"),
        new(PageKey.Lamp, "Lamps", "CONTROL", "M8 21h8M12 17v4M6 3h12l-3 8H9zM9 11a3 3 0 0 0 6 0", "Lamps"),
        new(PageKey.Fans, "Fans", "CONTROL", "M12 12m-2 0a2 2 0 1 0 4 0a2 2 0 1 0-4 0M12 10c0-4 1-7 4-7s3 4-2 7M14 12c4 0 7 1 7 4s-4 3-7-2M12 14c0 4-1 7-4 7s-3-4 2-7M10 12c-4 0-7-1-7-4s4-3 7 2", "Fans"),
        new(PageKey.Scenes, "Scenes", "CONTROL", "M12 3l2.6 5.3 5.9.9-4.3 4.1 1 5.8L12 16.4 6.8 19.1l1-5.8L3.5 9.2l5.9-.9z", "Scenes"),
        new(PageKey.Appliances, "Air fryer", "HOME", "M5 6h14v14H5zM8 3h8M9 10h6M12 13v4", "Air fryer"),
        new(PageKey.Modem, "Internet", "HOME", "M5 20v-4M10 20v-8M15 20v-12M20 20V4", "Internet"),
        new(PageKey.Settings, "Settings", "SYSTEM", "M4 6h10M18 6h2M4 12h4M12 12h8M4 18h12M18 18h2M14 4v4M8 10v4M16 16v4", "Settings"),
    ];
    public static PageDefinition Get(PageKey key) => All.First(p => p.Key == key);
    public static IReadOnlyList<string> Groups { get; } = ["GENERAL", "CONTROL", "HOME", "SYSTEM"];
}
