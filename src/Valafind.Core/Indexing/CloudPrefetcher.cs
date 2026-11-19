using Valafind.Core.Abstractions;
using Valafind.Core.Platform;

namespace Valafind.Core.Indexing;

/// <summary>An online-only file Valafind asked the sync client to download ahead of indexing.</summary>
internal sealed record CloudDownload(string Path, long Size, IndexPhase Phase, CloudMark Mark, DateTimeOffset RequestedUtc);

/// <summary>Optional capability of an <see cref="IIndexStore"/>: a durable list of the downloads Valafind requested, so
/// they are undone after a restart (or a crash) even when they finished while Valafind was not running.</summary>
internal interface ICloudDownloadJournal
{
    IReadOnlyList<CloudDownload> LoadDownloads();

    void AddDownload(CloudDownload download);

    void RemoveDownload(string path);
}

/// <summary>Limits for <see cref="CloudPrefetcher"/>.</summary>
internal sealed record CloudPrefetchOptions
{
    /// <summary>Most files requested ahead at once.</summary>
    public int MaxFiles { get; init; } = 1000;

    /// <summary>Share of the drive's free space (counting the space the requested files already take) that requested
    /// files may occupy.</summary>
    public double BudgetFraction { get; init; } = 0.10;

    /// <summary>When no requested file has arrived for this long, the waiting ones are given back and read one at a
    /// time instead (no connection, or a sync client that ignores the request).</summary>
    public TimeSpan StallTimeout { get; init; } = TimeSpan.FromMinutes(3);

    /// <summary>How often requested files are checked for having arrived.</summary>
    public TimeSpan PollInterval { get; init; } = TimeSpan.FromMilliseconds(500);

    /// <summary>Free bytes on the drive holding a path (null: unknown). Defaults to <see cref="DriveInfo"/>.</summary>
    public Func<string, long?>? FreeBytes { get; init; }

    public static CloudPrefetchOptions Default { get; } = new();
}

/// <summary>
/// Downloads online-only files ahead of the indexer so it never waits for one file at a time: up to
/// <see cref="CloudPrefetchOptions.MaxFiles"/> files, and at most <see cref="CloudPrefetchOptions.BudgetFraction"/> of
/// the free disk space, are requested from the sync client at once (<see cref="ICloudFiles.RequestDownload"/>), which
/// downloads them in parallel. A file that has arrived is indexed and then released
/// (<see cref="ICloudFiles.Release"/>: online-only again). Every request is recorded in an
/// <see cref="ICloudDownloadJournal"/> before anything else happens to it and removed once released, so a restart finds
/// whatever is still requested (<see cref="Adopt"/>).
/// </summary>
/// <remarks><see cref="TryHold"/>, <see cref="TakeArrived"/> and <see cref="Adopt"/> are called by the dispatcher
/// thread; <see cref="Release"/> by any thread.</remarks>
internal sealed class CloudPrefetcher : IDisposable
{
    private readonly ICloudFiles _cloud;
    private readonly ICloudDownloadJournal _journal;
    private readonly TimeProvider _time;
    private readonly CloudPrefetchOptions _options;
    private readonly Func<string, long?> _freeBytes;
    private readonly Lock _lock = new();
    private readonly AutoResetEvent _changed = new(false);
    private readonly Dictionary<string, Entry> _active;
    private readonly List<Entry> _waiting = [];
    private readonly Dictionary<string, long> _bytesByDrive;
    private DateTimeOffset _lastArrival;
    private long _lastPoll = long.MinValue;

    public CloudPrefetcher(ICloudFiles cloud, ICloudDownloadJournal journal, TimeProvider time, CloudPrefetchOptions options,
        StringComparer pathComparer)
    {
        _cloud = cloud;
        _journal = journal;
        _time = time;
        _options = options;
        _freeBytes = options.FreeBytes ?? DriveFreeBytes;
        _active = new Dictionary<string, Entry>(pathComparer);
        _bytesByDrive = new Dictionary<string, long>(pathComparer);
        _lastArrival = time.GetUtcNow();
    }

    /// <summary>A requested file and what to run once it is indexed.</summary>
    internal sealed class Entry(CloudDownload download, Action? onDone)
    {
        public CloudDownload Download { get; } = download;
        public Action? OnDone { get; } = onDone;
        public string Path => Download.Path;

