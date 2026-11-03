using System.Net.Sockets;
using Prism.Core;
using Prism.Core.Ipc;
using Prism.Hardware;
using Prism.Lighting;
using Prism.Router;

namespace Prism.Service;

public sealed partial class ServiceRuntime
{
    private sealed class CaptureModule(ServiceRuntime runtime, bool audio) : HostedModule(audio ? "audio" : "screen", ok => runtime.ModuleHealth(audio ? "audio" : "screen", ok))
    {
        protected override Task ExecuteAsync(CancellationToken token) => CaptureAsync(audio, token);
        private async Task CaptureAsync(bool audio, CancellationToken token)
        {
            if (runtime.mock || runtime.Observe || !OperatingSystem.IsWindows()) { await Task.Delay(Timeout.Infinite, token); return; }
            using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(500));
            do
            {
                // The case or any part with its own look (Music on the pump alone, say) needs the capture running.
                bool Needed() => Volatile.Read(ref runtime.settings) is var value && value.PartLooks.Values.Select(p => p.Effect).Append(value.Lighting.Effect).Any(effect => audio
                    ? effect is LightingEffect.Music or LightingEffect.AudioSpectrum
                    : effect is LightingEffect.Adaptive or LightingEffect.ScreenAmbient);
                if (!Needed() || (audio ? runtime.audioLevels is not WasapiLoopback : runtime.screenZones is not DesktopDuplication)) continue;
                using var captureStop = CancellationTokenSource.CreateLinkedTokenSource(token);
                var task = Task.Factory.StartNew(() => {
                    if (audio) ((WasapiLoopback)runtime.audioLevels!).Run(captureStop.Token);
                    else ((DesktopDuplication)runtime.screenZones!).Run(captureStop.Token);
                }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
                try
                {
                    while (Needed() && !task.IsCompleted && !token.IsCancellationRequested) await Task.Delay(250, token);
                }
                finally
                {
                    captureStop.Cancel();
                    try { await task; runtime.warnings.TryRemove(audio ? "audio" : "screen", out _); }
                    catch (Exception e) when (e is System.Runtime.InteropServices.COMException or NotSupportedException or InvalidOperationException)
                    { runtime.warnings[audio ? "audio" : "screen"] = "Capture unavailable in this session; using silent/black input. Retrying."; }
                }
            } while (await timer.WaitForNextTickAsync(token));
        }

    }
}
