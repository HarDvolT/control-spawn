using ControlSpawn.Service;
using Xunit;

namespace ControlSpawn.Service.Tests;

public sealed class ShutdownWatchdogTests
{
    [Fact]
    public async Task DeadlineRestoresFansThenExitsWithFailure()
    {
        var waiting = new TaskCompletionSource<TimeSpan>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var deadline = new ManualResetEventSlim();
        var calls = new List<string>();
        using var watchdog = new ShutdownWatchdog((_, timeout) =>
        {
            waiting.SetResult(timeout);
            return !deadline.Wait(TimeSpan.FromSeconds(5));
        }, code => calls.Add($"exit {code}"));
        var thread = watchdog.Start(() => calls.Add("restore"));
        Assert.NotNull(thread);

        Assert.Equal(TimeSpan.FromSeconds(20), await waiting.Task.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.True(thread.IsBackground);
        Assert.Empty(calls);
        deadline.Set();

        Assert.True(thread.Join(TimeSpan.FromSeconds(5)));
        Assert.Equal(["restore", "exit 1"], calls);
    }

    [Fact]
    public async Task CompletedStopSkipsTheEmergencyRestoreAndExit()
    {
        var waiting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = new List<string>();
        using var watchdog = new ShutdownWatchdog((completion, _) =>
        {
            waiting.SetResult();
            return completion.Wait(TimeSpan.FromSeconds(5));
        }, code => calls.Add($"exit {code}"));
        var thread = watchdog.Start(() => calls.Add("restore"));
        Assert.NotNull(thread);

        await waiting.Task.WaitAsync(TimeSpan.FromSeconds(5));
        watchdog.Dispose();

        Assert.True(thread.Join(TimeSpan.FromSeconds(5)));
        Assert.Empty(calls);
    }
}
