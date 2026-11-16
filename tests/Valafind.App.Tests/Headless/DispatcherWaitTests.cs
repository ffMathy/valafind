using Avalonia.Threading;
using Valafind.App.Services;
using Xunit;

namespace Valafind.App.Tests.Headless;

/// <summary>The OS-shutdown wait must let UI-thread continuations run (regression: shutdown-requested-deadlock).</summary>
[Collection(HeadlessUiDefinition.Name)]
public sealed class DispatcherWaitTests(HeadlessFixture fixture)
{
    [Fact]
    public Task TeardownThatResumesOnTheUiThreadCompletes() =>
        fixture.Session.Dispatch(() =>
        {
            // Same shape as AppHost.StopCore: CancelAsync on a token with a linked registration completes
            // asynchronously, and the continuation (ConfigureAwait(true)) needs the UI thread. Task.Wait deadlocked
            // here until its 5 s timeout.
            using var cts = new CancellationTokenSource();
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cts.Token);
            var reachedEnd = false;

            async Task TeardownAsync()
            {
                await cts.CancelAsync().ConfigureAwait(true);
                Dispatcher.UIThread.VerifyAccess();
                await Task.Delay(20).ConfigureAwait(true);
                reachedEnd = true;
            }

            var started = Environment.TickCount64;
            var completed = DispatcherWait.WaitPumping(TeardownAsync(), TimeSpan.FromSeconds(5));
            Assert.True(completed);
            Assert.True(reachedEnd);
            Assert.True(Environment.TickCount64 - started < 4000);
            Assert.True(linked.IsCancellationRequested);
        }, TestContext.Current.CancellationToken);

    [Fact]
    public Task GivesUpAfterTheTimeout() =>
        fixture.Session.Dispatch(() =>
        {
            var never = new TaskCompletionSource();
            var started = Environment.TickCount64;
            Assert.False(DispatcherWait.WaitPumping(never.Task, TimeSpan.FromMilliseconds(100)));
            Assert.True(Environment.TickCount64 - started >= 90);
        }, TestContext.Current.CancellationToken);
}
