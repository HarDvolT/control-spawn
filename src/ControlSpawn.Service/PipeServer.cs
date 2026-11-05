using System.Collections.Concurrent;
using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using ControlSpawn.Core.Ipc;

namespace ControlSpawn.Service;

/// <summary>A connection over any duplex stream. A single writer prevents responses interleaving with pushes.</summary>
public sealed class PipeSession(CommandProcessor commands, bool verifiedApp = false, ClientCommandLimiter? limiter = null, string? clientKey = null, TimeSpan? pushInterval = null)
{
    public const int MaximumLineLength = 64 * 1024;
    public async Task RunAsync(Stream stream, CancellationToken cancellationToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        using var reader = new StreamReader(stream, new UTF8Encoding(false, true), false, 4096, true);
        using var writeGate = new SemaphoreSlim(1, 1);
        bool hello = false, subscribed = false;
        bool interactiveApp = false;
        limiter ??= new ClientCommandLimiter(); clientKey ??= Guid.NewGuid().ToString();
        async Task WriteAsync(object message)
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(linked.Token);
            deadline.CancelAfter(TimeSpan.FromSeconds(2));
            await writeGate.WaitAsync(deadline.Token);
            try
            {
                var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(message, message.GetType(), PipeContract.Json) + "\n");
                await stream.WriteAsync(bytes, deadline.Token);
                await stream.FlushAsync(deadline.Token);
            }
            finally { writeGate.Release(); }
        }
        var updates = Channel.CreateBounded<bool>(new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropOldest });
        void Changed() { if (Volatile.Read(ref subscribed)) updates.Writer.TryWrite(true); }
        commands.State.Changed += Changed;
        async Task TickAsync()
        {
            using var timer = new PeriodicTimer(pushInterval ?? TimeSpan.FromSeconds(1));
            while (await timer.WaitForNextTickAsync(linked.Token)) Changed();
        }
        async Task PushAsync()
        {
            try
            {
                // The full history (up to an hour of samples) goes once; later pushes carry only the new samples,
                // which keeps each update a few KB instead of half a megabyte.
                DateTimeOffset? sentUpTo = null;
                await foreach (var _ in updates.Reader.ReadAllAsync(linked.Token))
                {
                    if (!Volatile.Read(ref subscribed)) continue;
                    var state = commands.GetState();
                    var history = state.History ?? [];
                    var latest = history.Count > 0 ? history[^1].Timestamp : sentUpTo;
                    if (sentUpTo is { } since && (history.Count == 0 || history[0].Timestamp <= since))
                        state = state with { History = history.Where(s => s.Timestamp > since).ToArray(), HistoryIsDelta = true };
                    await WriteAsync(state);
                    sentUpTo = latest;
                }
            }
            finally { linked.Cancel(); }
        }
        var ticks = TickAsync();
        var push = PushAsync();
        var pending = new List<Task>();
        async Task DispatchAsync(Request request)
        {
            try { await WriteAsync(await commands.HandleAsync(request, linked.Token)); }
            catch { linked.Cancel(); throw; }
        }
        try
        {
            while (!linked.IsCancellationRequested)
            {
                pending.RemoveAll(t => t.IsCompletedSuccessfully);
                foreach (var failed in pending.Where(t => t.IsFaulted || t.IsCanceled)) await failed;
                // Bound work per connection while allowing a long speed test beside fan/scene commands.
                if (pending.Count >= 16) { await Task.WhenAny(pending); continue; }
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(linked.Token);
                if (!hello) deadline.CancelAfter(TimeSpan.FromSeconds(5));
                var line = await ReadLineAsync(reader, deadline.Token);
                if (line is null) break;
                Request? request;
                try { request = JsonSerializer.Deserialize<Request>(line, PipeContract.Json); }
                catch (JsonException) { await WriteAsync(new Response(0, false, "Invalid request JSON.")); continue; }
                if (request is null || string.IsNullOrEmpty(request.Command))
                { await WriteAsync(new Response(request?.Id ?? 0, false, "Missing command.")); continue; }
                if (!limiter.Allow(clientKey)) { await WriteAsync(new Response(request.Id, false, "Command rate limit exceeded.")); continue; }
                if (request.Command == Commands.Hello)
                {
                    var a = request.Args;
                    bool valid = a is { ValueKind: JsonValueKind.Object } && a.Value.TryGetProperty("version", out var version) &&
                        version.ValueKind == JsonValueKind.Number && version.TryGetInt32(out var number) && number == PipeContract.ProtocolVersion &&
                        a.Value.TryGetProperty("client", out var client) && client.ValueKind == JsonValueKind.String && client.GetString() is "app" or "widget";
                    await WriteAsync(new Response(request.Id, valid, valid ? null : "Protocol version or client mismatch.",
                        valid ? JsonSerializer.SerializeToElement(new { version = PipeContract.ProtocolVersion }, PipeContract.Json) : null));
                    if (!valid) break;
                    interactiveApp = verifiedApp && a!.Value.GetProperty("client").GetString() == "app";
                    hello = true;
                }
                else if (!hello) await WriteAsync(new Response(request.Id, false, "Send hello first."));
                else if (!verifiedApp && request.Command is not (Commands.GetState or Commands.Subscribe))
                    await WriteAsync(new Response(request.Id, false, PipeErrors.ReadOnly));
                else if (!interactiveApp && request.Command is Commands.PrepareApplianceStart or Commands.ConfirmApplianceStart)
                    await WriteAsync(new Response(request.Id, false, "Cooking start is only available in the app."));
                else if (request.Command == Commands.Subscribe)
                {
                    await WriteAsync(new Response(request.Id, true)); Volatile.Write(ref subscribed, true); Changed();
                }
                else pending.Add(DispatchAsync(request));
            }
        }
        finally
        {
            commands.State.Changed -= Changed;
            linked.Cancel();
            try { await Task.WhenAll(pending.Append(push).Append(ticks)); } catch (OperationCanceledException) when (linked.IsCancellationRequested) { }
        }
    }
    public static async Task<string?> ReadLineAsync(TextReader reader, CancellationToken token)
    {
        var line = new StringBuilder(); var single = new char[1];
        while (true)
        {
            if (await reader.ReadAsync(single.AsMemory(), token) == 0)
                return line.Length == 0 ? null : throw new InvalidDataException("Truncated JSON line.");
            if (single[0] == '\n') return line.ToString().TrimEnd('\r');
            if (line.Length >= MaximumLineLength) throw new InvalidDataException("Request exceeds line limit.");
            line.Append(single[0]);
        }
    }
}

