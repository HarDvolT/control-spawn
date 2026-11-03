using System.Net.Sockets;
using Prism.Core;
using Prism.Core.Ipc;
using Prism.Hardware;
using Prism.Lighting;
using Prism.Router;

namespace Prism.Service;

public sealed partial class ServiceRuntime
{
    private sealed class LampModule(ServiceRuntime runtime) : HostedModule("lamps", ok => runtime.ModuleHealth("lamps", ok))
    {
        private sealed class Worker(string name, Func<CancellationToken, Task> run, Action<bool> health) : HostedModule(name, health)
        {
            protected override Task ExecuteAsync(CancellationToken token) => run(token);
        }
        protected override Task ExecuteAsync(CancellationToken token) => LampAsync(token);
        private async Task LampAsync(CancellationToken token)
        {
            var workers = new Dictionary<string, (CancellationTokenSource Stop, Task Task)>();
            using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(500));
            try
            {
                do
                {
                    var configs = Volatile.Read(ref runtime.settings).ConfiguredLamps;
                    foreach (var id in workers.Keys.Where(id => configs.All(c => c.Id != id)).ToArray())
                    {
                        var worker = workers[id]; worker.Stop.Cancel();
                        try { await worker.Task; } catch (OperationCanceledException) { }
                        worker.Stop.Dispose(); workers.Remove(id);
                        lock (runtime.stateGate) runtime.lampStates.Remove(id);
                        runtime.warnings.TryRemove(id, out _);
                    }
                    foreach (var config in configs.Where(c => !workers.ContainsKey(c.Id)))
                    {
                        var source = CancellationTokenSource.CreateLinkedTokenSource(token);
                        var initial = workers.Count == 0 ? runtime.lamp : null; runtime.lamp = null;
                        var worker = new Worker("lamps", token =>
                        {
                            var first = initial; initial = null;
                            return runtime.LampWorkerAsync(config.Id, first, token);
                        }, ok => runtime.ModuleHealth("lamp:" + config.Id, ok));
                        workers[config.Id] = (source, worker.RunAsync(source.Token));
                    }
                } while (await timer.WaitForNextTickAsync(token));
            }
            finally
            {
                foreach (var worker in workers.Values) worker.Stop.Cancel();
                foreach (var worker in workers.Values) { try { await worker.Task; } catch (OperationCanceledException) { } finally { worker.Stop.Dispose(); } }
            }
        }
    }
}