        /// <summary>Set (under the prefetcher's lock) once a release has begun, so it happens once.</summary>
        public bool Releasing { get; set; }
    }

    internal enum HoldResult
    {
        /// <summary>Requested; it comes back from <see cref="TakeArrived"/>.</summary>
        Held,
        /// <summary>The limits are reached: wait for requested files to be released, then try again.</summary>
        NoRoom,
        /// <summary>Cannot be requested ahead (the sync client refused, or the file alone exceeds the budget): read it
        /// directly.</summary>
        Failed,
    }

    /// <summary>Signalled when a requested file is given back (room for another request).</summary>
    public WaitHandle Changed => _changed;

    /// <summary>Files requested and not yet released.</summary>
    public int Count
    {
        get { lock (_lock) return _active.Count; }
    }

    /// <summary>Requested files that have not arrived yet (not yet returned by <see cref="TakeArrived"/>).</summary>
    public int Waiting
    {
        get { lock (_lock) return _waiting.Count; }
    }

    /// <summary>Bytes of the requested, not yet released files.</summary>
    public long HeldBytes
    {
        get { lock (_lock) return _bytesByDrive.Values.Sum(); }
    }

    /// <summary>Requests <paramref name="path"/> if it fits the limits.</summary>
    public HoldResult TryHold(string path, long size, IndexPhase phase, Action? onDone)
    {
        var drive = DriveOf(path);
        lock (_lock)
        {
            if (_active.ContainsKey(path))
                return HoldResult.Failed; // already requested by an earlier pass: read it directly
            var held = _bytesByDrive.GetValueOrDefault(drive);
            var budget = Budget(path, held);
            if (_active.Count >= _options.MaxFiles || held + size > budget)
                return _active.Count == 0 || size > budget ? HoldResult.Failed : HoldResult.NoRoom;
        }

        // Taken before the request: a write to the file from here on means someone is using it, and it is kept.
        var requestedUtc = _time.GetUtcNow();
        CloudDownload? download = null;
        // Recorded before the file is changed, so even a crash right after the change is undone at the next start.
        var mark = _cloud.RequestDownload(path, m =>
        {
            download = new CloudDownload(path, size, phase, m, requestedUtc);
            try
            {
                _journal.AddDownload(download);
                return true;
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                download = null;
                return false; // not recorded: do not change the file
            }
        });
        if (mark is null || download is null)
        {
            if (download is not null)
                ForgetRecord(path); // recorded, but the change itself failed: nothing to undo
            return HoldResult.Failed;
        }

        var entry = new Entry(download, onDone);
        lock (_lock)
        {
            _active[path] = entry;
            _waiting.Add(entry);
            _bytesByDrive[drive] = _bytesByDrive.GetValueOrDefault(drive) + size;
        }
        return HoldResult.Held;
    }

    private void ForgetRecord(string path)
    {
        try
        {
            _journal.RemoveDownload(path);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            // Left in the journal: released (harmlessly: the file is not pinned by us) after the next restart.
        }
    }

    /// <summary>Takes the requested files that have arrived (or vanished) and, when nothing has arrived for
    /// <see cref="CloudPrefetchOptions.StallTimeout"/>, the ones still waiting (<paramref name="stalled"/>). Both stay
    /// requested until <see cref="Release"/>. Checks at most once per <see cref="CloudPrefetchOptions.PollInterval"/>.</summary>
    public List<Entry> TakeArrived(out List<Entry> stalled)
    {
        stalled = [];
        var arrived = new List<Entry>();
        List<Entry> waiting;
        lock (_lock)
        {
            if (_waiting.Count == 0)
                return arrived;
            var now = _time.GetTimestamp();
            if (_lastPoll != long.MinValue && _time.GetElapsedTime(_lastPoll, now) < _options.PollInterval)
                return arrived;
            _lastPoll = now;
            waiting = [.. _waiting];
        }

        foreach (var entry in waiting)
        {
            if (HasArrived(entry.Path))
                arrived.Add(entry);
        }

        lock (_lock)
        {
            var utcNow = _time.GetUtcNow();
            if (arrived.Count > 0)
                _lastArrival = utcNow;
            foreach (var entry in arrived)
                _waiting.Remove(entry);
            var oldest = _waiting.Count == 0 ? utcNow : _waiting.Min(e => e.Download.RequestedUtc);
            var since = oldest > _lastArrival ? oldest : _lastArrival;
            if (_waiting.Count > 0 && utcNow - since >= _options.StallTimeout)
            {
                stalled.AddRange(_waiting);
                _waiting.Clear();
                _lastArrival = utcNow;
            }
        }
        return arrived;
    }

