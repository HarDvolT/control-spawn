using System.Text.Json;
using Prism.Core;
using Prism.Core.Ipc;

namespace Prism.App.Services;

public enum ServiceConnection { Disconnected, Connecting, Connected }

/// <summary>
/// The UI's only view of the service. Every page and widget is bound to this, never to hardware.
/// </summary>
public interface IPrismClient : IAsyncDisposable
{
    event EventHandler<StateMessage>? StateReceived;
    event EventHandler<ServiceConnection>? ConnectionChanged;

    ServiceConnection Connection { get; }
    bool IsConnected { get; }

    Task StartAsync(CancellationToken cancellationToken = default);
    Task<Response> SendAsync(string command, object? args = null, CancellationToken cancellationToken = default);

    Task<Response> SetFanModeAsync(string group, FanMode mode, CancellationToken cancellationToken = default);
    Task<Response> SetFanCurveAsync(string group, IReadOnlyList<FanPoint> points, TemperatureSource source, CancellationToken cancellationToken = default);
    Task<Response> SetLightingAsync(LightingEffect effect, string color, double brightness, double speed, CancellationToken cancellationToken = default);
    Task<Response> SetDeviceSyncAsync(string device, bool enabled, CancellationToken cancellationToken = default);
    Task<Response> SetLampAsync(bool on, int brightness, string follow, string? color = null, CancellationToken cancellationToken = default);
    Task<Response> ApplySceneAsync(string name, CancellationToken cancellationToken = default);
    Task<Response> StartSpeedTestAsync(CancellationToken cancellationToken = default);
    Task<StateMessage?> GetStateAsync(CancellationToken cancellationToken = default);
}

public abstract class PrismClientBase : IPrismClient
{
    private ServiceConnection _connection = ServiceConnection.Disconnected;

    public event EventHandler<StateMessage>? StateReceived;
    public event EventHandler<ServiceConnection>? ConnectionChanged;

    public ServiceConnection Connection => _connection;
    public bool IsConnected => _connection == ServiceConnection.Connected;

    protected void SetConnection(ServiceConnection connection)
    {
        if (_connection == connection) return;
        _connection = connection;
        ConnectionChanged?.Invoke(this, connection);
    }

    protected void Publish(StateMessage state) => StateReceived?.Invoke(this, state);

    public abstract Task StartAsync(CancellationToken cancellationToken = default);
    public abstract Task<Response> SendAsync(string command, object? args = null, CancellationToken cancellationToken = default);
    public abstract ValueTask DisposeAsync();

    public Task<Response> SetFanModeAsync(string group, FanMode mode, CancellationToken cancellationToken = default) =>
        SendAsync(Commands.SetFanMode, new { group, mode = mode.ToString() }, cancellationToken);

    public Task<Response> SetFanCurveAsync(string group, IReadOnlyList<FanPoint> points, TemperatureSource source,
        CancellationToken cancellationToken = default) =>
        SendAsync(Commands.SetFanCurve, new
        {
            group,
            points = points.Select(p => new[] { p.Temperature, p.Percent }).ToArray(),
            source = source.ToString()
        }, cancellationToken);

    public Task<Response> SetLightingAsync(LightingEffect effect, string color, double brightness, double speed,
        CancellationToken cancellationToken = default) =>
        SendAsync(Commands.SetLighting, new
        {
            effect = effect.ToString(),
            color,
            brightness = Math.Clamp(brightness, 0, 1),
            speed = Math.Clamp(speed, 0, 2)
        }, cancellationToken);

    public Task<Response> SetDeviceSyncAsync(string device, bool enabled, CancellationToken cancellationToken = default) =>
        SendAsync(Commands.SetDeviceSync, new { device, enabled }, cancellationToken);

    public Task<Response> SetLampAsync(bool on, int brightness, string follow, string? color = null,
        CancellationToken cancellationToken = default) =>
        SendAsync(Commands.SetLamp, new
        {
            on,
            brightness = Math.Clamp(brightness, 1, 100),
            follow,
            color
        }, cancellationToken);

    public Task<Response> ApplySceneAsync(string name, CancellationToken cancellationToken = default) =>
        SendAsync(Commands.ApplyScene, new { name }, cancellationToken);

    public Task<Response> StartSpeedTestAsync(CancellationToken cancellationToken = default) =>
        SendAsync(Commands.StartSpeedTest, null, cancellationToken);

    public async Task<StateMessage?> GetStateAsync(CancellationToken cancellationToken = default)
    {
        var response = await SendAsync(Commands.GetState, null, cancellationToken).ConfigureAwait(false);
        if (!response.Ok || response.Data is not { } data) return null;
        try { return data.Deserialize<StateMessage>(PipeContract.Json); }
        catch { return null; }
    }
}
