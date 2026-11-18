using Valafind.Core.Abstractions;
using Valafind.Core.Platform;

namespace Valafind.Core.Indexing;

/// <summary>
/// The cloud lane: online-only files are indexed on a lane of their own, so waiting for the cloud never holds back the
/// files that are already on this device.
/// </summary>
/// <remarks>
/// <para>A pass hands every online-only file it meets to <see cref="EnqueueCloud"/> and goes straight on with the next
/// file. The lane's thread requests those files from the sync client ahead (<see cref="CloudPrefetcher"/>, within its
/// limits), gives each one that has arrived to the shared workers to index, and gives it back afterwards. Files that
/// cannot be requested ahead are downloaded one at a time on the lane's thread, never on a worker. Within the lane,
/// documents come before pictures, audio and video, as in a pass; across lanes nothing waits.</para>
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
    }

    private readonly Lock _cloudLock = new();
    private readonly PriorityQueue<CloudItem, (int Rank, long Seq)> _cloudQueue = new();
    private Dictionary<string, CloudItem> _cloudItems = null!;
    private long _cloudSeq;
    private readonly AutoResetEvent _cloudSignal = new(false);
    private Thread? _cloudThread;
    private volatile bool _cloudLaneRunning;

    /// <summary>Online-only files queued or being handled.</summary>
    private bool CloudLaneBusy
    {
        get
        {
            lock (_cloudLock)
                return _cloudItems.Count > 0;
        }
    }

    private void StartCloudLane(CancellationToken cancellationToken)
    {
        var thread = new Thread(() => RunCloudLane(cancellationToken))
        {
            IsBackground = true,
            Name = "Valafind cloud files",
        };
        _cloudThread = thread;
        _cloudLaneRunning = true;
        thread.Start();
    }

    /// <summary>Waits (at most <paramref name="timeout"/>) for the lane's thread to end after cancellation.</summary>
    private void StopCloudLane(TimeSpan timeout)
    {
        _cloudLaneRunning = false;
        if (_cloudThread is { } thread && thread != Thread.CurrentThread)
            thread.Join(timeout);
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
            done();
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
                    // One bad file must not stop the lane (a full walk waits for it to empty).
                    IndexingLog.CloudLaneFailed(_logger, e);
                    progressed = false;
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

            if (Prepare(item.Path, out _) is not { } prepared)
            {
                Done(); // vanished, unchanged, or indexed meanwhile
                progressed = true;
                continue;
            }
            if (!prepared.OnlineOnly)
            {
                RunIndex(prepared, item.Phase, Done, after: null, cancellationToken); // the user downloaded it meanwhile
                progressed = true;
                continue;
            }
            if (!CanDownload(item.Path, prepared.Size))
            {
                RunIndex(prepared, item.Phase, Done, after: null, cancellationToken, allowDownload: false); // by name
                progressed = true;
                continue;
            }
            if (_prefetch is { } prefetch && !_prefetchOff)
            {
                var result = prefetch.TryHold(item.Path, prepared.Size, item.Phase, Done);
                if (result == CloudPrefetcher.HoldResult.Held)
                {
                    progressed = true;
                    continue;
                }
                if (result == CloudPrefetcher.HoldResult.NoRoom)
                {
                    // Limits reached: back to the front of the queue until requested files are given back.
                    lock (_cloudLock)
                    {
                        item.Taken = false;
                        _cloudQueue.Enqueue(item, order);
                    }
                    return progressed;
                }
            }
            DownloadThenIndex(prepared, item.Phase, Done, cancellationToken);
            progressed = true;
        }
    }

    /// <summary>Downloads one file on this (the lane's) thread, then indexes it on a worker and gives it back. When it
    /// cannot be downloaded, it is indexed by name.</summary>
    private void DownloadThenIndex(PreparedFile prepared, IndexPhase phase, Action done, CancellationToken cancellationToken)
    {
        var lease = CanDownload(prepared.Path, prepared.Size) ? _cloud.Download(prepared.Path, prepared.Size, cancellationToken) : null;
        if (lease is null)
        {
            IndexingLog.OnlineOnlyNameOnly(_logger, prepared.Path);
            RunIndex(prepared, phase, done, after: null, cancellationToken, allowDownload: false);
            return;
        }
        try
        {
            if (Prepare(prepared.Path, out _) is { } local)
            {
                RunIndex(local, phase, done, after: lease.Dispose, cancellationToken, allowDownload: false);
                return;
            }
        }
        catch
        {
            lease.Dispose();
            throw;
        }
        lease.Dispose();
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
            IndexingLog.DownloadStalled(_logger, entry.Path);
            prefetch.Release(entry.Path);
            var done = entry.OnDone ?? (static () => { });
            if (Prepare(entry.Path, out _) is { } prepared)
            {
                if (prepared.OnlineOnly)
                    DownloadThenIndex(prepared, entry.Download.Phase, done, cancellationToken);
                else
                    RunIndex(prepared, entry.Download.Phase, done, after: null, cancellationToken);
            }
            else
            {
                done();
            }
        }
        foreach (var entry in arrived)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var path = entry.Path;
            if (Prepare(path, out _) is { } prepared)
            {
                RunIndex(prepared, entry.Download.Phase, entry.OnDone, after: () => prefetch.Release(path), cancellationToken,
                    allowDownload: false);
            }
            else
            {
                prefetch.Release(path);
                entry.OnDone?.Invoke();
            }
        }
        return arrived.Count > 0 || stalled.Count > 0;
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
            if (!prefetch.HasArrived(path) || !File.Exists(path) || _discovery.IsExcluded(path))
            {
                prefetch.Release(path);
                continue;
            }
            if (Prepare(path, out _) is { } prepared)
                RunIndex(prepared, download.Phase, onDone: null, after: () => prefetch.Release(path), cancellationToken, allowDownload: false);
            else
                prefetch.Release(path);
        }
    }
}
