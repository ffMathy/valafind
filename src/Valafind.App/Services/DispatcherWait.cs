using Avalonia.Threading;

namespace Valafind.App.Services;

/// <summary>Waiting on the UI thread without deadlocking continuations that need the UI thread.</summary>
internal static class DispatcherWait
{
    /// <summary>
    /// Blocks the caller (which must be the UI thread) until <paramref name="task"/> completes or
    /// <paramref name="timeout"/> elapses, while keeping the dispatcher pumping in a nested frame. Unlike
    /// <see cref="Task.Wait(TimeSpan)"/>, awaits inside <paramref name="task"/> that resume on the UI thread
    /// (<c>ConfigureAwait(true)</c>) can still run. Returns true when the task completed.
    /// </summary>
    public static bool WaitPumping(Task task, TimeSpan timeout)
    {
        ArgumentNullException.ThrowIfNull(task);
        if (task.IsCompleted)
            return true;
        Dispatcher.UIThread.VerifyAccess();

        var frame = new DispatcherFrame();
        // Posting (rather than setting Continue from the pool thread) also wakes the dispatcher up.
        _ = task.ContinueWith(_ => Dispatcher.UIThread.Post(() => frame.Continue = false, DispatcherPriority.Send),
            CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        var timer = DispatcherTimer.RunOnce(() => frame.Continue = false, timeout, DispatcherPriority.Send);
        try
        {
            Dispatcher.UIThread.PushFrame(frame);
        }
        finally
        {
            timer.Dispose();
        }
        return task.IsCompleted;
    }
}
