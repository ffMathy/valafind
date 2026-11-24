using Microsoft.Extensions.Logging;
using Valafind.Core.Abstractions;
using Valafind.Core.Platform;

namespace Valafind.Core.Indexing;

/// <summary>
/// The cloud lane: online-only files are indexed on a lane of their own, so waiting for the cloud never holds back the
/// files that are already on this device.
/// </summary>
/// <remarks>
/// <para>A pass hands every online-only file to <see cref="EnqueueCloud"/> and goes straight on with the next file. The
/// lane's thread requests those files from the sync client ahead (<see cref="CloudPrefetcher"/>, within its limits) and
/// gives each one that has arrived to the workers on their cloud lane (<see cref="WorkLane.Cloud"/>): at most half of
/// the workers, and the passes never wait for them. Files that cannot be requested ahead are downloaded one at a time
/// on the lane's thread, never on a worker. Giving files back (online-only again) happens on a release thread of its
/// own, retried later when another program has a file open, so no worker ever waits for that either. Within the lane,
/// documents come before pictures, audio and video, as in a pass.</para>
/// <para>A full walk is only recorded as complete once the lane is empty too, so a restart never forgets a queued file;
/// by then every file on this device has been indexed, and file changes are handled while it waits.</para>
/// </remarks>
public sealed partial class IndexingCoordinator
{
    /// <summary>An online-only file queued on (or being handled by) the cloud lane.</summary>
    private sealed class CloudItem(string path, IndexPhase phase)
    {
        public string Path { get; } = path;
        public IndexPhase Phase { get; } = phase;

        /// <summary>Run once the file is handled (the passes' progress counters).</summary>
        public List<Action> OnDone { get; } = [];

        /// <summary>Taken off the queue: a change reported meanwhile is handled by a second round (<see cref="Again"/>).</summary>
        public bool Taken { get; set; }

        public bool Again { get; set; }
        public List<Action> Later { get; } = [];

        /// <summary>Completed (guards against completing twice when a failure follows a hand-off).</summary>
        public int Completed;
    }

    /// <summary>A file to give back, tried again later while it is not done yet. <see cref="Abandon"/> forgets it
    /// without touching it, when a call hung.</summary>
    private sealed record PendingRelease(string Name, Func<bool> TryOnce, Action GiveUp, Action Abandon)
    {
        public int Attempts { get; set; }
        public DateTimeOffset DueUtc { get; set; }
    }

    /// <summary>Attempts at giving a file back before it is left for the next restart: about two minutes in all.</summary>
    private const int MaxReleaseAttempts = 8;

    /// <summary>Longest a single call to give a file back may take (<see cref="CoordinatorTuning.CloudReleaseTimeout"/>).
    /// One that takes longer is left to finish on its own (it cannot be cancelled), and the release thread goes on
    /// without it.</summary>
    private TimeSpan ReleaseCallTimeout => _tuning.CloudReleaseTimeout;

    /// <summary>Calls that may hang before Valafind stops downloading cloud files for this run: a file it downloads must
    /// be given back, and with calls hanging that cannot be relied on.</summary>
    private const int MaxHungReleases = 3;

    private int _hungReleases;

    /// <summary>Set when giving files back keeps hanging: no more cloud downloads this run (names are still indexed).</summary>
    private volatile bool _cloudDownloadsOff;
    private static readonly TimeSpan CloudStatusInterval = TimeSpan.FromMinutes(1);

    private readonly Lock _cloudLock = new();
    private readonly PriorityQueue<CloudItem, (int Rank, long Seq)> _cloudQueue = new();
    private Dictionary<string, CloudItem> _cloudItems = null!;
    private long _cloudSeq;
    private readonly AutoResetEvent _cloudSignal = new(false);
    private Thread? _cloudThread;
    private volatile bool _cloudLaneRunning;

    private readonly Lock _releaseLock = new();
    private readonly List<PendingRelease> _releases = [];
    private readonly AutoResetEvent _releaseSignal = new(false);
    private Thread? _releaseThread;

    /// <summary>Online-only files queued or being handled.</summary>
    private bool CloudLaneBusy
    {
        get
        {
            lock (_cloudLock)
                return _cloudItems.Count > 0;
        }
    }

    private int ReleasesPending
    {
        get
        {
            lock (_releaseLock)
                return _releases.Count;
        }
    }

