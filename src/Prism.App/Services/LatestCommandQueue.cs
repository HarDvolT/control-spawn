using Prism.Core.Ipc;

namespace Prism.App.Services;

/// <summary>One serial lane per control. A trailing batch is never discarded, including on release.</summary>
public sealed class LatestCommandQueue(Func<string, object?, CancellationToken, Task<Response>> send)
{
    private readonly object gate = new();
    private readonly Dictionary<string, Lane> lanes = new();
    private sealed class Lane
    {
        public string Command = "";
        public object? Args;
        public List<TaskCompletionSource<Response>> Waiting = [];
    }

    public Task<Response> SendAsync(string key, string command, object? args, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        var completion = new TaskCompletionSource<Response>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (gate)
        {
            bool start = !lanes.TryGetValue(key, out var lane);
            if (start) lanes[key] = lane = new();
            lane!.Command = command; lane.Args = args; lane.Waiting.Add(completion);
            if (start) _ = RunAsync(key, lane);
        }
        // Cancelling a waiter must not cancel the final value for other callers in this lane.
        return completion.Task.WaitAsync(token);
    }

    private async Task RunAsync(string key, Lane lane)
    {
        while (true)
        {
            string command; object? args; List<TaskCompletionSource<Response>> waiting;
            lock (gate)
            {
                command = lane.Command; args = lane.Args; waiting = lane.Waiting; lane.Waiting = [];
            }
            Response response;
            try { response = await send(command, args, CancellationToken.None).ConfigureAwait(false); }
            catch (Exception e) { response = new(0, false, e.Message); }
            foreach (var completion in waiting) completion.TrySetResult(response);
            await Task.Delay(30).ConfigureAwait(false);
            lock (gate)
            {
                if (lane.Waiting.Count != 0) continue;
                lanes.Remove(key); return;
            }
        }
    }
}
