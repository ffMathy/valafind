using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Valafind.Core.Abstractions;
using Valafind.Core.Configuration;
using Valafind.Core.Platform;

namespace Valafind.Core.Indexing;

/// <summary>An indexed file as listed by <see cref="IIndexedPathSource"/>: its uniqueness key (the paging cursor)
/// and the path as stored.</summary>
public readonly record struct IndexedPath(string Key, string Path);

/// <summary>
/// Optional capability of an <see cref="IIndexStore"/>: listing indexed paths page by page. When the store implements
/// it, <see cref="IndexingCoordinator"/> removes entries for files that vanished while Valafind was not running, that
/// became excluded, or whose root was removed, and cleans up after watcher overflows.
/// </summary>
public interface IIndexedPathSource
{
    /// <summary>Returns at most <paramref name="limit"/> indexed files in ascending key order, starting after
    /// <paramref name="afterKey"/> (null = from the start). With <paramref name="directory"/>, only that path and
    /// paths below it (whole components) are listed. Paging never holds the whole list in memory and tolerates
    /// removals between pages.</summary>
    IReadOnlyList<IndexedPath> GetIndexedPaths(string? directory, string? afterKey, int limit);
}

/// <summary>What the store knows about one indexed file.</summary>
/// <param name="Path">The path as stored (its spelling may differ in case from a lookup on case-insensitive systems).</param>
/// <param name="Size">Size when indexed.</param>
/// <param name="ModifiedUtc">Last write time when indexed.</param>
public sealed record IndexedEntry(string Path, long Size, DateTimeOffset ModifiedUtc);

/// <summary>Optional capability of an <see cref="IIndexStore"/>: fingerprint lookup that also returns the stored
/// spelling, so case-only renames (Windows, macOS) are noticed and re-indexed under the new name.</summary>
public interface IIndexedEntrySource
{
    /// <summary>The entry <paramref name="path"/> resolves to (by the store's key rules), or null.</summary>
    IndexedEntry? GetEntry(string path);
}

/// <summary>
/// Drives indexing: Phase 1 (frequently / recently used files), Phase 2 (everything under the roots, discovered
/// lazily on a producer thread), then incremental updates from file-system watchers. All work runs on dedicated
/// low-priority threads and only while the <see cref="ThrottleGate"/> is open. Unchanged files are skipped by
/// (size, mtime) fingerprint, so restarts are cheap. The caller owns (and opens) the store, model and monitor.
/// </summary>
/// <remarks>
/// <para>Files are always indexed by kind, cheapest and most searched first: documents (everything that is not a
/// picture, audio or video), then pictures, then audio, then video (<see cref="IndexOrder"/>). Phase 1 indexes the
/// frequently used documents; Phase 2 indexes the remaining documents while it walks the disk and keeps the media it
/// finds for afterwards, then indexes pictures, audio and video in that order (frequently used ones first within each).
/// Changes reported by the file watchers during these passes follow the same rule: a new photo waits until the
/// documents are done.</para>
/// <para>With an <see cref="IMediaEmbeddingModel"/> and an <see cref="IMediaDecoder"/>, media are embedded by content
/// (a picture as one vector, audio as up to four 30-second clips, a video as its frames plus up to two clips of its
/// sound) next to a vector of the file's name; without them they are indexed by name only.</para>
/// </remarks>
public sealed class IndexingCoordinator : IIndexingService
{
    internal const string MetaPhase1Complete = "phase1_complete";
    internal const string MetaPhase1Count = "phase1_count";
    internal const string MetaPhase2Complete = "phase2_complete";
    internal const string MetaPhase2Count = "phase2_count";
    internal const string MetaLastFullScan = "phase2_last_full_scan_utc";
    internal const string MetaModelId = "indexing_model_id";
    internal const string MetaConfig = "indexing_config";

    /// <summary>FAT stores mtimes with 2 s resolution, SMB/exFAT and some tools round to whole seconds.</summary>
    private static readonly TimeSpan CoarseTimestampTolerance = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan MaxModelBackoff = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan RootRescanMinInterval = TimeSpan.FromMinutes(10);
    private const int DiscoveryGateEvery = 256;
    private const int DiscoveryChannelCapacity = 50_000;
    private const int SweepPage = 500;
    private const int ModelAttemptsPerBatch = 3;

    private readonly IFileDiscovery _discovery;
    private readonly IDocumentReader _reader;
    private readonly ITextChunker _chunker;
    private readonly IEmbeddingModel? _model;
    private readonly IMediaEmbeddingModel? _mediaModel;
    private readonly IMediaDecoder? _mediaDecoder;
    private readonly IIndexStore _store;
    private readonly ILogger<IndexingCoordinator> _logger;
    private readonly TimeProvider _time;
    private readonly CoordinatorTuning _tuning;
    private readonly ThrottleGate _gate;
    private readonly PendingChanges _pending;
    private readonly AutoResetEvent _pendingSignal = new(false);
    private readonly List<IDisposable> _watchers = [];
    private readonly Dictionary<string, DateTimeOffset> _lastRootRescan;
    private readonly StringComparer _pathComparer;
    private readonly Lock _lifecycleLock = new();
    private readonly Lock _statusLock = new();
    private readonly ITimer _statusTimer;
    private readonly ValafindSettings _settings;
    private readonly Lock _interactiveLock = new();
    private readonly ManualResetEventSlim _notInteractive = new(true);
    private bool _interactive;
    private long _interactiveSince;

    private CancellationTokenSource? _cts;
    private Task? _worker;
    private bool _disposed;

    // Progress (written by the worker / producer threads).
    private Activity _activity = Activity.Starting;
    private IndexPhase _phase = IndexPhase.Frequent;
    private long _frequentDone, _frequentTotal, _allDone, _allTotal;
    private volatile bool _discoveryComplete;
    private string? _currentFile;
    private int _consecutiveModelFailures;
    private bool _mediaReleased = true;

    // Kind order (IndexOrder): the rank the running pass has reached; files of a higher rank found meanwhile wait in
    // _deferred. NotOrdering when no pass is running (watcher changes are then handled as they come).
    private const int NotOrdering = int.MaxValue;
    private volatile int _stage = NotOrdering;
    private readonly DeferredFiles _deferred;
    private HashSet<string>? _frequentMedia;
    private bool _sweepUnsupportedLogged;
    private int _watchLimitLogged;

    private IndexStatus _status = IndexStatus.Initial;
    private bool _raisePending;
    private long _lastRaiseTimestamp;

    private enum Activity
    {
        Starting,
        Working,
        Idle,
    }

    /// <summary>Creates the coordinator. Nothing runs until <see cref="StartAsync"/>.</summary>
    /// <param name="discovery">Finds candidate files.</param>
    /// <param name="reader">Extracts text; returns null for unreadable files.</param>
    /// <param name="chunker">Splits text into model-sized chunks.</param>
    /// <param name="model">Embedding model, or null when it could not be loaded (status becomes <see cref="IndexingState.ModelUnavailable"/>).</param>
    /// <param name="store">Already opened index store.</param>
    /// <param name="monitor">System pressure source for throttling (not disposed by the coordinator).</param>
    /// <param name="settings">Thresholds, roots and pause state; thresholds are re-read live.</param>
    /// <param name="logger">Logger.</param>
    /// <param name="time">Clock; defaults to <see cref="TimeProvider.System"/>.</param>
    /// <param name="mediaDecoder">Decodes pictures, audio and video when <paramref name="model"/> can embed them; null:
    /// media are indexed by name only.</param>
    public IndexingCoordinator(IFileDiscovery discovery, IDocumentReader reader, ITextChunker chunker, IEmbeddingModel? model,
        IIndexStore store, ISystemPressureMonitor monitor, ValafindSettings settings, ILogger<IndexingCoordinator> logger,
        TimeProvider? time = null, IMediaDecoder? mediaDecoder = null)
        : this(discovery, reader, chunker, model, store, monitor, settings, logger, time, CoordinatorTuning.Default, mediaDecoder)
    {
    }

