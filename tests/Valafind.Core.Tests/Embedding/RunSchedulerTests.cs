#pragma warning disable CA1707 // xunit test names use underscores for readability

using Microsoft.ML.OnnxRuntime;
using Valafind.Core.Embedding;
using Xunit;

namespace Valafind.Core.Tests.Embedding;

/// <summary>Model-free tests of the query-priority scheduler inside <see cref="EmbeddingGemmaModel"/>.</summary>
public sealed class RunSchedulerTests
{
    private static readonly TimeSpan Hang = TimeSpan.FromSeconds(20);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Cancelling_while_a_waiter_wakes_up_never_deadlocks(bool interactiveWaiter)
    {
        // A waiter is woken by Exit at the same moment another thread cancels its token. The cancel callback takes
        // the scheduler's lock; the waiter must not block on that callback while holding the lock.
        // Each round fails on its own after Hang if it deadlocks. The number of rounds is bounded by time rather than
        // fixed: on a saturated CI runner (the ONNX tests run alongside) a round can take 20+ ms, and 3000 of them
        // overran the overall timeout without any deadlock.
        const int MaxIterations = 3000, MinIterations = 200;
        var budget = TimeSpan.FromSeconds(5);
        var scheduler = new EmbeddingGemmaModel.RunScheduler(maxBackgroundRuns: 1); // a second document run must wait
        var failure = (Exception?)null;
        var worker = new Thread(() =>
        {
            try
            {
                var clock = System.Diagnostics.Stopwatch.StartNew();
                for (var i = 0; i < MaxIterations && (i < MinIterations || clock.Elapsed < budget); i++)
                    OneRound(scheduler, interactiveWaiter, i);
            }
#pragma warning disable CA1031 // reported to the test thread below
            catch (Exception ex)
#pragma warning restore CA1031
            {
                failure = ex;
            }
        }) { IsBackground = true }; // a deadlocked round must not keep the test process alive
        worker.Start();
        // A deadlocked round throws TimeoutException after Hang; this outer bound only catches a hung test thread.
        Assert.True(worker.Join(TimeSpan.FromMinutes(3)), "scheduler deadlocked when a cancellation raced a wake-up");
        Assert.Null(failure);

        // Still usable afterwards.
        scheduler.EnterInteractive(CancellationToken.None);
        scheduler.Exit(null);
    }

    private static void OneRound(EmbeddingGemmaModel.RunScheduler scheduler, bool interactiveWaiter, int round)
    {
        using var holderOptions = new RunOptions();
        using var waiterOptions = new RunOptions();
        var holder = new EmbeddingGemmaModel.ActiveRun(holderOptions);
        var waiterRun = new EmbeddingGemmaModel.ActiveRun(waiterOptions);
        using var cts = new CancellationTokenSource();
        scheduler.EnterBackground(holder, CancellationToken.None); // the session is busy

        using var waiting = new ManualResetEventSlim();
        var waiter = new Thread(() =>
        {
            waiting.Set();
            try
            {
                if (interactiveWaiter)
                    scheduler.EnterInteractive(cts.Token);
                else
                    scheduler.EnterBackground(waiterRun, cts.Token);
                scheduler.Exit(waiterRun);
            }
            catch (OperationCanceledException)
            {
            }
        }) { IsBackground = true };
        waiter.Start();
        waiting.Wait();
        if (round % 2 == 0)
            Thread.Yield(); // vary the interleaving: sometimes the waiter is already in Monitor.Wait, sometimes not

        using var go = new Barrier(2);
        var canceller = new Thread(() =>
        {
            go.SignalAndWait();
            cts.Cancel();
        }) { IsBackground = true };
        canceller.Start();
        go.SignalAndWait();
        scheduler.Exit(holder); // wakes the waiter while the cancel callback runs

        if (!waiter.Join(Hang) || !canceller.Join(Hang))
            throw new TimeoutException($"deadlock in round {round}");
    }

