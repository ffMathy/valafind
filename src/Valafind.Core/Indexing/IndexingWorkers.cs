using Valafind.Core.Platform;

namespace Valafind.Core.Indexing;

/// <summary>Who queued a work item: the passes over the files on this device, or the cloud lane.</summary>
internal enum WorkLane
{
    Main = 0,
    Cloud = 1,
}

/// <summary>
/// Runs indexing work items on up to <see cref="IndexingPace.MaxConcurrency"/> low-priority threads, never more at once
/// than <see cref="IndexingPace.Concurrency"/> allows at that moment. <see cref="Run"/> blocks the caller (the
/// coordinator's dispatcher) while every allowed slot is busy, so the dispatcher never runs ahead of the workers, and
/// a lower pace takes effect as running items finish. Threads are started on demand and kept until disposal.
/// </summary>
/// <remarks>
/// <para>Work items should not throw; one that does fails the next <see cref="Run"/> or <see cref="WaitIdle"/> call of
/// its lane with its exception (cancellation included), so the coordinator stops as it would have done inline.</para>
/// <para>Two lanes share the threads (<see cref="WorkLane"/>). The main lane never waits for the cloud lane: it counts
/// only its own items against <see cref="IndexingPace.Concurrency"/>, and <see cref="WaitIdle"/> waits only for the
/// caller's lane. Cloud items take a slot only while the pace has one free, and never more than half of them, so files
/// on this device always have room.</para>
/// </remarks>
internal sealed class IndexingWorkers : IDisposable
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(250);

    // Monitor (not System.Threading.Lock) because waiting needs Monitor.Wait/PulseAll.
    private readonly object _lock = new();
    private readonly IndexingPace _pace;
    private readonly Queue<(Action Work, WorkLane Lane)> _queue = new();
    private readonly List<Thread> _threads = [];
    private int _inFlight;
    private readonly int[] _laneInFlight = new int[2];
    private int _busyThreads;
    private readonly Exception?[] _faults = new Exception?[2];
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

    /// <summary>Items of <paramref name="lane"/> queued or running.</summary>
    public int InFlightOf(WorkLane lane)
    {
        lock (_lock)
            return _laneInFlight[(int)lane];
    }

    /// <summary>Most cloud items at once: half the pace's slots, at least one.</summary>
    private int CloudShare => Math.Max(1, _pace.Concurrency / 2);

    /// <summary>Threads started so far (tests).</summary>
    internal int ThreadCount
    {
        get { lock (_lock) return _threads.Count; }
    }

    /// <summary>Queues <paramref name="work"/> once a slot is free (waiting for one if needed).</summary>
    public void Run(Action work, CancellationToken cancellationToken, WorkLane lane = WorkLane.Main)
    {
        ArgumentNullException.ThrowIfNull(work);
        lock (_lock)
        {
            while (true)
            {
                ThrowIfFaultedLocked(lane);
                ObjectDisposedException.ThrowIf(_disposed, this);
                cancellationToken.ThrowIfCancellationRequested();
                var free = lane == WorkLane.Main
                    ? _laneInFlight[(int)WorkLane.Main] < _pace.Concurrency
                    : _inFlight < _pace.Concurrency && _laneInFlight[(int)WorkLane.Cloud] < CloudShare;
                if (free)
                    break;
                Monitor.Wait(_lock, PollInterval);
            }
            _inFlight++;
            _laneInFlight[(int)lane]++;
            _pace.ReportInFlight(_inFlight);
            _queue.Enqueue((work, lane));
            // Threads not running an item will pick up queued ones; start another only when there are too few.
            if (_queue.Count > _threads.Count - _busyThreads && _threads.Count < _pace.MaxConcurrency)
                StartThreadLocked();
            Monitor.PulseAll(_lock);
        }
    }

    /// <summary>Waits until every queued item of <paramref name="lane"/> has finished, then rethrows the first failure
    /// among them. Items of the other lane are not waited for.</summary>
    public void WaitIdle(CancellationToken cancellationToken, WorkLane lane = WorkLane.Main)
    {
        lock (_lock)
        {
            // Not cancellable while items run: they observe the same token and finish quickly, and the caller must
            // not move on (or dispose what they use) while they still run.
            while (_laneInFlight[(int)lane] > 0)
                Monitor.Wait(_lock, PollInterval);
            ThrowIfFaultedLocked(lane);
        }
        cancellationToken.ThrowIfCancellationRequested();
    }

    /// <summary>Waits until no item of either lane runs (before disposing what they use).</summary>
    public void WaitAllIdle()
    {
        lock (_lock)
        {
            while (_inFlight > 0)
                Monitor.Wait(_lock, PollInterval);
        }
    }

    private void ThrowIfFaultedLocked(WorkLane lane)
    {
        if (_faults[(int)lane] is not { } fault)
            return;
        _faults[(int)lane] = null;
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
            WorkLane lane;
            lock (_lock)
            {
                while (_queue.Count == 0)
                {
                    if (_disposed)
                        return;
                    Monitor.Wait(_lock);
                }
                (work, lane) = _queue.Dequeue();
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
                    _faults[(int)lane] ??= e;
            }
            finally
            {
                lock (_lock)
                {
                    _inFlight--;
                    _laneInFlight[(int)lane]--;
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
