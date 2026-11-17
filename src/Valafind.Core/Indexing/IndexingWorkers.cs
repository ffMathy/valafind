using Valafind.Core.Platform;

namespace Valafind.Core.Indexing;

/// <summary>
/// Runs indexing work items on up to <see cref="IndexingPace.MaxConcurrency"/> low-priority threads, never more at once
/// than <see cref="IndexingPace.Concurrency"/> allows at that moment. <see cref="Run"/> blocks the caller (the
/// coordinator's dispatcher) while every allowed slot is busy, so the dispatcher never runs ahead of the workers, and
/// a lower pace takes effect as running items finish. Threads are started on demand and kept until disposal.
/// </summary>
/// <remarks>Work items should not throw; one that does fails the next <see cref="Run"/> or <see cref="WaitIdle"/>
/// call with its exception (cancellation included), so the coordinator stops as it would have done inline.</remarks>
internal sealed class IndexingWorkers : IDisposable
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(250);

    // Monitor (not System.Threading.Lock) because waiting needs Monitor.Wait/PulseAll.
    private readonly object _lock = new();
    private readonly IndexingPace _pace;
    private readonly Queue<Action> _queue = new();
    private readonly List<Thread> _threads = [];
    private int _inFlight;
    private int _busyThreads;
    private Exception? _fault;
    private bool _disposed;

    public IndexingWorkers(IndexingPace pace)
    {
        _pace = pace;
        _pace.Changed += OnPaceChanged;
    }

    /// <summary>Items queued or running.</summary>
    public int InFlight
    {
        get { lock (_lock) return _inFlight; }
    }

    /// <summary>Threads started so far (tests).</summary>
    internal int ThreadCount
    {
        get { lock (_lock) return _threads.Count; }
    }

    /// <summary>Queues <paramref name="work"/> once a slot is free (waiting for one if needed).</summary>
    public void Run(Action work, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(work);
        lock (_lock)
        {
            while (true)
            {
                ThrowIfFaultedLocked();
                ObjectDisposedException.ThrowIf(_disposed, this);
                cancellationToken.ThrowIfCancellationRequested();
                if (_inFlight < _pace.Concurrency)
                    break;
                Monitor.Wait(_lock, PollInterval);
            }
            _inFlight++;
            _pace.ReportInFlight(_inFlight);
            _queue.Enqueue(work);
            // Threads not running an item will pick up queued ones; start another only when there are too few.
            if (_queue.Count > _threads.Count - _busyThreads && _threads.Count < _pace.MaxConcurrency)
                StartThreadLocked();
            Monitor.PulseAll(_lock);
        }
    }

    /// <summary>Waits until every queued item has finished, then rethrows the first failure among them.</summary>
    public void WaitIdle(CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            // Not cancellable while items run: they observe the same token and finish quickly, and the caller must
            // not move on (or dispose what they use) while they still run.
            while (_inFlight > 0)
                Monitor.Wait(_lock, PollInterval);
            ThrowIfFaultedLocked();
        }
        cancellationToken.ThrowIfCancellationRequested();
    }

    private void ThrowIfFaultedLocked()
    {
        if (_fault is not { } fault)
            return;
        _fault = null;
        System.Runtime.ExceptionServices.ExceptionDispatchInfo.Throw(fault);
    }

    private void StartThreadLocked()
    {
        var thread = new Thread(WorkerLoop)
        {
            IsBackground = true,
            Name = "Valafind indexing worker " + (_threads.Count + 1),
        };
        _threads.Add(thread);
        thread.Start();
    }

    private void WorkerLoop()
    {
        using var priority = BackgroundPriority.EnterForCurrentThread();
        while (true)
        {
            Action work;
            lock (_lock)
            {
                while (_queue.Count == 0)
                {
                    if (_disposed)
                        return;
                    Monitor.Wait(_lock);
                }
                work = _queue.Dequeue();
                _busyThreads++;
            }

            try
            {
                work();
            }
#pragma warning disable CA1031 // handed to the dispatcher, which rethrows it
            catch (Exception e)
#pragma warning restore CA1031
            {
                lock (_lock)
                    _fault ??= e;
            }
            finally
            {
                lock (_lock)
                {
                    _inFlight--;
                    _busyThreads--;
                    _pace.ReportInFlight(_inFlight);
                    Monitor.PulseAll(_lock);
                }
            }
        }
    }

    private void OnPaceChanged(object? sender, EventArgs e)
    {
        lock (_lock)
            Monitor.PulseAll(_lock);
    }

    /// <summary>Stops the threads once the queue is empty. Call after <see cref="WaitIdle"/>.</summary>
    public void Dispose()
    {
        List<Thread> threads;
        lock (_lock)
        {
            if (_disposed)
                return;
            _disposed = true;
            _queue.Clear();
            threads = [.. _threads];
            Monitor.PulseAll(_lock);
        }
        _pace.Changed -= OnPaceChanged;
        foreach (var thread in threads)
        {
            if (thread != Thread.CurrentThread)
                thread.Join(TimeSpan.FromSeconds(5));
        }
    }
}
