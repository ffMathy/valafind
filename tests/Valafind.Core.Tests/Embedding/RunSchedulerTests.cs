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
        const int Iterations = 3000;
        var scheduler = new EmbeddingGemmaModel.RunScheduler();
        var failure = (Exception?)null;
        var worker = new Thread(() =>
        {
            try
            {
                for (var i = 0; i < Iterations; i++)
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
        Assert.True(worker.Join(Hang), "scheduler deadlocked when a cancellation raced a wake-up");
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