    internal IndexingCoordinator(IFileDiscovery discovery, IDocumentReader reader, ITextChunker chunker, IEmbeddingModel? model,
        IIndexStore store, ISystemPressureMonitor monitor, ValafindSettings settings, ILogger<IndexingCoordinator> logger,
        TimeProvider? time, CoordinatorTuning tuning, IMediaDecoder? mediaDecoder = null)
    {
        ArgumentNullException.ThrowIfNull(discovery);
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentNullException.ThrowIfNull(chunker);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(monitor);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(logger);
        _discovery = discovery;
        _reader = reader;
        _chunker = chunker;
        _model = model;
        _mediaModel = model as IMediaEmbeddingModel;
        _mediaDecoder = mediaDecoder;
        _store = store;
        _logger = logger;
        _time = time ?? TimeProvider.System;
        _tuning = tuning;
        _settings = settings;
        _pathComparer = OperatingSystem.IsLinux() ? StringComparer.Ordinal : StringComparer.OrdinalIgnoreCase;
        _deferred = new DeferredFiles(_pathComparer);
        _pending = new PendingChanges(_time, tuning.Debounce, _pathComparer, path => FindRoot(path), tuning.PendingCapacity);
        _lastRootRescan = new Dictionary<string, DateTimeOffset>(_pathComparer);
        _gate = new ThrottleGate(monitor, () => tuning.Throttle ?? ThrottleOptions.From(settings), _time);
        _gate.SetUserPaused(settings.UserPaused);
        _gate.Changed += (_, _) => Publish();
        _statusTimer = _time.CreateTimer(_ => RaiseDeferred(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        if (model is null)
            _status = IndexStatus.Initial with { State = IndexingState.ModelUnavailable };
    }

    /// <inheritdoc />
    public IndexStatus Status
    {
        get { lock (_statusLock) return _status; }
    }

    /// <inheritdoc />
    public event EventHandler<IndexStatus>? StatusChanged;

    /// <summary>The throttle gate (exposed for diagnostics).</summary>
    internal ThrottleGate Gate => _gate;

    /// <inheritdoc />
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (_model is null)
        {
            IndexingLog.ModelUnavailable(_logger);
            Publish();
            return;
        }

        CancellationToken token;
        lock (_lifecycleLock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_cts is not null)
                return;
            _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            token = _cts.Token;
        }

        await _gate.StartAsync(token).ConfigureAwait(false);
        var worker = Task.Factory.StartNew(() => RunWorker(token), token, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        lock (_lifecycleLock)
            _worker = worker;
    }

    /// <inheritdoc />
    public void SetUserPaused(bool paused) => _gate.SetUserPaused(paused);

    /// <inheritdoc />
    /// <remarks>While active, the worker finishes the model batch in flight (at most <see cref="CoordinatorTuning.BatchSize"/>
    /// documents) and then waits before reading the next file or starting the next batch, like a closed gate but
    /// without changing <see cref="Status"/>. It resumes as soon as this is set back to false, or after
    /// <see cref="CoordinatorTuning.InteractiveTimeout"/> if the flag is never cleared (e.g. the launcher was left open).</remarks>
    public void SetInteractive(bool active)
    {
        lock (_interactiveLock)
        {
            if (_disposed)
                return;
            _interactive = active;
            if (active)
            {
                _interactiveSince = _time.GetTimestamp();
                _notInteractive.Reset();
            }
            else
            {
                _notInteractive.Set();
            }
        }
    }

    /// <summary>Blocks while the user is searching (bounded by the interactive timeout).</summary>
    /// <returns>True when it waited.</returns>
    private bool WaitWhileInteractive(CancellationToken cancellationToken)
    {
        var waited = false;
        while (true)
        {
            TimeSpan remaining;
            lock (_interactiveLock)
            {
                if (!_interactive)
                    return waited;
                remaining = _tuning.InteractiveTimeout - _time.GetElapsedTime(_interactiveSince);
                if (remaining <= TimeSpan.Zero)
                    return waited; // left on too long: indexing must not starve
            }
            waited = true;
            WaitHandle.WaitAny([cancellationToken.WaitHandle, _notInteractive.WaitHandle], remaining);
            cancellationToken.ThrowIfCancellationRequested();
        }
    }

    /// <summary>Waits until the gate is open and the user is not searching.</summary>
    private void WaitForTurn(CancellationToken cancellationToken)
    {
        do
        {
            WaitForGate(cancellationToken);
        }
        while (WaitWhileInteractive(cancellationToken));
    }

    public void OverrideThrottle(TimeSpan duration) =>
        _gate.OverrideUntil = duration > TimeSpan.Zero ? _time.GetUtcNow() + duration : null;

    // ------------------------------------------------------------------ worker

    private void RunWorker(CancellationToken cancellationToken)
    {
        using var priority = BackgroundPriority.EnterForCurrentThread();
        try
        {
            PrepareMeta();
            StartWatchers();
            RunPhase1(cancellationToken);
            RunPhase2(cancellationToken);
            RunIncremental(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception e)
        {
            IndexingLog.WorkerFailed(_logger, e);
        }
    }

    private void PrepareMeta()
    {
        var modelId = _model!.ModelId;
        var storedModel = TryGetMeta(MetaModelId);
        var reset = !string.Equals(storedModel, modelId, StringComparison.Ordinal);
        if (!reset)
        {
            try
            {
                reset = _store.GetStats().Files == 0;
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                IndexingLog.StatsFailed(_logger, e);
            }
        }
        if (reset)
        {
            // New model (vectors were dropped) or empty store: earlier phase markers no longer mean anything.
            TrySetMeta(MetaPhase1Complete, "0");
            TrySetMeta(MetaPhase2Complete, "0");
            TrySetMeta(MetaModelId, modelId);
        }

        // Roots / excludes changed (Settings restarts the core): the files now in scope must be walked now, not at the
        // next daily scan, and entries that fell out of scope are swept at the end of that walk.
        var config = ConfigurationFingerprint();
        var storedConfig = TryGetMeta(MetaConfig);
        if (storedConfig is not null && !string.Equals(storedConfig, config, StringComparison.Ordinal))
        {
            IndexingLog.ConfigurationChanged(_logger);
            TrySetMeta(MetaPhase2Complete, "0");
        }
        if (!string.Equals(storedConfig, config, StringComparison.Ordinal))
            TrySetMeta(MetaConfig, config);
    }

    /// <summary>Hash of everything that decides which files are in scope: the effective roots and user excludes.</summary>
    internal string ConfigurationFingerprint()
    {
        var text = new StringBuilder();
        foreach (var root in _discovery.Roots.Order(StringComparer.Ordinal))
            text.Append("root:").Append(root).Append('\n');
        foreach (var exclude in _settings.Excludes.Where(e => !string.IsNullOrWhiteSpace(e)).Select(e => e.Trim()).Order(StringComparer.Ordinal))
            text.Append("exclude:").Append(exclude).Append('\n');
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text.ToString())));
    }

    private void RunPhase1(CancellationToken cancellationToken)
    {
        if (TryGetMeta(MetaPhase1Complete) == "1")
        {
            var count = ParseLong(TryGetMeta(MetaPhase1Count));
            Interlocked.Exchange(ref _frequentTotal, count);
            Interlocked.Exchange(ref _frequentDone, count);
            return;
        }

        _phase = IndexPhase.Frequent;
        SetActivity(Activity.Working);
        WaitForGate(cancellationToken);

        List<string> frequent;
        try
        {
            frequent = _discovery.EnumerateFrequentFiles(cancellationToken).ToList();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception e)
        {
            IndexingLog.FrequentFailed(_logger, e);
            frequent = [];
        }

        // Documents only: frequently used pictures, audio and video come right after the documents of Phase 2 (first
        // within their kind), so no media is embedded before every document is.
        _frequentMedia = new HashSet<string>(frequent.Where(p => IndexOrder.Rank(p) > IndexOrder.Documents), _pathComparer);
        frequent = frequent.Where(p => IndexOrder.Rank(p) == IndexOrder.Documents).ToList();
        Interlocked.Exchange(ref _frequentTotal, frequent.Count);
        Interlocked.Exchange(ref _frequentDone, 0);
        IndexingLog.Phase1Started(_logger, frequent.Count);
        Publish();
        _stage = IndexOrder.Documents;
        try
        {
            foreach (var path in frequent)
            {
                cancellationToken.ThrowIfCancellationRequested();
                DrainPending(cancellationToken);
                ProcessFile(path, IndexPhase.Frequent, cancellationToken);
                Interlocked.Increment(ref _frequentDone);
                Publish();
            }
        }
        finally
        {
            _stage = NotOrdering;
        }

        TrySetMeta(MetaPhase1Count, frequent.Count.ToString(CultureInfo.InvariantCulture));
        TrySetMeta(MetaPhase1Complete, "1");
    }

    private void RunPhase2(CancellationToken cancellationToken)
    {
        var complete = TryGetMeta(MetaPhase2Complete) == "1";
        if (complete)
        {
            // Show the last completed totals; a due re-walk below runs quietly in the background.
            var count = ParseLong(TryGetMeta(MetaPhase2Count));
            Interlocked.Exchange(ref _allTotal, count);
            Interlocked.Exchange(ref _allDone, count);
            _discoveryComplete = true;
            if (!IsFullScanDue())
                return;
        }
        RunFullScan(complete ? IndexPhase.Incremental : IndexPhase.All, cancellationToken);
    }

    private bool IsFullScanDue()
    {
        var last = TryGetMeta(MetaLastFullScan);
        if (!DateTimeOffset.TryParse(last, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var at))
            return true;
        return _time.GetUtcNow() - at >= _tuning.FullRescanInterval;
    }

    /// <summary>Walks every root (producer thread) and indexes what it finds (this thread). Phase 2 publishes its
    /// progress; a periodic re-walk (<paramref name="entryPhase"/> = Incremental) keeps showing the last completed
    /// totals and only replaces them when it finishes, so the UI does not look like the index is being rebuilt.</summary>
    private void RunFullScan(IndexPhase entryPhase, CancellationToken cancellationToken)
    {
        var visible = entryPhase != IndexPhase.Incremental;
        long walked = 0, done = 0;
        if (visible)
        {
            _phase = IndexPhase.All;
            Interlocked.Exchange(ref _allDone, 0);
            Interlocked.Exchange(ref _allTotal, 0);
            _discoveryComplete = false;
        }
        else
        {
            _phase = IndexPhase.Incremental;
        }
        _gate.SetIdle(false);
        SetActivity(Activity.Working);
        IndexingLog.Phase2Started(_logger, _discovery.Roots.Count);

        var channel = Channel.CreateBounded<string>(new BoundedChannelOptions(DiscoveryChannelCapacity)
        {
            SingleReader = true,
            SingleWriter = true,
            FullMode = BoundedChannelFullMode.Wait,
        });
        var walkSucceeded = false;
        var producer = Task.Factory.StartNew(() =>
        {
            using var priority = BackgroundPriority.EnterForCurrentThread();
            try
            {
                WaitForGate(cancellationToken);
                var n = 0;
                foreach (var path in _discovery.EnumerateAllFiles(cancellationToken))
                {
                    if (++n % DiscoveryGateEvery == 0)
                        WaitForGate(cancellationToken);
                    if (!channel.Writer.TryWrite(path))
                        channel.Writer.WriteAsync(path, cancellationToken).AsTask().GetAwaiter().GetResult();
                    Interlocked.Increment(ref walked);
                    if (visible)
                    {
                        Interlocked.Increment(ref _allTotal);
                        Publish();
                    }
                }
                walkSucceeded = true;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
            }
            catch (Exception e)
            {
                IndexingLog.DiscoveryFailed(_logger, e);
            }
            finally
            {
                channel.Writer.TryComplete();
                if (visible)
                {
                    _discoveryComplete = true;
                    Publish();
                }
            }
        }, cancellationToken, TaskCreationOptions.LongRunning, TaskScheduler.Default);

        // Documents as they are found; pictures, audio and video afterwards, in that order (IndexOrder). Files the
        // watchers deferred before this pass started stay queued.
        _stage = IndexOrder.Documents;
        try
        {
            try
            {
                while (channel.Reader.WaitToReadAsync(cancellationToken).AsTask().GetAwaiter().GetResult())
                {
                    while (channel.Reader.TryRead(out var path))
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        DrainPending(cancellationToken);
                        var rank = IndexOrder.Rank(path);
                        if (rank > IndexOrder.Documents)
                        {
                            _deferred.Add(rank, path, entryPhase);
                            continue;
                        }

                        ProcessFile(path, entryPhase, cancellationToken);
                        done++;
                        if (visible)
                        {
                            Interlocked.Increment(ref _allDone);
                            Publish();
                        }
                    }
                }
            }
            finally
            {
                try
                {
                    producer.GetAwaiter().GetResult();
                }
                catch (OperationCanceledException)
                {
                }
            }
            cancellationToken.ThrowIfCancellationRequested();

            for (var rank = IndexOrder.Images; rank <= IndexOrder.Videos; rank++)
            {
                _stage = rank;
                // Frequently used files first within their kind; files the watchers add meanwhile join the end.
                _deferred.PromoteFirst(rank, _frequentMedia);
                while (_deferred.TryTake(rank, out var path, out var phase))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    DrainPending(cancellationToken);
                    ProcessFile(path, phase, cancellationToken);
                    if (phase != entryPhase)
                        continue; // a watcher change, not part of the walk's count
                    done++;
                    if (visible)
                    {
                        Interlocked.Increment(ref _allDone);
                        Publish();
                    }
                }
            }
        }
        finally
        {
            _stage = NotOrdering;
        }
        _frequentMedia = null;
        cancellationToken.ThrowIfCancellationRequested();

        var total = Interlocked.Read(ref walked);
        if (!visible)
        {
            Interlocked.Exchange(ref _allTotal, total);
            Interlocked.Exchange(ref _allDone, done);
            _discoveryComplete = true;
            Publish();
        }
        if (walkSucceeded)
        {
            TrySetMeta(MetaPhase2Count, total.ToString(CultureInfo.InvariantCulture));
            TrySetMeta(MetaPhase2Complete, "1");
            TrySetMeta(MetaLastFullScan, _time.GetUtcNow().ToString("O", CultureInfo.InvariantCulture));
            SweepStale(null, cancellationToken);
        }
    }

    /// <summary>Removes index entries (all, or those under <paramref name="directory"/>) whose file vanished, became
    /// excluded or is no longer under any root. Pages through the store, so memory stays flat for any index size.
    /// Entries under a root that is currently unavailable (unmounted drive, offline share) are kept: their files are
    /// most likely still there and would otherwise all be re-embedded when the volume comes back.
    /// Requires <see cref="IIndexedPathSource"/>.</summary>
    private void SweepStale(string? directory, CancellationToken cancellationToken)
    {
        if (_store is not IIndexedPathSource source)
        {
            if (!_sweepUnsupportedLogged)
            {
                _sweepUnsupportedLogged = true;
                IndexingLog.SweepUnsupported(_logger);
            }
            return;
        }

        var rootAvailable = new Dictionary<string, bool>(_pathComparer);
        string? after = null;
        var removed = 0;
        while (true)
        {
            WaitForGate(cancellationToken);
            if (directory is null)
                DrainPending(cancellationToken); // not when called from a change handler (no re-entrancy)
            IReadOnlyList<IndexedPath> page;
            try
            {
                page = source.GetIndexedPaths(directory, after, SweepPage);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                IndexingLog.ListPathsFailed(_logger, e);
                return;
            }
            if (page.Count == 0)
                break;
            foreach (var entry in page)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (IsStale(entry.Path, rootAvailable) && TryStore(() => _store.RemoveFile(entry.Path), "remove"))
                    removed++;
            }
            after = page[^1].Key;
            if (page.Count < SweepPage)
                break;
        }
        if (removed > 0)
            IndexingLog.StaleRemoved(_logger, removed);
    }

    private bool IsStale(string path, Dictionary<string, bool> rootAvailable)
    {
        try
        {
            var root = FindRoot(path);
            if (root is null)
                return true; // root removed from the settings
            if (!rootAvailable.TryGetValue(root, out var available))
                rootAvailable[root] = available = IsRootAvailable(root);
            if (!available)
                return false;
            return !File.Exists(path) || _discovery.IsExcluded(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return false;
        }
    }

    /// <summary>A root counts as available when it exists and is not empty: an unmounted mount point (or a
    /// disconnected network drive) shows up as a missing or empty directory.</summary>
    private static bool IsRootAvailable(string root)
    {
        try
        {
            return Directory.Exists(root) && Directory.EnumerateFileSystemEntries(root).Any();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return false;
        }
    }

    /// <summary>The longest root containing <paramref name="path"/> (whole components), or null.</summary>
    private string? FindRoot(string path)
    {
        string? best = null;
        var comparison = OperatingSystem.IsLinux() ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        foreach (var root in _discovery.Roots)
        {
            var trimmed = Path.TrimEndingDirectorySeparator(root);
            if (!path.StartsWith(trimmed, comparison))
                continue;
            var under = path.Length == trimmed.Length || Path.EndsInDirectorySeparator(trimmed) ||
                        path[trimmed.Length] == Path.DirectorySeparatorChar || path[trimmed.Length] == Path.AltDirectorySeparatorChar;
            if (under && (best is null || root.Length > best.Length))
                best = root;
        }
        return best;
    }

    /// <summary>True when <paramref name="path"/> is itself a root.</summary>
    private static bool IsRoot(string path, string root) =>
        string.Equals(Path.TrimEndingDirectorySeparator(root), Path.TrimEndingDirectorySeparator(path),
            OperatingSystem.IsLinux() ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Guard for live watcher events (Delete, or a changed path that no longer exists): true when
    /// <paramref name="path"/> is a root or its root no longer exists. Unmounting a volume or losing a share does not
    /// raise per-file deletes, so the "empty root" heuristic of <see cref="IsRootAvailable"/> is deliberately not used
    /// here: deleting the last entry of a root leaves it empty, and that delete must still be applied.
    /// </summary>
    private bool IsRootOrUnderMissingRoot(string path)
    {
        var root = FindRoot(path);
        if (root is null)
            return false;
        if (IsRoot(path, root))
            return true;
        try
        {
            return !Directory.Exists(root);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return true;
        }
    }

    /// <summary>
    /// Guard for rescans of a directory that is gone (watcher overflow, or a collapsed root rescan): true when
    /// <paramref name="path"/> is a root or lies under a root that looks unmounted (missing or empty).
    /// </summary>
    private bool IsRootOrOnUnavailableRoot(string path)
    {
        var root = FindRoot(path);
        if (root is null)
            return false;
        return IsRoot(path, root) || !IsRootAvailable(root);
    }

    private void RunIncremental(CancellationToken cancellationToken)
    {
        _phase = IndexPhase.Incremental;
        var handles = new[] { cancellationToken.WaitHandle, _pendingSignal };
        while (!cancellationToken.IsCancellationRequested)
        {
            if (_pending.Count == 0)
            {
                SetActivity(Activity.Idle);
                _gate.SetIdle(true);
                ReleaseMediaModel();
            }

            var wait = _pending.NextDueIn() ?? _tuning.IdlePoll;
            if (wait > _tuning.IdlePoll)
                wait = _tuning.IdlePoll;
            if (wait > TimeSpan.Zero)
                WaitHandle.WaitAny(handles, wait);
            cancellationToken.ThrowIfCancellationRequested();

            DrainPending(cancellationToken);

            if (IsFullScanDue())
            {
                RunFullScan(IndexPhase.Incremental, cancellationToken);
                _phase = IndexPhase.Incremental;
            }
        }
    }

    /// <summary>Handles every watcher change whose debounce elapsed.</summary>
    private void DrainPending(CancellationToken cancellationToken)
    {
        var due = _pending.TakeDue();
        if (due.Count == 0)
            return;
        // Same order as the passes: deletes and rescans first, then documents, pictures, audio, video.
        due = due.OrderBy(c => c.Kind is ChangeKind.Delete or ChangeKind.Rescan ? -1 : IndexOrder.Rank(c.Path)).ToList();
        var previous = _activity;
        _gate.SetIdle(false);
        SetActivity(Activity.Working);
        foreach (var change in due)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                HandleChange(change, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception e)
            {
                IndexingLog.ChangeFailed(_logger, e, change.Kind, change.Path);
            }
        }
        if (previous != Activity.Working)
            SetActivity(previous);
    }

    private void HandleChange(DueChange change, CancellationToken cancellationToken)
    {
        switch (change.Kind)
        {
            case ChangeKind.Delete:
                // A root "deleted" is usually a volume going away: keep its entries (see SweepStale).
                // Deleting the last entry of a root empties it; that delete still applies.
                if (!IsRootOrUnderMissingRoot(change.Path))
                    RemovePath(change.Path);
                break;
            case ChangeKind.Rescan:
                RescanDirectory(change.Path, cancellationToken);
                SweepStale(change.Path, cancellationToken); // events were lost: deletes too
                break;
            default:
                if (Directory.Exists(change.Path))
                {
                    // Changed on a directory only means an entry inside it changed (Windows raises it for every
                    // create / delete / rename below); those entries have events of their own.
                    if (change.Kind == ChangeKind.Upsert && !_discovery.IsExcluded(change.Path))
                        RescanDirectory(change.Path, cancellationToken);
                }
                else if (File.Exists(change.Path))
                {
                    if (_discovery.IsExcluded(change.Path))
                        RemovePath(change.Path);
                    else
                        ProcessOrDefer(change.Path, cancellationToken);
                }
                else if (!IsRootOrUnderMissingRoot(change.Path))
                {
                    RemovePath(change.Path);
                }
                break;
        }
    }

    /// <summary>Indexes a file a watcher reported, unless a pass is still on an earlier kind: then it waits its turn.</summary>
    private void ProcessOrDefer(string path, CancellationToken cancellationToken)
    {
        var rank = IndexOrder.Rank(path);
        if (rank > _stage)
            _deferred.Add(rank, path, IndexPhase.Incremental);
        else
            ProcessFile(path, IndexPhase.Incremental, cancellationToken);
    }

    private void RemovePath(string path)
    {
        TryStore(() => _store.RemoveFile(path), "remove");
        var prefix = Path.EndsInDirectorySeparator(path) ? path : path + Path.DirectorySeparatorChar;
        TryStore(() => _store.RemoveUnder(prefix), "remove directory");
    }

    private void RescanDirectory(string directory, CancellationToken cancellationToken)
    {
        if (!Directory.Exists(directory))
        {
            if (!IsRootOrOnUnavailableRoot(directory))
                RemovePath(directory);
            return;
        }
        IEnumerable<string> files = _discovery is UserFileDiscovery user
            ? user.EnumerateUnder(directory, cancellationToken)
            : GenericWalk(directory, cancellationToken);
        var n = 0;
        var media = new List<string>();
        foreach (var file in files)
        {
            if (++n % DiscoveryGateEvery == 0)
                WaitForGate(cancellationToken);
            if (IndexOrder.Rank(file) > IndexOrder.Documents)
                media.Add(file); // after the documents of this directory
            else
                ProcessFile(file, IndexPhase.Incremental, cancellationToken);
        }

        foreach (var file in media.OrderBy(IndexOrder.Rank))
            ProcessOrDefer(file, cancellationToken);
    }

    /// <summary>Walk for third-party <see cref="IFileDiscovery"/> implementations: breadth-first, no links, honouring IsExcluded.</summary>
    private IEnumerable<string> GenericWalk(string directory, CancellationToken cancellationToken)
    {
        var options = new EnumerationOptions
        {
            IgnoreInaccessible = true,
            RecurseSubdirectories = false,
            AttributesToSkip = FileAttributes.ReparsePoint | FileAttributes.System | FileAttributes.Offline | FileAttributes.Device,
        };
        var queue = new Queue<string>();
        if (!_discovery.IsExcluded(directory))
            queue.Enqueue(directory);
        while (queue.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var current = queue.Dequeue();
            List<string> files = [], subdirectories = [];
            try
            {
                files.AddRange(Directory.EnumerateFiles(current, "*", options));
                subdirectories.AddRange(Directory.EnumerateDirectories(current, "*", options));
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
            }
            foreach (var file in files)
            {
                if (!_discovery.IsExcluded(file))
                    yield return file;
            }
            foreach (var sub in subdirectories)
            {
                if (!_discovery.IsExcluded(sub))
                    queue.Enqueue(sub);
            }
        }
    }

    // ------------------------------------------------------------------ per-file pipeline

    /// <summary>Indexes one file if it changed since it was last indexed. Never throws except for cancellation.</summary>
    /// <returns>True when the file is (now) up to date in the index.</returns>
    internal bool ProcessFile(string path, IndexPhase phase, CancellationToken cancellationToken)
    {
        SetCurrentFile(path);
        long size;
        DateTimeOffset modified;
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists)
            {
                TryStore(() => _store.RemoveFile(path), "remove");
                return false;
            }
            size = info.Length;
            modified = new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            IndexingLog.StatFailed(_logger, e, path);
            return false;
        }

        FileFingerprint? fingerprint = null;
        string? storedPath = null;
        try
        {
            if (_store is IIndexedEntrySource entries)
            {
                if (entries.GetEntry(path) is { } entry)
                {
                    fingerprint = new FileFingerprint(entry.Size, entry.ModifiedUtc);
                    storedPath = entry.Path;
                }
            }
            else
            {
                fingerprint = _store.GetFingerprint(path);
            }
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            IndexingLog.FingerprintFailed(_logger, e, path);
        }

        if (size == 0)
        {
            if (fingerprint is not null)
                TryStore(() => _store.RemoveFile(path), "remove");
            return false;
        }
        // A case-only rename (Windows, macOS) keeps size and mtime: re-index so the stored path and the name that is
        // part of every embedding follow the new spelling.
        var renamed = storedPath is not null && IsDifferentSpelling(storedPath, path);
        if (fingerprint is not null && !renamed && IsUnchanged(fingerprint, size, modified))
            return true;

        WaitForTurn(cancellationToken);

        ExtractedText? extracted;
        try
        {
            extracted = _reader.ReadAsync(path, cancellationToken).GetAwaiter().GetResult();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception e)
        {
            IndexingLog.ReadFailed(_logger, e, path);
            extracted = null;
        }

        var kind = extracted?.Kind ?? FileKinds.Classify(path);
        if (extracted is null && kind == FileKind.Other)
        {
            // Unknown, unreadable file: not worth a name-only entry.
            if (fingerprint is not null)
                TryStore(() => _store.RemoveFile(path), "remove");
            return false;
        }

        IReadOnlyList<TextChunk> chunks = [];
        if (extracted is not null && !string.IsNullOrWhiteSpace(extracted.Text))
        {
            try
            {
                chunks = _chunker.Chunk(extracted.Text);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                IndexingLog.ChunkFailed(_logger, e, path);
                chunks = [];
            }
        }

        var title = Path.GetFileName(path);
        List<DocumentText> documents;
        if (chunks.Count == 0)
        {
            chunks = [new TextChunk(0, 0, 0, string.Empty)];
            documents = [new DocumentText(title, NameOnlyText(path))];
        }
        else
        {
            documents = chunks.Select(c => new DocumentText(title, c.Text)).ToList();
        }

        var vectors = new List<float[]>(documents.Count);
        for (var start = 0; start < documents.Count; start += _tuning.BatchSize)
        {
            // Between batches: yield to system pressure and to interactive search. Before the first one only to search
            // (the gate was checked right before extraction; a file already read is finished rather than held).
            if (start > 0)
                WaitForTurn(cancellationToken);
            else
                WaitWhileInteractive(cancellationToken);
            var batch = documents.GetRange(start, Math.Min(_tuning.BatchSize, documents.Count - start));
            var embedded = EmbedWithBackoff(batch, path, cancellationToken);
            if (embedded is null)
                return false;
            vectors.AddRange(embedded);
        }

        var records = new List<ChunkRecord>(chunks.Count);
        for (var i = 0; i < chunks.Count; i++)
            records.Add(new ChunkRecord(chunks[i].Index, chunks[i].Start, chunks[i].Length, chunks[i].Text, vectors[i]));

        // Pictures, audio and video: vectors of their content next to the name vector.
        if (kind is FileKind.Image or FileKind.Audio or FileKind.Video && (extracted is null || string.IsNullOrWhiteSpace(extracted.Text)))
        {
            foreach (var media in EmbedMedia(path, kind, cancellationToken))
                records.Add(media with { Index = records.Count });
        }

        return TryStore(() => _store.UpsertFile(new FileEntry(path, size, modified, kind, phase), records), "write");
    }

    /// <summary>Same size and same mtime. A difference below 2 s is tolerated only when one side has whole-second
    /// resolution (FAT, exFAT, SMB, some copy tools); on NTFS / ext4 / APFS any mtime change is a real edit.</summary>
    internal static bool IsUnchanged(FileFingerprint stored, long size, DateTimeOffset modified)
    {
        if (stored.Size != size)
            return false;
        var a = stored.ModifiedUtc.UtcTicks;
        var b = modified.UtcTicks;
        if (a == b)
            return true;
        var coarse = a % TimeSpan.TicksPerSecond == 0 || b % TimeSpan.TicksPerSecond == 0;
        return coarse && Math.Abs(a - b) < CoarseTimestampTolerance.Ticks;
    }

    /// <summary>True when the stored spelling differs from <paramref name="path"/> other than by Unicode normalisation
    /// (macOS APIs hand out NFC or NFD for the same name).</summary>
    internal static bool IsDifferentSpelling(string storedPath, string path)
    {
        string current;
        try
        {
            current = Path.GetFullPath(path);
        }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
        if (string.Equals(storedPath, current, StringComparison.Ordinal))
            return false;
        try
        {
            return !string.Equals(storedPath.Normalize(NormalizationForm.FormC), current.Normalize(NormalizationForm.FormC), StringComparison.Ordinal);
        }
        catch (ArgumentException)
        {
            return true;
        }
    }

    /// <summary>Name-only embedding text: path relative to its root with separators shown as " / ".</summary>
    internal string NameOnlyText(string path)
    {
        string relative = path;
        string? bestRoot = null;
        foreach (var root in _discovery.Roots)
        {
            var prefix = Path.EndsInDirectorySeparator(root) ? root : root + Path.DirectorySeparatorChar;
            if (path.StartsWith(prefix, OperatingSystem.IsLinux() ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase) &&
                (bestRoot is null || root.Length > bestRoot.Length))
                bestRoot = root;
        }
        if (bestRoot is not null)
            relative = Path.GetRelativePath(bestRoot, path);
        var parts = relative.Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries);
        return string.Join(" / ", parts);
    }

    /// <summary>
    /// Content vectors of a picture (one), an audio file (one per clip) or a video (one for its frames, one per clip of
    /// its sound). Chunk text stays empty; for clips <see cref="ChunkRecord.Start"/> and <see cref="ChunkRecord.Length"/>
    /// hold the clip's position in whole seconds. Decoding or embedding failures leave the file with its name vector.
    /// </summary>
    private List<ChunkRecord> EmbedMedia(string path, FileKind kind, CancellationToken cancellationToken)
    {
        var records = new List<ChunkRecord>();
        var model = _mediaModel;
        var decoder = _mediaDecoder;
        if (model is null || decoder is null)
            return records;
        var capabilities = model.MediaCapabilities;
        try
        {
            switch (kind)
            {
                case FileKind.Image when capabilities.HasFlag(MediaCapabilities.Image):
                {
                    WaitForTurn(cancellationToken);
                    var image = decoder.DecodeImageAsync(path, _tuning.ImageMaxSide, cancellationToken).GetAwaiter().GetResult();
                    if (image is not null)
                        records.Add(MediaRecord(EmbedMediaPart(() => model.EmbedImage(image, cancellationToken), path, cancellationToken), 0, 0));
                    break;
                }
                case FileKind.Audio when capabilities.HasFlag(MediaCapabilities.Audio):
                    AddClips(_tuning.MaxAudioClips);
                    break;
                case FileKind.Video:
                    if (capabilities.HasFlag(MediaCapabilities.Video))
                    {
                        WaitForTurn(cancellationToken);
                        var frames = decoder.DecodeVideoAsync(path, _tuning.MaxVideoFrames, _tuning.VideoMaxSide, cancellationToken).GetAwaiter().GetResult();
                        if (frames is { Frames.Count: > 0 })
                            records.Add(MediaRecord(EmbedMediaPart(() => model.EmbedVideo(frames.Frames, cancellationToken), path, cancellationToken), 0, (int)frames.Duration.TotalSeconds));
                    }

                    if (capabilities.HasFlag(MediaCapabilities.Audio))
                        AddClips(_tuning.MaxVideoAudioClips);
                    break;
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception e)
        {
            IndexingLog.MediaFailed(_logger, e, path);
        }

        records.RemoveAll(r => r.Vector.Length == 0);
        return records;

        void AddClips(int maxClips)
        {
            WaitForTurn(cancellationToken);
            var clips = decoder.DecodeAudioAsync(path, maxClips, cancellationToken).GetAwaiter().GetResult();
            foreach (var clip in clips ?? [])
            {
                WaitForTurn(cancellationToken);
                records.Add(MediaRecord(EmbedMediaPart(() => model.EmbedAudio(clip, cancellationToken), path, cancellationToken),
                    (int)clip.Offset.TotalSeconds, (int)Math.Ceiling((double)clip.Samples.Length / AudioClip.SampleRate)));
            }
        }
    }

    private static ChunkRecord MediaRecord(float[] vector, int startSeconds, int lengthSeconds) =>
        new(0, startSeconds, lengthSeconds, string.Empty, vector);

    /// <summary>One media embedding; an empty vector when it failed (logged).</summary>
    private float[] EmbedMediaPart(Func<float[]> embed, string path, CancellationToken cancellationToken)
    {
        WaitWhileInteractive(cancellationToken);
        try
        {
            _mediaReleased = false;
            var vector = embed();
            return vector.Length == _model!.Dimensions ? vector : [];
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception e)
        {
            IndexingLog.MediaFailed(_logger, e, path);
            return [];
        }
    }

    /// <summary>Frees the media encoders' memory once indexing has nothing left to do.</summary>
    private void ReleaseMediaModel()
    {
        if (_mediaReleased || _mediaModel is null)
            return;
        _mediaReleased = true;
        try
        {
            _mediaModel.ReleaseMediaResources();
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            IndexingLog.MediaFailed(_logger, e, "(release)");
        }
    }

    private IReadOnlyList<float[]>? EmbedWithBackoff(List<DocumentText> batch, string path, CancellationToken cancellationToken)
    {
        for (var attempt = 1; ; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var vectors = _model!.EmbedDocuments(batch, cancellationToken);
                if (vectors.Count != batch.Count)
                    throw new InvalidOperationException($"Model returned {vectors.Count} vectors for {batch.Count} inputs");
                _consecutiveModelFailures = 0;
                return vectors;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception e)
            {
                var failures = ++_consecutiveModelFailures;
                if (failures == 1 || failures % 20 == 0)
                    IndexingLog.EmbedFailedWarning(_logger, e, path, failures);
                else
                    IndexingLog.EmbedFailed(_logger, e, path);

                var delay = TimeSpan.FromSeconds(Math.Min(MaxModelBackoff.TotalSeconds, Math.Pow(2, Math.Min(failures, 16)) * _tuning.ModelBackoffUnit.TotalSeconds));
                if (delay > TimeSpan.Zero && cancellationToken.WaitHandle.WaitOne(delay))
                    cancellationToken.ThrowIfCancellationRequested();
                if (attempt >= ModelAttemptsPerBatch)
                    return null;
            }
        }
    }

    private void WaitForGate(CancellationToken cancellationToken)
    {
        if (_gate.IsOpen)
            return;
        Publish();
        try
        {
            _gate.WaitUntilOpenAsync(cancellationToken).GetAwaiter().GetResult();
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // Gate disposed while waiting: treat as shutdown.
            throw new OperationCanceledException(cancellationToken);
        }
        Publish();
    }

    // ------------------------------------------------------------------ watchers

    /// <summary>
    /// Watches every root. Linux uses <see cref="InotifyTreeWatcher"/>, which places watches only on directories that
    /// are indexed (a recursive <see cref="FileSystemWatcher"/> puts one on every subdirectory, node_modules, .git and
    /// ~/.cache included, and can exhaust the per-user limit shared with IDEs and sync clients). Windows
    /// (ReadDirectoryChangesW) and macOS (FSEvents) watch a whole tree with one handle at no per-directory cost, so
    /// they keep <see cref="FileSystemWatcher"/>; excluded paths are dropped by a string check before queueing.
    /// </summary>
    private void StartWatchers()
    {
        foreach (var root in _discovery.Roots)
        {
            try
            {
                if (!Directory.Exists(root))
                    continue;
                IDisposable watcher = (OperatingSystem.IsLinux() ? StartInotify(root) : null) ?? (IDisposable)StartFileSystemWatcher(root);
                lock (_watchers)
                    _watchers.Add(watcher);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or PlatformNotSupportedException)
            {
                IndexingLog.WatchFailed(_logger, e, root);
            }
        }
    }

    [System.Runtime.Versioning.SupportedOSPlatform("linux")]
    private InotifyTreeWatcher? StartInotify(string root)
    {
        Func<string, string, bool> isExcludedDirectory = _discovery is UserFileDiscovery user
            ? (directory, parent) => user.Rules.IsExcludedDirectory(directory, parent)
            : (directory, _) => _discovery.IsExcluded(directory);
        InotifyTreeWatcher watcher;
        try
        {
            watcher = new InotifyTreeWatcher(root, isExcludedDirectory,
                (path, kind) => OnWatcherEvent(path, kind switch
                {
                    TreeEventKind.Created => ChangeKind.Upsert,
                    TreeEventKind.Changed => ChangeKind.Changed,
                    _ => ChangeKind.Delete,
                }),
                problem => OnWatchProblem(root, problem));
        }
        catch (PlatformNotSupportedException e)
        {
            IndexingLog.InotifyUnavailable(_logger, e);
            return null;
        }
        watcher.Start();
        return watcher;
    }

    private FileSystemWatcher StartFileSystemWatcher(string root)
    {
        var watcher = new FileSystemWatcher(root)
        {
            IncludeSubdirectories = true,
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size,
            InternalBufferSize = 64 * 1024,
        };
        try
        {
            watcher.Created += (_, e) => OnWatcherEvent(e.FullPath, ChangeKind.Upsert);
            watcher.Changed += (_, e) => OnWatcherEvent(e.FullPath, ChangeKind.Changed);
            watcher.Deleted += (_, e) => OnWatcherEvent(e.FullPath, ChangeKind.Delete);
            watcher.Renamed += (_, e) =>
            {
                OnWatcherEvent(e.OldFullPath, ChangeKind.Delete);
                OnWatcherEvent(e.FullPath, ChangeKind.Upsert);
            };
            watcher.Error += (_, e) => OnWatcherError(root, watcher, e.GetException());
            watcher.EnableRaisingEvents = true;
            return watcher;
        }
        catch
        {
            watcher.Dispose();
            throw;
        }
    }

    /// <summary>Test hook: the inotify watchers (Linux) once their initial watches are in place.</summary>
    internal async Task<int> WaitForWatchersAsync()
    {
        List<IDisposable> watchers;
        lock (_watchers)
            watchers = [.. _watchers];
        var count = 0;
        foreach (var watcher in watchers)
        {
            if (OperatingSystem.IsLinux() && watcher is InotifyTreeWatcher inotify)
            {
                await inotify.Ready.ConfigureAwait(false);
                count += inotify.WatchCount;
            }
        }
        return count;
    }

    internal void OnWatcherEvent(string path, ChangeKind kind)
    {
        try
        {
            if (_discovery is UserFileDiscovery user && user.IsExcludedByNameOnly(path))
                return;
            _pending.Add(path, kind);
            _gate.SetIdle(false);
            _pendingSignal.Set();
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private void OnWatchProblem(string root, TreeWatchProblem problem)
    {
        if (problem == TreeWatchProblem.WatchLimitReached)
        {
            // Not an event loss: some directories are simply not watched. Rescanning the root would not fix that;
            // the periodic full scan picks up their changes.
            if (Interlocked.Exchange(ref _watchLimitLogged, 1) == 0)
                IndexingLog.WatchLimitReached(_logger, root);
            return;
        }
        ScheduleRootRescan(root, null);
    }

    private void OnWatcherError(string root, FileSystemWatcher watcher, Exception? error)
    {
        if (!ScheduleRootRescan(root, error))
            return;
        try
        {
            // Overflow keeps the watcher running; other errors may have stopped it.
            if (!watcher.EnableRaisingEvents)
                watcher.EnableRaisingEvents = true;
        }
        catch (Exception e) when (e is IOException or ObjectDisposedException or ArgumentException or PlatformNotSupportedException)
        {
            IndexingLog.WatcherRestartFailed(_logger, e, root);
        }
    }

    /// <summary>Events under <paramref name="root"/> were lost: re-walk it (fingerprints make that a stat per file)
    /// and sweep entries that vanished. At most once per <see cref="RootRescanMinInterval"/> per root.</summary>
    /// <returns>False when rate-limited.</returns>
    private bool ScheduleRootRescan(string root, Exception? error)
    {
        var now = _time.GetUtcNow();
        lock (_lastRootRescan)
        {
            if (_lastRootRescan.TryGetValue(root, out var last) && now - last < RootRescanMinInterval)
                return false;
            _lastRootRescan[root] = now;
        }
        IndexingLog.WatcherError(_logger, error, root);
        OnWatcherEvent(root, ChangeKind.Rescan);
        return true;
    }

    // ------------------------------------------------------------------ status

    private void SetActivity(Activity activity)
    {
        _activity = activity;
        if (activity != Activity.Working)
            _currentFile = null;
        Publish();
    }

    private void SetCurrentFile(string path)
    {
        _currentFile = path;
        Publish();
    }

    private IndexStatus BuildStatus()
    {
        IndexingState state;
        if (_model is null)
            state = IndexingState.ModelUnavailable;
        else
        {
            state = _activity switch
            {
                Activity.Starting => IndexingState.Starting,
                Activity.Idle => _gate.UserPaused ? IndexingState.PausedByUser : IndexingState.UpToDate,
                _ => _gate.State,
            };
        }
        return new IndexStatus(state, _phase,
            Interlocked.Read(ref _frequentDone), Interlocked.Read(ref _frequentTotal),
            Interlocked.Read(ref _allDone), Interlocked.Read(ref _allTotal),
            _discoveryComplete, _gate.LastSample, _activity == Activity.Working ? _currentFile : null);
    }

    /// <summary>Updates <see cref="Status"/> and raises <see cref="StatusChanged"/> at most every StatusInterval.</summary>
    private void Publish()
    {
        if (_disposed)
            return;
        var status = BuildStatus();
        bool raiseNow;
        lock (_statusLock)
        {
            _status = status;
            if (_raisePending)
                return;
            var since = _time.GetElapsedTime(_lastRaiseTimestamp);
            if (_lastRaiseTimestamp == 0 || since >= _tuning.StatusInterval)
            {
                _lastRaiseTimestamp = _time.GetTimestamp();
                raiseNow = true;
            }
            else
            {
                _raisePending = true;
                raiseNow = false;
                try
                {
                    _statusTimer.Change(_tuning.StatusInterval - since, Timeout.InfiniteTimeSpan);
                }
                catch (ObjectDisposedException)
                {
                }
            }
        }
        if (raiseNow)
            Raise(status);
    }

    private void RaiseDeferred()
    {
        IndexStatus status;
        lock (_statusLock)
        {
            if (!_raisePending)
                return;
            _raisePending = false;
            _lastRaiseTimestamp = _time.GetTimestamp();
            status = _status;
        }
        Raise(status);
    }

    private void Raise(IndexStatus status)
    {
        try
        {
            StatusChanged?.Invoke(this, status);
        }
        catch (Exception e)
        {
            IndexingLog.StatusHandlerFailed(_logger, e);
        }
    }

    // ------------------------------------------------------------------ store helpers

    private bool TryStore(Action action, string operation)
    {
        try
        {
            action();
            return true;
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            IndexingLog.StoreFailed(_logger, e, operation);
            return false;
        }
    }

    private string? TryGetMeta(string key)
    {
        try
        {
            return _store.GetMeta(key);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            IndexingLog.MetaFailed(_logger, e, key);
            return null;
        }
    }

    private void TrySetMeta(string key, string value) => TryStore(() => _store.SetMeta(key, value), "meta write");

    private static long ParseLong(string? value) =>
        long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) && n >= 0 ? n : 0;

    // ------------------------------------------------------------------ disposal

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        Task? worker;
        CancellationTokenSource? cts;
        lock (_lifecycleLock)
        {
            if (_disposed)
                return;
            _disposed = true;
            worker = _worker;
            cts = _cts;
        }

        lock (_watchers)
        {
            foreach (var watcher in _watchers)
            {
                try
                {
                    if (watcher is FileSystemWatcher fsw)
                        fsw.EnableRaisingEvents = false;
                    watcher.Dispose();
                }
                catch (Exception e) when (e is IOException or ObjectDisposedException)
                {
                }
            }
            _watchers.Clear();
        }

        if (cts is not null)
            await cts.CancelAsync().ConfigureAwait(false);
        if (worker is not null)
        {
            try
            {
                await worker.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception e)
            {
                IndexingLog.ShutdownFault(_logger, e);
            }
        }

        _gate.Dispose();
        await _statusTimer.DisposeAsync().ConfigureAwait(false);
        _pendingSignal.Dispose();
        lock (_interactiveLock)
            _notInteractive.Dispose();
        cts?.Dispose();
    }
}