public sealed class PipeServer(CommandProcessor commands, IPipeClientVerifier? verifier = null)
{
    public static PipeSecurity CreateSecurity()
    {
        var acl = new PipeSecurity();
        acl.SetAccessRuleProtection(true, false);
        acl.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.NetworkSid, null), PipeAccessRights.FullControl, AccessControlType.Deny));
        foreach (var sid in new[] { WellKnownSidType.InteractiveSid, WellKnownSidType.LocalSystemSid, WellKnownSidType.BuiltinAdministratorsSid })
            acl.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(sid, null),
                sid == WellKnownSidType.InteractiveSid ? PipeAccessRights.ReadWrite : PipeAccessRights.FullControl, AccessControlType.Allow));
        // The creating account needs CreateNewInstance for every pipe instance after the first. A non-elevated
        // console run (--mock) has the Administrators group filtered out, so grant the process user explicitly.
        if (System.Security.Principal.WindowsIdentity.GetCurrent().User is { } self)
            acl.AddAccessRule(new PipeAccessRule(self, PipeAccessRights.FullControl, AccessControlType.Allow));
        return acl;
    }
    public async Task RunAsync(CancellationToken token)
    {
        verifier ??= new PipeClientVerifier();
        var limiter = new ClientCommandLimiter(); var connections = new ConnectionLimiter();
        using var clientsStop = CancellationTokenSource.CreateLinkedTokenSource(token);
        var clients = new ConcurrentDictionary<int, Task>(); int id = 0;
        async Task ServeAsync(NamedPipeServerStream pipe)
        {
            await using (pipe)
            {
                try
                {
                    var client = verifier.Verify(pipe);
                    if (!connections.TryOpen(client.Key)) return;   // closing the pipe is the answer: too many from that process
                    try { await new PipeSession(commands, client.IsApp, limiter, client.Key).RunAsync(pipe, clientsStop.Token); }
                    finally { connections.Close(client.Key); }
                }
                catch (Exception e) when (e is IOException or OperationCanceledException or JsonException or InvalidOperationException or DecoderFallbackException) { }
            }
        }
        try
        {
            while (!token.IsCancellationRequested)
            {
                var pipe = NamedPipeServerStreamAcl.Create(PipeContract.PipeName, PipeDirection.InOut,
                    NamedPipeServerStream.MaxAllowedServerInstances, PipeTransmissionMode.Byte, PipeOptions.Asynchronous,
                    16384, 16384, CreateSecurity());
                try { await pipe.WaitForConnectionAsync(token); }
                catch { await pipe.DisposeAsync(); throw; }
                int key = ++id;
                var task = ServeAsync(pipe); clients[key] = task;
                _ = task.ContinueWith(_ => { clients.TryRemove(key, out var ignored); }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            }
        }
        finally { clientsStop.Cancel(); await Task.WhenAll(clients.Values); }
    }
}