    private void StartCloudLane(CancellationToken cancellationToken)
    {
        _releaseThread = new Thread(() => RunReleases(cancellationToken))
        {
            IsBackground = true,
            Name = "Valafind cloud releases",
        };
        _releaseThread.Start();
        var thread = new Thread(() => RunCloudLane(cancellationToken))
        {
            IsBackground = true,
            Name = "Valafind cloud files",
        };
        _cloudThread = thread;
        _cloudLaneRunning = true;
        thread.Start();
    }

    /// <summary>Waits (at most <paramref name="timeout"/> each) for the lane's threads to end after cancellation.</summary>
    private void StopCloudLane(TimeSpan timeout)
    {
        _cloudLaneRunning = false;
        foreach (var thread in new[] { _cloudThread, _releaseThread })
        {
            if (thread is not null && thread != Thread.CurrentThread)
                thread.Join(timeout);
        }
    }

    /// <summary>Queues an online-only file for the lane and returns at once.</summary>
    private void EnqueueCloud(string path, IndexPhase phase, Action? onDone)
    {
        lock (_cloudLock)
        {
            if (_cloudItems.TryGetValue(path, out var existing))
            {
                // Already queued: one round handles both. Already being handled: go round once more afterwards, as
                // the file may have changed since it was read.
                if (existing.Taken)
                {
                    existing.Again = true;
                    if (onDone is not null)
                        existing.Later.Add(onDone);
                }
                else if (onDone is not null)
                {
                    existing.OnDone.Add(onDone);
                }
                return;
            }
            var item = new CloudItem(path, phase);
            if (onDone is not null)
                item.OnDone.Add(onDone);
            _cloudItems[path] = item;
            _cloudQueue.Enqueue(item, (IndexOrder.Rank(path), _cloudSeq++));
        }
        _gate.SetIdle(false);
        _cloudSignal.Set();
    }

    private void CompleteCloud(CloudItem item)
    {
        if (Interlocked.Exchange(ref item.Completed, 1) != 0)
            return;
        bool empty;
        lock (_cloudLock)
        {
            if (_cloudItems.TryGetValue(item.Path, out var current) && ReferenceEquals(current, item))
                _cloudItems.Remove(item.Path);
            if (item.Again)
            {
                var again = new CloudItem(item.Path, item.Phase);
                again.OnDone.AddRange(item.Later);
                _cloudItems[item.Path] = again;
                _cloudQueue.Enqueue(again, (IndexOrder.Rank(item.Path), _cloudSeq++));
            }
            empty = _cloudItems.Count == 0;
        }
        foreach (var done in item.OnDone)
        {
            try
            {
                done();
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                IndexingLog.CloudLaneFailed(_logger, e);
            }
        }
        _cloudSignal.Set();
        if (empty)
            _pendingSignal.Set(); // lets the idle loop notice that everything is done
    }

    /// <summary>Handles changes reported by the watchers until the lane is empty. Used before a walk is recorded as
    /// complete; every file on this device is indexed by then.</summary>
    private void WaitForCloudLane(CancellationToken cancellationToken)
    {
        var handles = new[] { cancellationToken.WaitHandle, _pendingSignal };
        while (_cloudLaneRunning && CloudLaneBusy)
        {
            DrainPending(cancellationToken);
            WaitHandle.WaitAny(handles, TimeSpan.FromSeconds(1));
            cancellationToken.ThrowIfCancellationRequested();
        }
    }