/// <summary>Timing knobs; tests shrink them.</summary>
internal sealed record CoordinatorTuning(
    TimeSpan Debounce,
    TimeSpan StatusInterval,
    TimeSpan IdlePoll,
    TimeSpan FullRescanInterval,
    TimeSpan ModelBackoffUnit,
    int BatchSize,
    ThrottleOptions? Throttle)
{
    /// <summary>Longest time <see cref="IndexingCoordinator.SetInteractive"/>(true) holds indexing back.</summary>
    public TimeSpan InteractiveTimeout { get; init; } = TimeSpan.FromMinutes(2);

    /// <summary>Pending watcher paths kept individually before they collapse into root rescans.</summary>
    public int PendingCapacity { get; init; } = PendingChanges.DefaultCapacity;

    /// <summary>30-second clips embedded per audio file (consecutive when they cover it, else spread over it).</summary>
    public int MaxAudioClips { get; init; } = 4;

    /// <summary>30-second clips of a video's sound embedded next to its frames.</summary>
    public int MaxVideoAudioClips { get; init; } = 2;

    /// <summary>Frames decoded per video (one per second, thinned out evenly).</summary>
    public int MaxVideoFrames { get; init; } = 8;

    /// <summary>Pictures are decoded at most this large (the model resizes them to its patch budget anyway).</summary>
    public int ImageMaxSide { get; init; } = 2048;

    /// <summary>Video frames are decoded at most this large.</summary>
    public int VideoMaxSide { get; init; } = 1280;

    /// <summary>Batch size 2: a model batch cannot be cancelled once started, and two chunks take about a second on
    /// a laptop CPU, which bounds how long a query can wait behind indexing.</summary>
    public static CoordinatorTuning Default { get; } = new(
        Debounce: TimeSpan.FromSeconds(2),
        StatusInterval: TimeSpan.FromMilliseconds(250),
        IdlePoll: TimeSpan.FromMinutes(1),
        FullRescanInterval: TimeSpan.FromHours(24),
        ModelBackoffUnit: TimeSpan.FromSeconds(1),
        BatchSize: 2,
        Throttle: null);
}

