using System.Collections.Concurrent;
using Valafind.Core.Abstractions;
using Valafind.Core.Indexing;

namespace Valafind.Core.Tests.Indexing;

internal sealed class FakeStore : IIndexStore, IIndexedPathSource
{
    private readonly Lock _lock = new();
    public readonly Dictionary<string, (FileEntry Entry, IReadOnlyList<ChunkRecord> Chunks)> Files = new(StringComparer.Ordinal);
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

    public string? GetMeta(string key) => Meta.TryGetValue(key, out var v) ? v : null;

    public void SetMeta(string key, string value) => Meta[key] = value;

    public IReadOnlyList<string> GetIndexedPaths()
    {
        lock (_lock)
            return Files.Keys.ToList();
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

    public async Task<ExtractedText?> ReadAsync(string path, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref Reads);
        if (Delay > TimeSpan.Zero)
            await Task.Delay(Delay, cancellationToken);
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
