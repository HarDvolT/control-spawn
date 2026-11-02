using System.Collections.Concurrent;
using System.IO;
using System.IO.Pipes;
using System.Text;
using Prism.Core.Ipc;

namespace Prism.App.Services;

/// <summary>
/// Named-pipe client for Prism.Service. Reads JSON lines, correlates responses by id, pushes state
/// snapshots to subscribers, and reconnects with exponential back-off when the service is absent.
/// </summary>
public sealed class PipePrismClient : PrismClientBase
{
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly ConcurrentDictionary<int, TaskCompletionSource<Response>> _pending = new();
    private readonly TimeSpan _requestTimeout;
    private CancellationTokenSource? _lifetime;
    private Task? _loop;
    private NamedPipeClientStream? _pipe;
    private StreamWriter? _writer;
    private int _nextId;

    public PipePrismClient(TimeSpan? requestTimeout = null)
    {
        _requestTimeout = requestTimeout ?? TimeSpan.FromSeconds(5);
    }

    public override Task StartAsync(CancellationToken cancellationToken = default)
    {
        if (_loop is not null) return Task.CompletedTask;
        _lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _loop = Task.Run(() => RunAsync(_lifetime.Token));
        return Task.CompletedTask;
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        var delay = TimeSpan.FromMilliseconds(500);
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                SetConnection(ServiceConnection.Connecting);
                _pipe = new NamedPipeClientStream(".", PipeContract.PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
                await _pipe.ConnectAsync(2000, cancellationToken).ConfigureAwait(false);
                _writer = new StreamWriter(_pipe, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false))
                {
                    AutoFlush = true,
                    NewLine = "\n"
                };
                using var reader = new StreamReader(_pipe, Encoding.UTF8);
                SetConnection(ServiceConnection.Connected);
                delay = TimeSpan.FromMilliseconds(500);

                await WriteRawAsync(JsonLineProtocol.CreateRequest(
                    Interlocked.Increment(ref _nextId), Commands.Hello,
                    new { version = PipeContract.ProtocolVersion, client = "app" }), cancellationToken).ConfigureAwait(false);
                await WriteRawAsync(JsonLineProtocol.CreateRequest(
                    Interlocked.Increment(ref _nextId), Commands.Subscribe, null), cancellationToken).ConfigureAwait(false);

                while (!cancellationToken.IsCancellationRequested)
                {
                    var line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
                    if (line is null) break;
                    Dispatch(line);
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch
            {
                // Service stopped, pipe busy, protocol hiccup: fall through to back-off and retry.
            }
            finally
            {
                SetConnection(ServiceConnection.Disconnected);
                FailPending("Service not running");
                Cleanup();
            }

            try { await Task.Delay(delay, cancellationToken).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
            delay = TimeSpan.FromMilliseconds(Math.Min(delay.TotalMilliseconds * 2, 10000));
        }
        SetConnection(ServiceConnection.Disconnected);
    }

    private readonly List<TelemetrySample> _history = new();
    /// <summary>Rebuilds the full history from the first full push plus the new samples in each later push.</summary>
    private StateMessage MergeHistory(StateMessage state)
    {
        if (state.History is null) return state;
        if (!state.HistoryIsDelta) _history.Clear();
        foreach (var sample in state.History)
            if (_history.Count == 0 || sample.Timestamp > _history[^1].Timestamp) _history.Add(sample);
        if (_history.Count > 3600) _history.RemoveRange(0, _history.Count - 3600);
        return state with { History = _history.ToArray(), HistoryIsDelta = false };
    }

    private void Dispatch(string line)
    {
        if (!JsonLineProtocol.TryParse(line, out var state, out var response)) return;
        if (state is not null)
        {
            Publish(MergeHistory(state));
            return;
        }
        if (response is not null && _pending.TryRemove(response.Id, out var pending))
            pending.TrySetResult(response);
    }

    private async Task WriteRawAsync(Request request, CancellationToken cancellationToken)
    {
        var writer = _writer ?? throw new IOException("Service not running");
        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await writer.WriteLineAsync(JsonLineProtocol.Serialize(request).AsMemory(), cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public override async Task<Response> SendAsync(string command, object? args = null, CancellationToken cancellationToken = default)
    {
        if (!IsConnected || _writer is null)
            return new Response(0, false, "Service not running");

        var id = Interlocked.Increment(ref _nextId);
        var completion = new TaskCompletionSource<Response>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = completion;
        try
        {
            await WriteRawAsync(new Request(id, command, JsonLineProtocol.ToArgs(args)), cancellationToken).ConfigureAwait(false);
            var timeout = Task.Delay(_requestTimeout, cancellationToken);
            var finished = await Task.WhenAny(completion.Task, timeout).ConfigureAwait(false);
            if (finished == completion.Task) return await completion.Task.ConfigureAwait(false);
            _pending.TryRemove(id, out _);
            return new Response(id, false, "The service did not answer in time");
        }
        catch (OperationCanceledException)
        {
            _pending.TryRemove(id, out _);
            throw;
        }
        catch (Exception ex)
        {
            _pending.TryRemove(id, out _);
            return new Response(id, false, ex.Message);
        }
    }

    private void FailPending(string error)
    {
        foreach (var key in _pending.Keys)
            if (_pending.TryRemove(key, out var pending))
                pending.TrySetResult(new Response(key, false, error));
    }

    private void Cleanup()
    {
        try { _writer?.Dispose(); } catch { }
        try { _pipe?.Dispose(); } catch { }
        _writer = null;
        _pipe = null;
    }

    public override async ValueTask DisposeAsync()
    {
        try { _lifetime?.Cancel(); } catch { }
        if (_loop is not null)
        {
            try { await _loop.ConfigureAwait(false); } catch { }
        }
        Cleanup();
        _lifetime?.Dispose();
        _writeLock.Dispose();
        SetConnection(ServiceConnection.Disconnected);
    }
}