    private void RunCloudLane(CancellationToken cancellationToken)
    {
        using var priority = BackgroundPriority.EnterForCurrentThread();
        try
        {
            ReleaseEarlierDownloads(cancellationToken);
            var poll = (_tuning.Prefetch ?? CloudPrefetchOptions.Default).PollInterval;
            var nextStatus = _time.GetUtcNow() + CloudStatusInterval;
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                bool progressed;
                try
                {
                    progressed = DispatchArrived(cancellationToken) | TakeQueued(cancellationToken);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception e)
                {
                    // Per-file failures are handled where they happen; this is a last resort so the lane goes on.
                    IndexingLog.CloudLaneFailed(_logger, e);
                    progressed = false;
                }
                if (_time.GetUtcNow() >= nextStatus)
                {
                    nextStatus = _time.GetUtcNow() + CloudStatusInterval;
                    LogCloudStatus();
                }
                if (progressed)
                    continue;
                WaitHandle[] handles = _prefetch is { } prefetch
                    ? [cancellationToken.WaitHandle, _cloudSignal, prefetch.Changed]
                    : [cancellationToken.WaitHandle, _cloudSignal];
                WaitHandle.WaitAny(handles, poll);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (ObjectDisposedException) when (cancellationToken.IsCancellationRequested)
        {
        }
        finally
        {
            _cloudLaneRunning = false;
        }
    }

    /// <summary>What the lane is doing, for the log (a stuck lane shows here).</summary>
    private void LogCloudStatus()
    {
        int queued;
        lock (_cloudLock)
            queued = _cloudItems.Count;
        var requested = _prefetch?.Count ?? 0;
        var arriving = _prefetch?.Waiting ?? 0;
        var indexing = _workers.InFlightOf(WorkLane.Cloud);
        var releasing = ReleasesPending;
        if (queued + requested + indexing + releasing == 0)
            return;
        IndexingLog.CloudLaneStatus(_logger, queued, requested, arriving, indexing, releasing);
    }

    /// <summary>Takes queued files in order and requests them (or, when that is not possible, downloads them one at a
    /// time on this thread). Returns whether anything was handled.</summary>
    private bool TakeQueued(CancellationToken cancellationToken)
    {
        var progressed = false;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CloudItem item;
            (int Rank, long Seq) order;
            lock (_cloudLock)
            {
                if (!_cloudQueue.TryDequeue(out item!, out order))
                    return progressed;
                item.Taken = true;
            }
            void Done() => CompleteCloud(item);

            try
            {
                if (!HandleQueued(item, order, Done, cancellationToken))
                    return progressed; // limits reached: back in the queue
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception e)
            {
                // Never leave a file half-handled: a full walk waits until every queued file is done.
                IndexingLog.CloudFileFailed(_logger, e, item.Path);
                Done();
            }
            progressed = true;
        }
    }

    /// <summary>Handles one queued file. False when the request limits are full: it is back at the front of the queue.</summary>
    private bool HandleQueued(CloudItem item, (int Rank, long Seq) order, Action done, CancellationToken cancellationToken)
    {
        if (Prepare(item.Path, out _) is not { } prepared)
        {
            done(); // vanished, unchanged, or indexed meanwhile
            return true;
        }
        if (!prepared.OnlineOnly)
        {
            RunIndex(prepared, item.Phase, done, after: null, cancellationToken, lane: WorkLane.Cloud); // downloaded meanwhile
            return true;
        }
        if (!CanDownload(item.Path, prepared.Size))
        {
            RunIndex(prepared, item.Phase, done, after: null, cancellationToken, allowDownload: false, lane: WorkLane.Cloud); // by name
            return true;
        }
        if (_prefetch is { } prefetch && !_prefetchOff)
        {
            var result = prefetch.TryHold(item.Path, prepared.Size, item.Phase, done);
            if (result == CloudPrefetcher.HoldResult.Held)
                return true;
            if (result == CloudPrefetcher.HoldResult.NoRoom)
            {
                // Limits reached: back to the front of the queue until requested files are given back.
                lock (_cloudLock)
                {
                    item.Taken = false;
                    _cloudQueue.Enqueue(item, order);
                }
                return false;
            }
        }
        DownloadThenIndex(prepared, item.Phase, done, cancellationToken);
        return true;
    }

    /// <summary>Downloads one file on this (the lane's) thread, then indexes it on a worker; it is given back afterwards
    /// on the release thread. When it cannot be downloaded, it is indexed by name.</summary>
    private void DownloadThenIndex(PreparedFile prepared, IndexPhase phase, Action done, CancellationToken cancellationToken)
    {
        var lease = CanDownload(prepared.Path, prepared.Size) ? _cloud.Download(prepared.Path, prepared.Size, cancellationToken) : null;
        if (lease is null)
        {
            IndexingLog.OnlineOnlyNameOnly(_logger, prepared.Path);
            RunIndex(prepared, phase, done, after: null, cancellationToken, allowDownload: false, lane: WorkLane.Cloud);
            return;
        }
        var handedOff = false;
        try
        {
            if (Prepare(prepared.Path, out _) is { } local)
            {
                RunIndex(local, phase, done, after: () => ReleaseLater(prepared.Path, lease), cancellationToken,
                    allowDownload: false, lane: WorkLane.Cloud);
                handedOff = true;
                return;
            }
        }
        finally
        {
            if (!handedOff)
                ReleaseLater(prepared.Path, lease);
        }
        done();
    }