    [Fact]
    public async Task Interactive_entry_preempts_the_running_background_run()
    {
        var scheduler = new EmbeddingGemmaModel.RunScheduler();
        using var options = new RunOptions();
        var background = new EmbeddingGemmaModel.ActiveRun(options);
        scheduler.EnterBackground(background, CancellationToken.None);
        Assert.True(scheduler.IsBackgroundRunActive);

        var entered = Task.Run(() => scheduler.EnterInteractive(CancellationToken.None), TestContext.Current.CancellationToken);
        var deadline = DateTime.UtcNow.Add(Hang);
        // EnterInteractive sets Preempted and then RunOptions.Terminate on another thread: wait for BOTH, or this
        // thread can observe the first and read the second in between (seen on a busy CI runner).
        while (!(background.Preempted && options.Terminate) && DateTime.UtcNow < deadline)
            Thread.Sleep(1);
        Assert.True(background.Preempted);
        Assert.True(options.Terminate);
        Assert.False(entered.IsCompleted, "interactive entry must wait until the terminated run leaves");

        scheduler.Exit(background);
        await entered.WaitAsync(Hang, TestContext.Current.CancellationToken);
        Assert.False(scheduler.IsBackgroundRunActive);
        scheduler.Exit(null);
    }
}

public sealed class ConcurrentRunSchedulerTests
{
    private static readonly TimeSpan Hang = TimeSpan.FromSeconds(20);

    [Fact]
    public async Task Document_runs_overlap_and_a_query_preempts_all_of_them()
    {
        var scheduler = new EmbeddingGemmaModel.RunScheduler();
        using var a = new RunOptions();
        using var b = new RunOptions();
        var first = new EmbeddingGemmaModel.ActiveRun(a);
        var second = new EmbeddingGemmaModel.ActiveRun(b);
        scheduler.EnterBackground(first, CancellationToken.None);
        scheduler.EnterBackground(second, CancellationToken.None); // does not wait for the first
        Assert.Equal(2, scheduler.BackgroundRuns);

        var query = Task.Run(() => scheduler.EnterInteractive(CancellationToken.None), TestContext.Current.CancellationToken);
        var deadline = DateTime.UtcNow.Add(Hang);
        while (!(first.Preempted && a.Terminate && second.Preempted && b.Terminate) && DateTime.UtcNow < deadline)
            Thread.Sleep(1);
        Assert.True(a.Terminate && b.Terminate, "both document runs must be terminated");

        // A new document run waits behind the pending query.
        using var c = new RunOptions();
        var third = Task.Run(() => scheduler.EnterBackground(new EmbeddingGemmaModel.ActiveRun(c), CancellationToken.None), TestContext.Current.CancellationToken);
        scheduler.Exit(first);
        await Task.Delay(50, TestContext.Current.CancellationToken);
        Assert.False(query.IsCompleted, "the query waits until every terminated run has left");
        scheduler.Exit(second);
        await query.WaitAsync(Hang, TestContext.Current.CancellationToken);
        Assert.False(third.IsCompleted, "document runs wait while the query runs");
        scheduler.Exit(null);
        await third.WaitAsync(Hang, TestContext.Current.CancellationToken);
        Assert.Equal(1, scheduler.BackgroundRuns);
    }

    [Fact]
    public void Respects_the_cap_on_overlapping_document_runs()
    {
        var scheduler = new EmbeddingGemmaModel.RunScheduler(maxBackgroundRuns: 2);
        using var a = new RunOptions();
        using var b = new RunOptions();
        using var c = new RunOptions();
        var first = new EmbeddingGemmaModel.ActiveRun(a);
        scheduler.EnterBackground(first, CancellationToken.None);
        scheduler.EnterBackground(new EmbeddingGemmaModel.ActiveRun(b), CancellationToken.None);
        var third = Task.Run(() => scheduler.EnterBackground(new EmbeddingGemmaModel.ActiveRun(c), CancellationToken.None), TestContext.Current.CancellationToken);
        Assert.False(third.Wait(100, TestContext.Current.CancellationToken));
        scheduler.Exit(first);
        Assert.True(third.Wait(Hang, TestContext.Current.CancellationToken));
    }
}
