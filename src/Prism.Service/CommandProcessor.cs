using System.Text.Json;
using Prism.Core;
using Prism.Core.Ipc;
using Prism.Router;

namespace Prism.Service;

public interface IServiceRuntime
{
    StateMessage GetState(ServiceSettings settings);
    Task ApplyAsync(ServiceSettings settings, CancellationToken cancellationToken);
    Task<SpeedTestResult?> StartSpeedTestAsync(CancellationToken cancellationToken);
}

/// <summary>Serialized settings commands; connection-local hello/subscribe live in PipeSession.</summary>
public sealed class CommandProcessor
{
    private readonly ISettingsStore store;
    private readonly IServiceRuntime runtime;
    private readonly SemaphoreSlim gate = new(1, 1);
    private ServiceSettings settings;
    public ServiceSettings Settings => Volatile.Read(ref settings);
    public CommandProcessor(ISettingsStore store, IServiceRuntime runtime)
    { this.store = store; this.runtime = runtime; settings = store.Load(); Settings.Validate(); }
    public StateMessage GetState() => runtime.GetState(Settings);
    public async Task<Response> HandleAsync(Request request, CancellationToken cancellationToken = default)
    {
        // A speed test must not hold the settings gate while fans/scenes need to change.
        if (request.Command == Commands.StartSpeedTest) return await SpeedResponseAsync(request.Id, cancellationToken);
        await gate.WaitAsync(cancellationToken);
        try
        {
            if (request.Command == Commands.GetState) return new(request.Id, true, Data: JsonSerializer.SerializeToElement(GetState(), PipeContract.Json));
            var args = request.Args;
            if (args is null || args.Value.ValueKind != JsonValueKind.Object) throw new ArgumentException("Command args must be an object.");
            var a = args.Value;
            ServiceSettings next;
            switch (request.Command)
            {
                case Commands.SetFanMode:
                {
                    var group = Group(a); var mode = EnumValue<FanMode>(a, "mode");
                    var fans = new Dictionary<string, FanConfiguration>(Settings.Fans);
                    if (mode == FanMode.Custom && fans[group].Points is null) throw new ArgumentException("Set a custom curve first.");
                    fans[group] = fans[group] with { Mode = mode }; next = Settings with { Fans = fans }; break;
                }
                case Commands.SetFanCurve:
                {
                    var group = Group(a); var source = EnumValue<TemperatureSource>(a, "source");
                    var points = a.GetProperty("points").EnumerateArray().Select(p =>
                    {
                        if (p.ValueKind != JsonValueKind.Array || p.GetArrayLength() != 2) throw new ArgumentException("Points must be [temperature,percent].");
                        return new FanPoint(p[0].GetDouble(), p[1].GetDouble());
                    }).ToArray();
                    var fans = new Dictionary<string, FanConfiguration>(Settings.Fans) { [group] = new(FanMode.Custom, source, points) };
                    next = Settings with { Fans = fans }; break;
                }
                case Commands.SetLighting:
                    next = Settings with { Lighting = new(EnumValue<LightingEffect>(a, "effect"), Text(a, "color"), a.GetProperty("brightness").GetDouble(), a.GetProperty("speed").GetDouble()) }; break;
                case Commands.SetDeviceSync:
                    var sync = new Dictionary<string, bool>(Settings.SyncedDevices) { [Text(a, "device")] = a.GetProperty("enabled").GetBoolean() };
                    next = Settings with { SyncedDevices = sync }; break;
                case Commands.SetLamp:
                    next = Settings with { Lamp = Settings.Lamp with { On = a.GetProperty("on").GetBoolean(), Brightness = a.GetProperty("brightness").GetInt32(), Follow = Text(a, "follow"), Color = Text(a, "color"), Kelvin = null } }; break;
                case Commands.ApplyScene:
                    if (!Settings.Scenes.TryGetValue(Text(a, "name"), out var scene)) throw new ArgumentException("Unknown scene.");
                    next = Settings with { Fans = Settings.Fans.ToDictionary(f => f.Key, f => f.Value with { Mode = scene.Fans }),
                        Lighting = scene.Lighting, Lamp = scene.Lamp with { Host = Settings.Lamp.Host } }; break;
                default: throw new ArgumentException("Unknown command.");
            }
            next.Validate(); store.Save(next); Volatile.Write(ref settings, next);
            // Once persisted, a disconnect must not leave the running adapters on the old settings.
            await runtime.ApplyAsync(next, CancellationToken.None);
            return new(request.Id, true);
        }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException or KeyNotFoundException or JsonException or FormatException or OverflowException)
        { return new(request.Id, false, e.Message); }
        catch (IOException) { return new(request.Id, false, "Settings could not be saved."); }
        catch (UnauthorizedAccessException) { return new(request.Id, false, "Settings access denied."); }
        finally { gate.Release(); }
    }
    private async Task<Response> SpeedResponseAsync(int id, CancellationToken token)
    {
        try
        {
            var result = await runtime.StartSpeedTestAsync(token);
            return result is null ? new(id, false, "Speed test cancelled.") : new(id, true, Data: JsonSerializer.SerializeToElement(result, PipeContract.Json));
        }
        catch (Exception e) when (e is InvalidOperationException or SpeedTestError or IOException)
        { return new(id, false, e is InvalidOperationException ? e.Message : "Speed test failed."); }
    }
    private static string Text(JsonElement a, string key) => a.GetProperty(key).GetString() ?? throw new ArgumentException($"Missing {key}.");
    private static T EnumValue<T>(JsonElement a, string key) where T : struct, Enum =>
        Enum.TryParse<T>(Text(a, key), false, out var value) && Enum.IsDefined(value) && !int.TryParse(Text(a, key), out _)
            ? value : throw new ArgumentException($"Invalid {key}.");
    private static string Group(JsonElement a) => Text(a, "group") is "Radiator" or "Case fans" ? Text(a, "group") : throw new ArgumentException("Unknown or read-only fan group.");
}