    /// <summary>Gives the requested files that have arrived to the workers (each is given back once indexed). Files
    /// that did not arrive in time are given back and then downloaded one at a time. Returns whether anything was
    /// handled.</summary>
    private bool DispatchArrived(CancellationToken cancellationToken)
    {
        if (_prefetch is not { } prefetch)
            return false;
        var arrived = prefetch.TakeArrived(out var stalled);
        foreach (var entry in stalled)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var done = entry.OnDone ?? (static () => { });
            try
            {
                IndexingLog.DownloadStalled(_logger, entry.Path);
                prefetch.Release(entry.Path);
                if (Prepare(entry.Path, out _) is { } prepared)
                {
                    if (prepared.OnlineOnly)
                        DownloadThenIndex(prepared, entry.Download.Phase, done, cancellationToken);
                    else
                        RunIndex(prepared, entry.Download.Phase, done, after: null, cancellationToken, lane: WorkLane.Cloud);
                }
                else
                {
                    done();
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception e)
            {
                IndexingLog.CloudFileFailed(_logger, e, entry.Path);
                done();
            }
        }
        foreach (var entry in arrived)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var path = entry.Path;
            var handedOff = false;
            try
            {
                if (Prepare(path, out _) is { } prepared)
                {
                    RunIndex(prepared, entry.Download.Phase, entry.OnDone, after: () => ReleaseLater(prefetch, path), cancellationToken,
                        allowDownload: false, lane: WorkLane.Cloud);
                    handedOff = true;
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception e)
            {
                IndexingLog.CloudFileFailed(_logger, e, path);
            }
            if (!handedOff)
            {
                ReleaseLater(prefetch, path);
                entry.OnDone?.Invoke();
            }
        }
        return arrived.Count > 0 || stalled.Count > 0;
    }

    // ------------------------------------------------------------------ giving files back

    /// <summary>Gives a requested file back on the release thread; retried there while another program has it open.</summary>
    private void ReleaseLater(CloudPrefetcher prefetch, string path) =>
        QueueRelease(new PendingRelease(path, () => prefetch.TryRelease(path, lastAttempt: false),
            () => prefetch.TryRelease(path, lastAttempt: true), () => prefetch.Forget(path)));

    /// <summary>Frees a file downloaded one at a time (the lease's own safety rules apply) on the release thread.</summary>
    private void ReleaseLater(string path, IDisposable lease) =>
        QueueRelease(new PendingRelease(path, () =>
        {
            lease.Dispose();
            return true;
        }, static () => { }, static () => { }));

    private void QueueRelease(PendingRelease release)
    {
        release.DueUtc = _time.GetUtcNow();
        lock (_releaseLock)
            _releases.Add(release);
        try
        {
            _releaseSignal.Set();
        }
        catch (ObjectDisposedException)
        {
        }
    }

