using ControlSpawn.Core;

namespace ControlSpawn.App.Models;

/// <summary>The four approved scenes, shared by the mock service, the tray and the pages.</summary>
public sealed record SceneDefinition(
    string Name,
    string Color,
    LightingEffect Effect,
    FanMode FanMode,
    string LampFollow,
    bool LampOn,
    int LampBrightness,
    string Trigger,
    string LightingText,
    string FansText,
    string LampText,
    int Noise);

public static class SceneCatalog
{
    public static IReadOnlyList<SceneDefinition> All { get; } =
    [
        new("Gaming", "#FF4D1A", LightingEffect.Smart, FanMode.Turbo, "pc", true, 60,
            "Starts with any game on your list", "Smart (temp)", "Turbo", "Orange, 60%", 70),
        new("Work", "#E4E4E7", LightingEffect.Static, FanMode.Standard, "pc", true, 90,
            "Manual", "Static white", "Standard", "Warm white, 90%", 44),
        new("Night", "#6366F1", LightingEffect.Breathing, FanMode.Silent, "own", false, 15,
            "23:00 – 07:00", "Breathing, dim", "Silent", "Off", 22),
        new("Movie", "#F5A524", LightingEffect.Adaptive, FanMode.Silent, "average", true, 35,
            "Manual", "Adaptive (screen)", "Silent", "Follows screen", 26)
    ];

    public static SceneDefinition Get(string name) =>
        All.FirstOrDefault(s => string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase)) ?? All[0];
}