internal static partial class IndexingLog
{
    [LoggerMessage(1, LogLevel.Warning, "Embedding model unavailable; indexing disabled")]
    public static partial void ModelUnavailable(ILogger logger);

    [LoggerMessage(2, LogLevel.Error, "Indexing worker stopped unexpectedly")]
    public static partial void WorkerFailed(ILogger logger, Exception exception);

    [LoggerMessage(3, LogLevel.Debug, "Could not read index statistics")]
    public static partial void StatsFailed(ILogger logger, Exception exception);

    [LoggerMessage(4, LogLevel.Warning, "Could not enumerate frequently used files")]
    public static partial void FrequentFailed(ILogger logger, Exception exception);

    [LoggerMessage(5, LogLevel.Information, "Phase 1: {Count} frequently used files")]
    public static partial void Phase1Started(ILogger logger, int count);

    [LoggerMessage(6, LogLevel.Information, "Phase 2: scanning {RootCount} root(s)")]
    public static partial void Phase2Started(ILogger logger, int rootCount);

    [LoggerMessage(7, LogLevel.Warning, "File discovery failed")]
    public static partial void DiscoveryFailed(ILogger logger, Exception exception);

    [LoggerMessage(8, LogLevel.Debug, "Index store cannot list paths; stale entries are only removed via file watching")]
    public static partial void SweepUnsupported(ILogger logger);