    /// <summary>The release thread: gives files back as they come, and tries again (1 s, 2 s, 4 s, … apart) when one is
    /// in use. On shutdown it makes one last attempt at each file still waiting.</summary>
    private void RunReleases(CancellationToken cancellationToken)
    {
        using var priority = BackgroundPriority.EnterForCurrentThread();
        var handles = new[] { cancellationToken.WaitHandle, _releaseSignal };
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var now = _time.GetUtcNow();
                List<PendingRelease> due;
                TimeSpan wait;
                lock (_releaseLock)
                {
                    due = _releases.Where(r => r.DueUtc <= now).ToList();
                    foreach (var release in due)
                        _releases.Remove(release);
                    wait = _releases.Count == 0 ? TimeSpan.FromSeconds(30) : _releases.Min(r => r.DueUtc) - now;
                }
                foreach (var release in due)
                {
                    if (cancellationToken.IsCancellationRequested)
                    {
                        lock (_releaseLock)
                            _releases.Add(release);
                        break;
                    }
                    TryReleaseOnce(release);
                }
                if (due.Count == 0)
                    WaitHandle.WaitAny(handles, wait < TimeSpan.Zero ? TimeSpan.Zero : wait);
            }
        }
        catch (ObjectDisposedException)
        {
        }
        finally
        {
            FinishReleases();
        }
    }

    private void TryReleaseOnce(PendingRelease release)
    {
        if (!TryGuarded(release, release.TryOnce, ReleaseCallTimeout, out var done))
            return; // hung: abandoned
        if (done)
            return;
        release.Attempts++;
        if (release.Attempts >= MaxReleaseAttempts)
        {
            GiveUp(release); // stays recorded: given back after the next restart
            return;
        }
        release.DueUtc = _time.GetUtcNow() + TimeSpan.FromSeconds(Math.Pow(2, release.Attempts - 1));
        lock (_releaseLock)
            _releases.Add(release);
    }

    private void GiveUp(PendingRelease release) =>
        TryGuarded(release, () =>
        {
            release.GiveUp();
            return true;
        }, ReleaseCallTimeout, out _);

    /// <summary>Runs one call to give a file back, for at most <paramref name="timeout"/>. False when it hung: the file
    /// is abandoned (it stays recorded for the next start), and after <see cref="MaxHungReleases"/> hangs cloud
    /// downloads stop for this run.</summary>
    private bool TryGuarded(PendingRelease release, Func<bool> call, TimeSpan timeout, out bool done)
    {
        done = false;
        // A thread of its own, not the thread pool: a call stuck in the pool would delay the next ones until they too
        // timed out before they even started.
        var task = Task.Factory.StartNew(call, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        try
        {
            if (task.Wait(timeout))
            {
                done = task.Result;
                return true;
            }
        }
        catch (AggregateException e) when (e.InnerException is not OperationCanceledException)
        {
            IndexingLog.CloudFileFailed(_logger, e.InnerException ?? e, release.Name);
            return true;
        }

        IndexingLog.CloudReleaseHung(_logger, release.Name, timeout);
        // Count the hang, and stop downloads at the last one, before abandoning the file: abandoning frees its place
        // among the files being downloaded, and the cloud lane would otherwise fill it with one more download first.
        if (Interlocked.Increment(ref _hungReleases) == MaxHungReleases)
        {
            _cloudDownloadsOff = true;
            _prefetchOff = true;
            IndexingLog.CloudDownloadsStopped(_logger, MaxHungReleases);
        }
        try
        {
            release.Abandon();
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            IndexingLog.CloudFileFailed(_logger, e, release.Name);
        }
        return false;
    }

    /// <summary>On shutdown: one last attempt at every file still waiting to be given back, for at most ten seconds;
    /// what is left stays recorded for the next start.</summary>
    private void FinishReleases()
    {
        List<PendingRelease> left;
        lock (_releaseLock)
        {
            left = [.. _releases];
            _releases.Clear();
        }
        var deadline = _time.GetUtcNow() + TimeSpan.FromSeconds(10);
        foreach (var release in left)
        {
            var remaining = deadline - _time.GetUtcNow();
            if (remaining <= TimeSpan.Zero)
            {
                release.Abandon(); // recorded: given back at the next start
                continue;
            }
            if (TryGuarded(release, release.TryOnce, remaining, out var done) && !done)
                release.Abandon();
        }
    }

    /// <summary>
    /// Downloads requested by an earlier run and not yet given back (Valafind was stopped, or the computer restarted,
    /// while they were requested): the ones that arrived meanwhile are indexed and every one is given back, so no file
    /// stays downloaded or pinned because of Valafind. Runs on the lane, so it holds nothing else back.
    /// </summary>
    private void ReleaseEarlierDownloads(CancellationToken cancellationToken)
    {
        if (_prefetch is not { } prefetch)
            return;
        IReadOnlyList<CloudDownload> downloads;
        try
        {
            downloads = prefetch.Adopt();
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            IndexingLog.DownloadJournalFailed(_logger, e);
            _prefetchOff = true;
            return;
        }
        if (downloads.Count == 0)
            return;
        IndexingLog.ReleasingEarlierDownloads(_logger, downloads.Count);
        foreach (var download in downloads)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var path = download.Path;
            try
            {
                if (!prefetch.HasArrived(path) || !File.Exists(path) || _discovery.IsExcluded(path))
                {
                    ReleaseLater(prefetch, path);
                    continue;
                }
                if (Prepare(path, out _) is { } prepared)
                    RunIndex(prepared, download.Phase, onDone: null, after: () => ReleaseLater(prefetch, path), cancellationToken,
                        allowDownload: false, lane: WorkLane.Cloud);
                else
                    ReleaseLater(prefetch, path);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception e)
            {
                IndexingLog.CloudFileFailed(_logger, e, path);
                ReleaseLater(prefetch, path);
            }
        }
    }
}
