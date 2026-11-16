using System.Globalization;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Valafind.Core.Abstractions;
using Valafind.Core.Configuration;
using Valafind.Core.Platform;

namespace Valafind.Core.Indexing;

/// <summary>
/// Optional capability of an <see cref="IIndexStore"/>: listing every indexed path. When the store implements it,
/// <see cref="IndexingCoordinator"/> removes entries for files that vanished while Valafind was not running.
/// </summary>
public interface IIndexedPathSource
{
    /// <summary>Returns a snapshot of all indexed file paths.</summary>
    IReadOnlyList<string> GetIndexedPaths();
}

/// <summary>
/// Drives indexing: Phase 1 (frequently / recently used files), Phase 2 (everything under the roots, discovered
/// lazily on a producer thread), then incremental updates from file-system watchers. All work runs on dedicated
/// low-priority threads and only while the <see cref="ThrottleGate"/> is open. Unchanged files are skipped by
/// (size, mtime) fingerprint, so restarts are cheap. The caller owns (and opens) the store, model and monitor.
/// </summary>
public sealed class IndexingCoordinator : IIndexingService
{
    internal const string MetaPhase1Complete = "phase1_complete";
    internal const string MetaPhase1Count = "phase1_count";
    internal const string MetaPhase2Complete = "phase2_complete";
    internal const string MetaPhase2Count = "phase2_count";
    internal const string MetaLastFullScan = "phase2_last_full_scan_utc";
    internal const string MetaModelId = "indexing_model_id";

    private static readonly TimeSpan FingerprintTolerance = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan MaxModelBackoff = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan RootRescanMinInterval = TimeSpan.FromMinutes(10);
    private const int DiscoveryGateEvery = 256;
    private const int DiscoveryChannelCapacity = 50_000;
    private const int SweepBatch = 500;
    private const int ModelAttemptsPerBatch = 3;

    private readonly IFileDiscovery _discovery;
    private readonly IDocumentReader _reader;
    private readonly ITextChunker _chunker;
    private readonly IEmbeddingModel? _model;
    private readonly IIndexStore _store;
    private readonly ILogger<IndexingCoordinator> _logger;
    private readonly TimeProvider _time;
    private readonly CoordinatorTuning _tuning;
    private readonly ThrottleGate _gate;
    private readonly PendingChanges _pending;
    private readonly AutoResetEvent _pendingSignal = new(false);
    private readonly List<FileSystemWatcher> _watchers = [];
    private readonly Dictionary<string, DateTimeOffset> _lastRootRescan;
    private readonly StringComparer _pathComparer;
    private readonly Lock _lifecycleLock = new();
    private readonly Lock _statusLock = new();
    private readonly ITimer _statusTimer;

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
    private bool _sweepUnsupportedLogged;

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
    public IndexingCoordinator(IFileDiscovery discovery, IDocumentReader reader, ITextChunker chunker, IEmbeddingModel? model,
        IIndexStore store, ISystemPressureMonitor monitor, ValafindSettings settings, ILogger<IndexingCoordinator> logger,
        TimeProvider? time = null)
        : this(discovery, reader, chunker, model, store, monitor, settings, logger, time, CoordinatorTuning.Default)
    {
    }

    internal IndexingCoordinator(IFileDiscovery discovery, IDocumentReader reader, ITextChunker chunker, IEmbeddingModel? model,
        IIndexStore store, ISystemPressureMonitor monitor, ValafindSettings settings, ILogger<IndexingCoordinator> logger,
        TimeProvider? time, CoordinatorTuning tuning)
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
        _store = store;
        _logger = logger;
        _time = time ?? TimeProvider.System;
        _tuning = tuning;
        _pathComparer = OperatingSystem.IsLinux() ? StringComparer.Ordinal : StringComparer.OrdinalIgnoreCase;
        _pending = new PendingChanges(_time, tuning.Debounce, _pathComparer);
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

