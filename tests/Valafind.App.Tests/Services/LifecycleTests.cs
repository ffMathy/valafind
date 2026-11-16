using Valafind.App.Services;
using Xunit;

namespace Valafind.App.Tests.Services;

public sealed class SerialTaskQueueTests
{
    [Fact]
    public async Task ASecondStepWaitsForTheFirstToFinish()
    {
        // Regression (overlapping-stopcore): Quit during a settings-triggered restart returned while the old Core was
        // still being disposed, and two restarts could leave two Cores on the same index.
        var queue = new SerialTaskQueue();
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var log = new List<string>();

        var first = queue.Enqueue(async () =>
        {
            log.Add("stop-1 begin");
            await gate.Task;
            log.Add("stop-1 end");
        });
        var second = queue.Enqueue(() =>
        {
            log.Add("stop-2");
            return Task.CompletedTask;
        });

        await Task.Delay(50, TestContext.Current.CancellationToken);
        Assert.False(second.IsCompleted);
        Assert.Equal(["stop-1 begin"], log);

        gate.SetResult();
        await second.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Assert.True(first.IsCompletedSuccessfully);
        Assert.Equal(["stop-1 begin", "stop-1 end", "stop-2"], log);
        await queue.Idle.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task AFailedStepDoesNotBlockLaterOnes()
    {
        var queue = new SerialTaskQueue();
        var failed = queue.Enqueue(() => Task.FromException(new InvalidOperationException("dispose failed")));
        var ran = false;
        await queue.Enqueue(() =>
        {
            ran = true;
            return Task.CompletedTask;
        }).WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        Assert.True(ran);
        await Assert.ThrowsAsync<InvalidOperationException>(() => failed);
    }

    [Fact]
    public async Task StepsEnqueuedFromInsideAStepRunAfterIt()
    {
        var queue = new SerialTaskQueue();
        var log = new List<string>();
        Task? inner = null;
        await queue.Enqueue(() =>
        {
            inner = queue.Enqueue(() =>
            {
                log.Add("inner");
                return Task.CompletedTask;
            });
            log.Add("outer");
            return Task.CompletedTask;
        });
        await inner!.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Assert.Equal(["outer", "inner"], log);
    }
}

public sealed class ModelInstallWatcherTests
{
    [Fact]
    public void MissingModelTriggersOnceItAppears()
    {
        // Regression (model-install-never-detected): after "model missing" nothing ever looked again.
        string? signature = null;
        var changes = 0;
        using var watcher = new ModelInstallWatcher(() => signature, () => changes++, TimeSpan.FromHours(1));
        watcher.Start(baseline: null);

        Assert.False(watcher.Poll());
        signature = "/m|onnx/model_quantized.onnx|1@2";
        Assert.True(watcher.Poll());
        Assert.Equal(1, changes);
        Assert.False(watcher.IsActive);
        Assert.False(watcher.Poll()); // stopped itself: at most one reload per start
        Assert.Equal(1, changes);
    }

    [Fact]
    public void FailedModelOnlyRetriesWhenTheFilesChange()
    {
        var signature = "/m|a|1@1";
        var changes = 0;
        using var watcher = new ModelInstallWatcher(() => signature, () => changes++, TimeSpan.FromHours(1));
        watcher.Start(baseline: signature);

        Assert.False(watcher.Poll()); // same broken files: no reload loop
        signature = "/m|a|2@5";       // fetch script re-run
        Assert.True(watcher.Poll());
        Assert.Equal(1, changes);
    }

    [Fact]
    public void StoppedWatcherIgnoresPolls()
    {
        var changes = 0;
        using var watcher = new ModelInstallWatcher(() => "x", () => changes++, TimeSpan.FromHours(1));
        Assert.False(watcher.Poll());
        watcher.Start(null);
        watcher.Stop();
        Assert.False(watcher.Poll());
        Assert.Equal(0, changes);
    }

    [Fact]
    public async Task PollsOnItsOwnTimer()
    {
        var changed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var watcher = new ModelInstallWatcher(() => "found", () => changed.TrySetResult(), TimeSpan.FromMilliseconds(20));
        watcher.Start(null);
        await changed.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
    }

    [Fact]
    public void ProbeErrorsCountAsNotInstalled()
    {
        using var watcher = new ModelInstallWatcher(() => throw new UnauthorizedAccessException(), () => { }, TimeSpan.FromHours(1));
        watcher.Start(null);
        Assert.False(watcher.Poll());
        Assert.True(watcher.IsActive);
    }
}
