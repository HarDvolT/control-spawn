using System.Diagnostics;
using System.Text.Json;
using Prism.Core.Ipc;

namespace Prism.Service;

/// <summary>Scene automations: the service re-checks the rules every few seconds and applies or restores scenes.</summary>
public sealed partial class CommandProcessor
{
    /// <summary>What the user had before an automation took over, restored when the rule ends.</summary>
    private sealed record Snapshot(Dictionary<string, FanConfiguration> Fans, Dictionary<int, FanConfiguration> HeaderFans,
        LightingConfiguration Lighting, LampConfiguration Lamp, IReadOnlyList<LampConfiguration>? Lamps);

    private AutomationEngine.Decision? activeAutomation;
    private Snapshot? beforeAutomation;
    private bool userChangedDuringAutomation, automationApplying;
    private volatile bool sessionLocked;
    private readonly SemaphoreSlim automationWake = new(0, 1);

    public static readonly TimeSpan AutomationInterval = TimeSpan.FromSeconds(3);
    public Func<DateTime> Clock { get; set; } = () => DateTime.Now;
    public Func<IEnumerable<string>> RunningPrograms { get; set; } = ListPrograms;

    public AutomationState Automations
    {
        get { var active = activeAutomation; return new(Settings.Automations, active?.Rule, active?.Scene, active?.Detail); }
    }

    public async Task RunAutomationsAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try { await EvaluateAutomationsAsync(token); }
            catch (Exception e) when (e is IOException or InvalidOperationException or ArgumentException) { }
            try { await automationWake.WaitAsync(AutomationInterval, token); }
            catch (OperationCanceledException) { return; }
        }
    }

    /// <summary>One pass: apply the scene a rule wants, or restore what the user had once no rule applies.</summary>
    public async Task EvaluateAutomationsAsync(CancellationToken token = default)
    {
        var current = Settings;
        var decision = AutomationEngine.Evaluate(current.Automations, current.Scenes.Keys, Clock(), sessionLocked, RunningPrograms());
        if (decision?.Scene == activeAutomation?.Scene && decision?.Rule == activeAutomation?.Rule) { activeAutomation = decision; return; }
        await gate.WaitAsync(token);
        try
        {
            var before = activeAutomation;
            if (decision is not null && current.Scenes.TryGetValue(decision.Scene, out var scene))
            {
                if (before is null) { beforeAutomation = Capture(Settings); userChangedDuringAutomation = false; }
                activeAutomation = decision;
                if (before?.Scene != decision.Scene) await CommitFromAutomationAsync(WithScene(Settings, scene));
            }
            else
            {
                activeAutomation = null;
                // If the user changed lighting, fans or lamps meanwhile, their choice stays.
                if (beforeAutomation is { } saved && !userChangedDuringAutomation) await CommitFromAutomationAsync(Restore(Settings, saved));
                beforeAutomation = null;
            }
        }
        finally { gate.Release(); }
        runtime.TriggerLightingEvent(LightingEventKind.SceneChange);
        StateChanged?.Invoke();
    }

    private async Task CommitFromAutomationAsync(ServiceSettings next)
    {
        automationApplying = true;
        try { next.Validate(); store.Save(next); Volatile.Write(ref settings, next); await runtime.ApplyAsync(next, CancellationToken.None); }
        finally { automationApplying = false; }
    }

    /// <summary>Called for every committed user command: a manual change to what a scene controls means "keep this".</summary>
    private void NoteUserChange(ServiceSettings before, ServiceSettings next)
    {
        if (activeAutomation is null || automationApplying) return;
        if (!Equals(before.Lighting, next.Lighting) || !before.Fans.Values.SequenceEqual(next.Fans.Values) || !Equals(before.Lamp, next.Lamp)
            || !(before.Lamps ?? []).SequenceEqual(next.Lamps ?? [])) userChangedDuringAutomation = true;
    }

    private void SetSessionLocked(JsonElement a)
    {
        sessionLocked = a.GetProperty("locked").GetBoolean();
        if (automationWake.CurrentCount == 0) automationWake.Release();
    }

    private static AutomationSettings UpdateAutomations(AutomationSettings current, JsonElement a)
    {
        bool Flag(string key, bool value) => a.TryGetProperty(key, out var v) ? v.GetBoolean() : value;
        string Str(string key, string value) => a.TryGetProperty(key, out var v) ? v.GetString() ?? value : value;
        var extras = a.TryGetProperty("extraGames", out var games) && games.ValueKind == JsonValueKind.Array
            ? games.EnumerateArray().Select(g => g.GetString()?.Trim() ?? "").Where(g => g.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).Take(50).ToArray()
            : current.ExtraGames;
        return new(Flag("gameEnabled", current.GameEnabled), Str("gameScene", current.GameScene),
            Flag("nightEnabled", current.NightEnabled), Str("nightScene", current.NightScene), Str("nightStart", current.NightStart), Str("nightEnd", current.NightEnd),
            Flag("lockEnabled", current.LockEnabled), Str("lockScene", current.LockScene), extras);
    }

    private static ServiceSettings WithScene(ServiceSettings s, SceneConfiguration scene) => s with
    {
        Fans = s.Fans.ToDictionary(f => f.Key, f => f.Value with { Mode = scene.Fans }),
        HeaderFans = s.HeaderFans.ToDictionary(f => f.Key, f => f.Value with { Mode = scene.Fans }),
        Lighting = scene.Lighting, Lamp = scene.Lamp with { Host = s.Lamp.Host },
        Lamps = s.Lamps?.Select(l => l with { On = scene.Lamp.On, Brightness = scene.Lamp.Brightness, Color = scene.Lamp.Color, Follow = scene.Lamp.Follow, Kelvin = scene.Lamp.Kelvin }).ToArray()
    };

    private static Snapshot Capture(ServiceSettings s) => new(new(s.Fans), new(s.HeaderFans), s.Lighting, s.Lamp, s.Lamps?.ToArray());

    // Restore only what a scene changes; lamps are matched by id so ones added or removed meanwhile are left alone.
    private static ServiceSettings Restore(ServiceSettings s, Snapshot saved) => s with
    {
        Fans = s.Fans.ToDictionary(f => f.Key, f => saved.Fans.TryGetValue(f.Key, out var old) ? f.Value with { Mode = old.Mode } : f.Value),
        HeaderFans = s.HeaderFans.ToDictionary(f => f.Key, f => saved.HeaderFans.TryGetValue(f.Key, out var old) ? f.Value with { Mode = old.Mode } : f.Value),
        Lighting = saved.Lighting,
        Lamp = s.Lamp with { On = saved.Lamp.On, Brightness = saved.Lamp.Brightness, Color = saved.Lamp.Color, Follow = saved.Lamp.Follow, Kelvin = saved.Lamp.Kelvin },
        Lamps = s.Lamps?.Select(l => saved.Lamps?.FirstOrDefault(o => o.Id == l.Id) is { } old
            ? l with { On = old.On, Brightness = old.Brightness, Color = old.Color, Follow = old.Follow, Kelvin = old.Kelvin } : l).ToArray()
    };

    private static IEnumerable<string> ListPrograms()
    {
        foreach (var process in Process.GetProcesses())
        {
            string? path = null;
            try { path = process.MainModule?.FileName; }
            catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException or NotSupportedException) { }
            finally { process.Dispose(); }
            if (path is not null) yield return path;
        }
    }
}