        Interlocked.Exchange(ref _frequentTotal, frequent.Count);
        Interlocked.Exchange(ref _frequentDone, 0);
        IndexingLog.Phase1Started(_logger, frequent.Count);
        Publish();
        foreach (var path in frequent)
        {
            cancellationToken.ThrowIfCancellationRequested();
            DrainPending(cancellationToken);
            ProcessFile(path, IndexPhase.Frequent, cancellationToken);
            Interlocked.Increment(ref _frequentDone);
            Publish();
        }

        TrySetMeta(MetaPhase1Count, frequent.Count.ToString(CultureInfo.InvariantCulture));
        TrySetMeta(MetaPhase1Complete, "1");
    }

    private void RunPhase2(CancellationToken cancellationToken)
    {
        var complete = TryGetMeta(MetaPhase2Complete) == "1";
        if (complete && !IsFullScanDue())
        {
            var count = ParseLong(TryGetMeta(MetaPhase2Count));
            Interlocked.Exchange(ref _allTotal, count);
            Interlocked.Exchange(ref _allDone, count);
            _discoveryComplete = true;
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

    /// <summary>Walks every root (producer thread) and indexes what it finds (this thread).</summary>
    private void RunFullScan(IndexPhase entryPhase, CancellationToken cancellationToken)
    {
        _phase = IndexPhase.All;
        Interlocked.Exchange(ref _allDone, 0);
        Interlocked.Exchange(ref _allTotal, 0);
        _discoveryComplete = false;
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
                    Interlocked.Increment(ref _allTotal);
                    Publish();
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
                _discoveryComplete = true;
                Publish();
            }
        }, cancellationToken, TaskCreationOptions.LongRunning, TaskScheduler.Default);

        try
        {
            while (channel.Reader.WaitToReadAsync(cancellationToken).AsTask().GetAwaiter().GetResult())
            {
                while (channel.Reader.TryRead(out var path))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    DrainPending(cancellationToken);
                    ProcessFile(path, entryPhase, cancellationToken);
                    Interlocked.Increment(ref _allDone);
                    Publish();
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

        if (walkSucceeded)
        {
            TrySetMeta(MetaPhase2Count, Interlocked.Read(ref _allTotal).ToString(CultureInfo.InvariantCulture));
            TrySetMeta(MetaPhase2Complete, "1");
            TrySetMeta(MetaLastFullScan, _time.GetUtcNow().ToString("O", CultureInfo.InvariantCulture));
            SweepStale(cancellationToken);
        }
    }

    /// <summary>Removes index entries whose file vanished or became excluded (requires <see cref="IIndexedPathSource"/>).</summary>
    private void SweepStale(CancellationToken cancellationToken)
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

        IReadOnlyList<string> paths;
        try
        {
            paths = source.GetIndexedPaths();
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            IndexingLog.ListPathsFailed(_logger, e);
            return;
        }

        var removed = 0;
        for (var i = 0; i < paths.Count; i++)
        {
            if (i % SweepBatch == 0)
            {
                WaitForGate(cancellationToken);
                DrainPending(cancellationToken);
            }
            var path = paths[i];
            bool gone;
            try
            {
                gone = !File.Exists(path) || _discovery.IsExcluded(path);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                continue;
            }
            if (gone && TryStore(() => _store.RemoveFile(path), "remove"))
                removed++;
        }
        if (removed > 0)
            IndexingLog.StaleRemoved(_logger, removed);
    }

    private void RunIncremental(CancellationToken cancellationToken)
    {
        _phase = IndexPhase.Incremental;
        var handles = new[] { cancellationToken.WaitHandle, _pendingSignal };
        while (!cancellationToken.IsCancellationRequested)
        {
            if (_pending.Count == 0)
                SetActivity(Activity.Idle);

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
        var previous = _activity;
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
                RemovePath(change.Path);
                break;
            case ChangeKind.Rescan:
                RescanDirectory(change.Path, cancellationToken);
                break;
            default:
                if (Directory.Exists(change.Path))
                {
                    if (!_discovery.IsExcluded(change.Path))
                        RescanDirectory(change.Path, cancellationToken);
                }
                else if (File.Exists(change.Path))
                {
                    if (_discovery.IsExcluded(change.Path))
                        RemovePath(change.Path);
                    else
                        ProcessFile(change.Path, IndexPhase.Incremental, cancellationToken);
                }
                else
                {
                    RemovePath(change.Path);
                }
                break;
        }
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
            RemovePath(directory);
            return;
        }
        IEnumerable<string> files = _discovery is UserFileDiscovery user
            ? user.EnumerateUnder(directory, cancellationToken)
            : GenericWalk(directory, cancellationToken);
        var n = 0;
        foreach (var file in files)
        {
            if (++n % DiscoveryGateEvery == 0)
                WaitForGate(cancellationToken);
            ProcessFile(file, IndexPhase.Incremental, cancellationToken);
        }
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
        try
        {
            fingerprint = _store.GetFingerprint(path);
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
        if (fingerprint is not null && fingerprint.Size == size &&
            (fingerprint.ModifiedUtc - modified).Duration() < FingerprintTolerance)
            return true;

        WaitForGate(cancellationToken);

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
            if (start > 0)
                WaitForGate(cancellationToken);
            var batch = documents.GetRange(start, Math.Min(_tuning.BatchSize, documents.Count - start));
            var embedded = EmbedWithBackoff(batch, path, cancellationToken);
            if (embedded is null)
                return false;
            vectors.AddRange(embedded);
        }

        var records = new List<ChunkRecord>(chunks.Count);
        for (var i = 0; i < chunks.Count; i++)
            records.Add(new ChunkRecord(chunks[i].Index, chunks[i].Start, chunks[i].Length, chunks[i].Text, vectors[i]));

        return TryStore(() => _store.UpsertFile(new FileEntry(path, size, modified, kind, phase), records), "write");
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

    private void StartWatchers()
    {
        foreach (var root in _discovery.Roots)
        {
            try
            {
                if (!Directory.Exists(root))
                    continue;
                var watcher = new FileSystemWatcher(root)
                {
                    IncludeSubdirectories = true,
                    NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size,
                    InternalBufferSize = 64 * 1024,
                };
                watcher.Created += (_, e) => OnWatcherEvent(e.FullPath, ChangeKind.Upsert);
                watcher.Changed += (_, e) => OnWatcherEvent(e.FullPath, ChangeKind.Upsert);
                watcher.Deleted += (_, e) => OnWatcherEvent(e.FullPath, ChangeKind.Delete);
                watcher.Renamed += (_, e) =>
                {
                    OnWatcherEvent(e.OldFullPath, ChangeKind.Delete);
                    OnWatcherEvent(e.FullPath, ChangeKind.Upsert);
                };
                var capturedRoot = root;
                watcher.Error += (_, e) => OnWatcherError(capturedRoot, watcher, e.GetException());
                watcher.EnableRaisingEvents = true;
                lock (_watchers)
                    _watchers.Add(watcher);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or PlatformNotSupportedException)
            {
                IndexingLog.WatchFailed(_logger, e, root);
            }
        }
    }

    private void OnWatcherEvent(string path, ChangeKind kind)
    {
        try
        {
            if (_discovery is UserFileDiscovery user && user.IsExcludedByNameOnly(path))
                return;
            _pending.Add(path, kind);
            _pendingSignal.Set();
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private void OnWatcherError(string root, FileSystemWatcher watcher, Exception? error)
    {
        var now = _time.GetUtcNow();
        lock (_lastRootRescan)
        {
            if (_lastRootRescan.TryGetValue(root, out var last) && now - last < RootRescanMinInterval)
                return;
            _lastRootRescan[root] = now;
        }
        IndexingLog.WatcherError(_logger, error, root);
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
        OnWatcherEvent(root, ChangeKind.Rescan);
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
                    watcher.EnableRaisingEvents = false;
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
    public static CoordinatorTuning Default { get; } = new(
        Debounce: TimeSpan.FromSeconds(2),
        StatusInterval: TimeSpan.FromMilliseconds(250),
        IdlePoll: TimeSpan.FromMinutes(1),
        FullRescanInterval: TimeSpan.FromHours(24),
        ModelBackoffUnit: TimeSpan.FromSeconds(1),
        BatchSize: 8,
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
}