    /// <summary>Waits until a requested file is released or the next poll is due.</summary>
    public void WaitForChange(CancellationToken cancellationToken)
    {
        WaitHandle.WaitAny([_changed, cancellationToken.WaitHandle], _options.PollInterval);
        cancellationToken.ThrowIfCancellationRequested();
    }

    /// <summary>Gives a requested file back (online-only again when that is safe, original setting restored) and
    /// forgets it. A file another program has open stays recorded and is given back after the next restart.</summary>
    public void Release(string path) => TryRelease(path, lastAttempt: true);

    /// <summary>One attempt at giving a requested file back.</summary>
    /// <param name="lastAttempt">When the file is in use: false keeps it requested (and counted against the limits) for
    /// another attempt later; true forgets it here, and it stays recorded for the next restart.</param>
    /// <returns>True when the file is done with (given back, or recorded for the next restart); false when it should be
    /// tried again.</returns>
    public bool TryRelease(string path, bool lastAttempt)
    {
        Entry? entry;
        lock (_lock)
        {
            if (!_active.TryGetValue(path, out entry) || entry.Releasing)
                return true;
            entry.Releasing = true;
            _waiting.Remove(entry);
        }
        var done = true;
        try
        {
            done = _cloud.Release(entry.Path, entry.Download.Mark, entry.Download.RequestedUtc);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            done = false;
        }
        if (!done && !lastAttempt)
        {
            lock (_lock)
                entry.Releasing = false;
            return false;
        }

        // Counted against the limits until it is actually given back: until then it is still on the disk.
        lock (_lock)
        {
            _active.Remove(path);
            var drive = DriveOf(path);
            var bytes = _bytesByDrive.GetValueOrDefault(drive) - entry.Download.Size;
            if (bytes > 0)
                _bytesByDrive[drive] = bytes;
            else
                _bytesByDrive.Remove(drive);
        }
        try
        {
            if (done)
                _journal.RemoveDownload(entry.Path);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            // Stays in the journal: released again (harmlessly) after the next restart.
        }
        try
        {
            _changed.Set();
        }
        catch (ObjectDisposedException)
        {
        }
        return true;
    }

    /// <summary>The downloads a previous run requested and did not release, registered as requested again so
    /// <see cref="Release"/> works for them. Call once, before anything is held.</summary>
    public IReadOnlyList<CloudDownload> Adopt()
    {
        var downloads = _journal.LoadDownloads();
        lock (_lock)
        {
            foreach (var download in downloads)
            {
                if (_active.ContainsKey(download.Path))
                    continue;
                _active[download.Path] = new Entry(download, null);
                var drive = DriveOf(download.Path);
                _bytesByDrive[drive] = _bytesByDrive.GetValueOrDefault(drive) + download.Size;
            }
        }
        return downloads;
    }

    public void Dispose() => _changed.Dispose();

    /// <summary>True when the content of <paramref name="path"/> is on this device (or the file is gone).</summary>
    internal bool HasArrived(string path)
    {
        try
        {
            var info = new FileInfo(path);
            return !info.Exists || !_cloud.IsOnlineOnly(path, info.Attributes);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return true; // unreadable: let the indexer decide what to do with it
        }
    }

    private long Budget(string path, long held)
    {
        var free = _freeBytes(path);
        if (free is not { } bytes)
            return 0;
        // Requested files that already arrived took some of the free space: count them back in.
        return (long)(_options.BudgetFraction * Math.Max(0, bytes + held));
    }

    private static string DriveOf(string path)
    {
        try
        {
            return Path.GetPathRoot(Path.GetFullPath(path)) ?? string.Empty;
        }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return string.Empty;
        }
    }

    private static long? DriveFreeBytes(string path)
    {
        try
        {
            var root = Path.GetPathRoot(Path.GetFullPath(path));
            return string.IsNullOrEmpty(root) ? null : new DriveInfo(root).AvailableFreeSpace;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }
}