    [LoggerMessage(9, LogLevel.Debug, "Could not list indexed paths")]
    public static partial void ListPathsFailed(ILogger logger, Exception exception);

    [LoggerMessage(10, LogLevel.Information, "Removed {Count} stale index entries")]
    public static partial void StaleRemoved(ILogger logger, int count);

    [LoggerMessage(11, LogLevel.Debug, "Failed to handle change {Kind} {Path}")]
    public static partial void ChangeFailed(ILogger logger, Exception exception, ChangeKind kind, string path);

    [LoggerMessage(12, LogLevel.Debug, "Cannot stat {Path}")]
    public static partial void StatFailed(ILogger logger, Exception exception, string path);

    [LoggerMessage(13, LogLevel.Debug, "Cannot read fingerprint of {Path}")]
    public static partial void FingerprintFailed(ILogger logger, Exception exception, string path);

    [LoggerMessage(14, LogLevel.Debug, "Cannot read {Path}")]
    public static partial void ReadFailed(ILogger logger, Exception exception, string path);

    [LoggerMessage(15, LogLevel.Debug, "Cannot chunk {Path}")]
    public static partial void ChunkFailed(ILogger logger, Exception exception, string path);

    [LoggerMessage(16, LogLevel.Warning, "Embedding failed for {Path} ({Failures} consecutive failures)")]
    public static partial void EmbedFailedWarning(ILogger logger, Exception exception, string path, int failures);

