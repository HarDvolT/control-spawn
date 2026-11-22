using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using ControlSpawn.Core;

namespace ControlSpawn.Hardware;

public interface IGpuSensorClient : IDisposable
{
    IReadOnlyList<SensorReading>? TryRead(TimeSpan timeout);
}

internal interface IGpuSensorProcess : IDisposable
{
    bool HasExited { get; }
    Task<string?> Ready { get; }
    Task<string?> ReadAsync();
}

public sealed class GpuSensorClient : IGpuSensorClient
{
    public const string HostArgument = "--gpu-sensor-host";
    private static readonly TimeSpan FirstDelay = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan MaxDelay = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan RecoveryPeriod = TimeSpan.FromSeconds(60);
    private static readonly JsonSerializerOptions ReplyJson = new()
    {
        RespectRequiredConstructorParameters = true,
        RespectNullableAnnotations = true
    };
    private readonly object gate = new();
    private readonly IClock clock;
    private readonly Func<IGpuSensorProcess> start;
    private IGpuSensorProcess? child;
    private TimeSpan retryAt;
    private TimeSpan retryDelay = FirstDelay;
    private TimeSpan? goodSince;
    private bool failed, disposed;

    public GpuSensorClient() : this(new SystemClock(), () => new GpuSensorProcess()) { }

    internal GpuSensorClient(IClock clock, Func<IGpuSensorProcess> start)
    {
        this.clock = clock;
        this.start = start;
    }

    public IReadOnlyList<SensorReading>? TryRead(TimeSpan timeout)
    {
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (clock.Elapsed < retryAt) return null;

            try
            {
                var process = child ??= start();
                if (process.HasExited) return RestartLater();
                // The readiness deadline spans polls so a slow launch cannot stall the fan heartbeat.
                if (!process.Ready.IsCompleted) return null;
                if (process.Ready.GetAwaiter().GetResult() != "ready") return RestartLater();

                var line = process.ReadAsync().WaitAsync(timeout).GetAwaiter().GetResult();
                if (line is null || process.HasExited) return RestartLater();
                var readings = ParseReply(line);
                if (readings is null) MarkFailed();
                else MarkGood();
                return readings;
            }
            catch (Exception ex) when (ex is TimeoutException or IOException or InvalidOperationException or Win32Exception)
            {
                return RestartLater();
            }
        }
    }

    internal static IReadOnlyList<SensorReading>? ParseReply(string line)
    {
        try
        {
            var readings = JsonSerializer.Deserialize<SensorReading[]>(line, ReplyJson);
            if (readings is null || Array.Exists(readings, reading => reading is null ||
                reading.Category != SensorCategory.Gpu || (reading.Value is double value && !double.IsFinite(value))))
                return null;
            return readings;
        }
        catch (JsonException) { return null; }
    }

    private IReadOnlyList<SensorReading>? RestartLater()
    {
        StopChild();
        retryAt = clock.Elapsed + retryDelay;
        retryDelay = TimeSpan.FromTicks(Math.Min(retryDelay.Ticks * 2, MaxDelay.Ticks));
        MarkFailed();
        return null;
    }

    private void MarkFailed()
    {
        goodSince = null;
        if (failed) return;
        failed = true;
        ControlSpawnLog.Warning("sensors", "GPU sensors stopped answering.");
    }

    private void MarkGood()
    {
        var now = clock.Elapsed;
        goodSince ??= now;
        if (now - goodSince >= RecoveryPeriod) retryDelay = FirstDelay;
        if (!failed) return;
        failed = false;
        ControlSpawnLog.Warning("sensors", "GPU sensors are answering again.");
    }

    private void StopChild()
    {
        var process = child;
        child = null;
        process?.Dispose();
    }

    public void Dispose()
    {
        lock (gate)
        {
            disposed = true;
            StopChild();
        }
    }

    private sealed class GpuSensorProcess : IGpuSensorProcess
    {
        private static readonly TimeSpan ReadyTimeout = TimeSpan.FromSeconds(5);
        private readonly Process process;
        private readonly StreamWriter input;
        private readonly StreamReader output;

        public GpuSensorProcess()
        {
            var executable = Environment.ProcessPath ?? throw new InvalidOperationException("Service executable path unavailable.");
            var info = new ProcessStartInfo(executable, $"{HostArgument} {Environment.ProcessId.ToString(CultureInfo.InvariantCulture)}")
            {
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true
            };
            process = Process.Start(info) ?? throw new IOException("Cannot start GPU sensor host.");
            input = process.StandardInput;
            output = process.StandardOutput;
            Ready = ReadReadyAsync();
        }

        public bool HasExited => process.HasExited;
        public Task<string?> Ready { get; }

        private async Task<string?> ReadReadyAsync()
        {
            using var deadline = new CancellationTokenSource(ReadyTimeout);
            try { return await output.ReadLineAsync(deadline.Token).AsTask().WaitAsync(deadline.Token).ConfigureAwait(false); }
            catch (Exception ex) when (ex is OperationCanceledException or IOException or InvalidOperationException) { return null; }
        }

        public async Task<string?> ReadAsync()
        {
            try
            {
                await input.WriteLineAsync("read").ConfigureAwait(false);
                await input.FlushAsync().ConfigureAwait(false);
                return await output.ReadLineAsync().ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or InvalidOperationException) { return null; }
        }

        public void Dispose()
        {
            try { process.Kill(entireProcessTree: true); }
            catch (Exception ex) when (ex is InvalidOperationException or Win32Exception or AggregateException) { }
            finally
            {
                // Close the pipe without flushing a write that may still be pending after a timeout.
                input.BaseStream.Dispose();
                output.Dispose();
                process.Dispose();
            }
        }
    }
}
