using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using Valafind.Core.Abstractions;

namespace Valafind.Core.Tests.Search;

/// <summary>
/// Deterministic bag-of-words "embedding": each lower-cased word is hashed into a signed bucket, then the vector is
/// L2-normalised. Texts sharing words have positive cosine similarity; no model files needed.
/// </summary>
internal sealed class HashingEmbeddingModel : IEmbeddingModel
{
    private int _queryCalls;
    private int _documentCalls;

    public HashingEmbeddingModel(int dimensions = 64) => Dimensions = dimensions;

    public string ModelId => $"hashing/{Dimensions}";

    public int Dimensions { get; }

    public int MaxInputTokens => 2048;

    public int QueryCalls => _queryCalls;

    public int DocumentCalls => _documentCalls;

    public ConcurrentQueue<IReadOnlyList<DocumentText>> DocumentBatches { get; } = new();

    /// <summary>Runs inside EmbedQuery before the vector is returned (e.g. to cancel or block).</summary>
    public Action<CancellationToken>? OnEmbedQuery { get; set; }

    /// <summary>Runs inside EmbedDocuments (e.g. to throw or block).</summary>
    public Action<CancellationToken>? OnEmbedDocuments { get; set; }

    public int CountTokens(string text) => text.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;

    public float[] EmbedQuery(string query, CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref _queryCalls);
        OnEmbedQuery?.Invoke(cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        return Embed(query);
    }

    public IReadOnlyList<float[]> EmbedDocuments(IReadOnlyList<DocumentText> documents, CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref _documentCalls);
        DocumentBatches.Enqueue(documents.ToList());
        OnEmbedDocuments?.Invoke(cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        return documents.Select(d => Embed(d.Text)).ToList();
    }

    public float[] Embed(string text)
    {
        var v = new float[Dimensions];
        foreach (var word in text.ToLowerInvariant().Split((char[])[' ', '.', ',', '!', '?', '\n', '\t', ';', ':'], StringSplitOptions.RemoveEmptyEntries))
        {
            var hash = SHA256.HashData(Encoding.UTF8.GetBytes(word));
            var bucket = BitConverter.ToUInt32(hash, 0) % (uint)Dimensions;
            v[bucket] += (hash[4] & 1) == 0 ? 1f : -1f;
        }
        var norm = MathF.Sqrt(v.Sum(x => x * x));
        if (norm == 0)
        {
            v[0] = 1;
            return v;
        }
        for (var i = 0; i < v.Length; i++)
            v[i] /= norm;
        return v;
    }

    public void Dispose()
    {
    }
}

/// <summary>Store returning a fixed hit list.</summary>
internal sealed class FixedStore(params StoreHit[] hits) : IIndexStore
{
    private int _searches;

    public int Searches => _searches;

    public IReadOnlyList<StoreHit> Search(ReadOnlySpan<float> query, SearchFilter filter, int topK)
    {
        Interlocked.Increment(ref _searches);
        return hits.OrderByDescending(h => h.Score).Take(topK).ToList();
    }

    public void Open(string modelId, int dimensions) { }
    public FileFingerprint? GetFingerprint(string path) => null;
    public void UpsertFile(FileEntry file, IReadOnlyList<ChunkRecord> chunks) { }
    public void RemoveFile(string path) { }
    public int RemoveUnder(string directoryPrefix) => 0;
    public IndexStats GetStats() => new(hits.Length, hits.Length, 0, 0);
    public string? GetMeta(string key) => null;
    public void SetMeta(string key, string value) { }
    public void Dispose() { }

    public static StoreHit Hit(string path, float score, string text, FileKind kind = FileKind.Text) =>
        new(path, kind, text.Length, new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero), score, 0, text);
}
