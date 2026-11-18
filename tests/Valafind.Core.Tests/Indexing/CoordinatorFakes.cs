using System.Collections.Concurrent;
using Valafind.Core.Abstractions;
using Valafind.Core.Indexing;

namespace Valafind.Core.Tests.Indexing;

internal sealed class FakeStore(StringComparer? comparer = null) : IIndexStore, IIndexedPathSource, IIndexedEntrySource
{
    private readonly Lock _lock = new();
    public readonly Dictionary<string, (FileEntry Entry, IReadOnlyList<ChunkRecord> Chunks)> Files = new(comparer ?? StringComparer.Ordinal);
    private readonly StringComparison _comparison =
        (comparer ?? StringComparer.Ordinal).Equals(StringComparer.Ordinal) ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
    public int PageRequests;
    public readonly ConcurrentDictionary<string, string> Meta = new(StringComparer.Ordinal);
    public readonly List<(string Op, string Path, IndexPhase? Phase)> Log = [];
    public string? ModelId;

    public void Open(string modelId, int dimensions) => ModelId = modelId;

    public FileFingerprint? GetFingerprint(string path)
    {
        lock (_lock)
            return Files.TryGetValue(path, out var f) ? new FileFingerprint(f.Entry.Size, f.Entry.ModifiedUtc) : null;
    }

    public void UpsertFile(FileEntry file, IReadOnlyList<ChunkRecord> chunks)
    {
        lock (_lock)
        {
            Files.Remove(file.Path); // like the real store: the newest spelling is kept
            Files[file.Path] = (file, chunks);
            Log.Add(("upsert", file.Path, file.Phase));
        }
    }

    public void RemoveFile(string path)
    {
        lock (_lock)
        {
            if (Files.Remove(path))
                Log.Add(("remove", path, null));
        }
    }

    public int RemoveUnder(string directoryPrefix)
    {
        lock (_lock)
        {
            var victims = Files.Keys.Where(k => k.StartsWith(directoryPrefix, StringComparison.Ordinal)).ToList();
            foreach (var v in victims)
            {
                Files.Remove(v);
                Log.Add(("remove", v, null));
            }
            return victims.Count;
        }
    }

    public IReadOnlyList<StoreHit> Search(ReadOnlySpan<float> query, SearchFilter filter, int topK) => [];

    public IndexStats GetStats()
    {
        lock (_lock)
            return new IndexStats(Files.Count, Files.Values.Sum(f => f.Chunks.Count),
                Files.Values.Count(f => f.Entry.Phase == IndexPhase.Frequent), Files.Values.Count(f => f.Entry.Phase == IndexPhase.All));
    }

    public IReadOnlyDictionary<FileKind, long> GetFileCountsByKind()
    {
        lock (_lock)
            return Files.Values.GroupBy(f => f.Entry.Kind).ToDictionary(g => g.Key, g => (long)g.Count());
    }

    public string? GetMeta(string key) => Meta.TryGetValue(key, out var v) ? v : null;

    public void SetMeta(string key, string value) => Meta[key] = value;

    public IReadOnlyList<IndexedPath> GetIndexedPaths(string? directory, string? afterKey, int limit)
    {
        Interlocked.Increment(ref PageRequests);
        lock (_lock)
        {
            string Key(string p) => _comparison == StringComparison.Ordinal ? p : p.ToLowerInvariant();
            var under = directory is null ? null : Path.TrimEndingDirectorySeparator(directory);
            return Files.Keys
                .Where(p => under is null || string.Equals(p, under, _comparison) ||
                            p.StartsWith(under + Path.DirectorySeparatorChar, _comparison))
                .Select(p => new IndexedPath(Key(p), p))
                .Where(e => afterKey is null || string.CompareOrdinal(e.Key, afterKey) > 0)
                .OrderBy(e => e.Key, StringComparer.Ordinal)
                .Take(limit)
                .ToList();
        }
    }

    /// <summary>Single-path lookups (<see cref="GetEntry"/>) and batch lookups (<see cref="GetEntries"/>) so far.</summary>
    public int EntryLookups, BatchLookups;

    public IndexedEntry? GetEntry(string path)
    {
        Interlocked.Increment(ref EntryLookups);
        return Find(path);
    }

    public IReadOnlyList<IndexedEntry?> GetEntries(IReadOnlyList<string> paths)
    {
        Interlocked.Increment(ref BatchLookups);
        return paths.Select(Find).ToList();
    }

    private IndexedEntry? Find(string path)
    {
        lock (_lock)
        {
            foreach (var (key, value) in Files)
            {
                if (string.Equals(key, path, _comparison))
                    return new IndexedEntry(key, value.Entry.Size, value.Entry.ModifiedUtc);
            }
            return null;
        }
    }

    public bool Contains(string path)
    {
        lock (_lock)
            return Files.ContainsKey(path);
    }

    public (FileEntry Entry, IReadOnlyList<ChunkRecord> Chunks) Get(string path)
    {
        lock (_lock)
            return Files[path];
    }

    public List<(string Op, string Path, IndexPhase? Phase)> Snapshot()
    {
        lock (_lock)
            return [.. Log];
    }