    [LoggerMessage(17, LogLevel.Debug, "Embedding failed for {Path}")]
    public static partial void EmbedFailed(ILogger logger, Exception exception, string path);

    [LoggerMessage(18, LogLevel.Warning, "Cannot watch {Root}; changes will be picked up by periodic rescans")]
    public static partial void WatchFailed(ILogger logger, Exception exception, string root);

    [LoggerMessage(19, LogLevel.Warning, "File watcher error for {Root}; scheduling a rescan")]
    public static partial void WatcherError(ILogger logger, Exception? exception, string root);

    [LoggerMessage(20, LogLevel.Debug, "Could not restart watcher for {Root}")]
    public static partial void WatcherRestartFailed(ILogger logger, Exception exception, string root);

    [LoggerMessage(21, LogLevel.Debug, "StatusChanged handler threw")]
    public static partial void StatusHandlerFailed(ILogger logger, Exception exception);

    [LoggerMessage(22, LogLevel.Warning, "Index store {Operation} failed")]
    public static partial void StoreFailed(ILogger logger, Exception exception, string operation);

    [LoggerMessage(23, LogLevel.Debug, "Cannot read index meta {Key}")]
    public static partial void MetaFailed(ILogger logger, Exception exception, string key);

    [LoggerMessage(24, LogLevel.Debug, "Indexing worker faulted during shutdown")]
    public static partial void ShutdownFault(ILogger logger, Exception exception);

    [LoggerMessage(25, LogLevel.Information, "Index roots or excludes changed; re-walking all roots")]
    public static partial void ConfigurationChanged(ILogger logger);

    [LoggerMessage(26, LogLevel.Warning, "inotify watch limit reached under {Root}; some folders are not watched and are refreshed by the daily scan (raise fs.inotify.max_user_watches to watch all)")]
    public static partial void WatchLimitReached(ILogger logger, string root);

    [LoggerMessage(27, LogLevel.Debug, "inotify unavailable; falling back to FileSystemWatcher")]
    public static partial void InotifyUnavailable(ILogger logger, Exception exception);

    [LoggerMessage(28, LogLevel.Debug, "Embedding the content of {Path} failed; it stays findable by name")]
    public static partial void MediaFailed(ILogger logger, Exception exception, string path);
}
