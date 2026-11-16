namespace Valafind.App.Services;

/// <summary>
/// Runs asynchronous steps strictly one after another: a step starts only when every earlier step has finished
/// (successfully or not). Used for the Core lifecycle so a stop, restart or shutdown never overlaps another one.
/// </summary>
internal sealed class SerialTaskQueue
{
    private readonly object _gate = new();
    private Task _tail = Task.CompletedTask;

    /// <summary>Completes when every step enqueued so far has finished.</summary>
    public Task Idle
    {
        get
        {
            lock (_gate)
                return _tail;
        }
    }

    /// <summary>Queues <paramref name="step"/>; the returned task completes (or faults) with it.</summary>
    /// <remarks>A failure of an earlier step does not prevent later steps; it is observed only by that step's caller.
    /// Steps resume on the caller's synchronization context (the UI thread for the app host).</remarks>
    public Task Enqueue(Func<Task> step)
    {
        ArgumentNullException.ThrowIfNull(step);
        Task previous;
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_gate)
        {
            previous = _tail;
            _tail = done.Task;
        }
        return RunAsync(previous, step, done);
    }

    private static async Task RunAsync(Task previous, Func<Task> step, TaskCompletionSource done)
    {
        try
        {
            // `previous` is always a completion signal that never faults.
            await previous.ConfigureAwait(true);
            await step().ConfigureAwait(true);
        }
        finally
        {
            done.SetResult();
        }
    }
}