    public void Dispose()
    {
    }
}

/// <summary>Reads .txt/.md as text, reports images as name-only, everything else unreadable.</summary>
internal sealed class FakeReader : IDocumentReader
{
    public TimeSpan Delay { get; set; }
    public int Reads;
    private int _active;
    public int PeakConcurrentReads;

    public async Task<ExtractedText?> ReadAsync(string path, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref Reads);
        var active = Interlocked.Increment(ref _active);
        int peak;
        while ((peak = Volatile.Read(ref PeakConcurrentReads)) < active && Interlocked.CompareExchange(ref PeakConcurrentReads, active, peak) != peak)
        {
        }
        try
        {
            if (Delay > TimeSpan.Zero)
                await Task.Delay(Delay, cancellationToken);
        }
        finally
        {
            Interlocked.Decrement(ref _active);
        }
        var kind = FileKinds.Classify(path);
        if (kind == FileKind.Text)
            return new ExtractedText(await File.ReadAllTextAsync(path, cancellationToken), FileKind.Text);
        if (kind == FileKind.Image)
            return new ExtractedText(string.Empty, FileKind.Image);
        return null;
    }
}

internal sealed class FakeChunker(int size = 50) : ITextChunker
{
    public IReadOnlyList<TextChunk> Chunk(string text)
    {
        var chunks = new List<TextChunk>();
        for (int start = 0, i = 0; start < text.Length; start += size, i++)
        {
            var length = Math.Min(size, text.Length - start);
            chunks.Add(new TextChunk(i, start, length, text.Substring(start, length)));
        }
        return chunks;
    }
}

internal sealed class FakeModel(string id = "fake/4") : IEmbeddingModel
{
    public readonly ConcurrentQueue<IReadOnlyList<DocumentText>> Calls = new();
    public Func<DocumentText, bool>? FailWhen { get; set; }

    public string ModelId { get; } = id;
    public int Dimensions => 4;
    public int MaxInputTokens => 2048;
    public int CountTokens(string text) => text.Length / 4;
    public float[] EmbedQuery(string query, CancellationToken cancellationToken = default) => [1, 0, 0, 0];

    public IReadOnlyList<float[]> EmbedDocuments(IReadOnlyList<DocumentText> documents, CancellationToken cancellationToken = default)
    {
        Calls.Enqueue(documents);
        if (FailWhen is { } f && documents.Any(f))
            throw new InvalidOperationException("model failure");
        return documents.Select(d => new[] { d.Text.Length, d.Title.Length, 1f, 0f }).ToList();
    }

    public int EmbeddedDocuments => Calls.Sum(c => c.Count);

    public void Dispose()
    {
    }
}

internal sealed class FakeDiscovery(IReadOnlyList<string> roots, IReadOnlyList<string> frequent, IReadOnlyList<string> all) : IFileDiscovery
{
    public IReadOnlyList<string> Roots { get; } = roots;
    public bool IsExcluded(string path) => false;
    public IEnumerable<string> EnumerateFrequentFiles(CancellationToken cancellationToken) => frequent;

    public IEnumerable<string> EnumerateAllFiles(CancellationToken cancellationToken)
    {
        foreach (var path in all)
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return path;
        }
    }
}

/// <summary><see cref="FakeDiscovery"/> that also reports each file's size and mtime the way a directory listing does.</summary>
internal sealed class FakeStatDiscovery(IReadOnlyList<string> roots, IReadOnlyList<string> all) : IFileDiscovery, IFileStatDiscovery
{
    public IReadOnlyList<string> Roots { get; } = roots;
    public bool IsExcluded(string path) => false;
    public IEnumerable<string> EnumerateFrequentFiles(CancellationToken cancellationToken) => [];
    public IEnumerable<string> EnumerateAllFiles(CancellationToken cancellationToken) => all.Where(File.Exists);

    public IEnumerable<DiscoveredFile> EnumerateAllFilesWithStats(CancellationToken cancellationToken)
    {
        foreach (var path in EnumerateAllFiles(cancellationToken))
        {
            var info = new FileInfo(path);
            yield return new DiscoveredFile(path, info.Length, new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero));
        }
    }
}

/// <summary>Change history whose checkpoints are "cp-1", "cp-2", …; a replay reports <see cref="Changes"/> and
/// returns <see cref="CanReplay"/>.</summary>
internal sealed class FakeHistory : IChangeHistory
{
    private int _captures;
    public bool CanReplay { get; set; } = true;
    public List<(string Path, ChangeKind Kind)> Changes { get; } = [];
    public List<string> Replayed { get; } = [];
    public int Captures => Volatile.Read(ref _captures);

    public string? Capture(IReadOnlyList<string> roots) => "cp-" + Interlocked.Increment(ref _captures);

    public bool TryReplay(string checkpoint, IReadOnlyList<string> roots, Action<string, ChangeKind> onChange, CancellationToken cancellationToken)
    {
        lock (Replayed)
            Replayed.Add(checkpoint);
        foreach (var (path, kind) in Changes)
            onChange(path, kind);
        return CanReplay;
    }
}
