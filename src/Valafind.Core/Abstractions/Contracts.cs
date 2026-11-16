// Module boundaries for Valafind. Each module (Embedding, Extraction, Storage/Search, Indexing/System)
// implements the interfaces below; the App composes them. Keep these stable: changing a contract means
// changing every implementation and the composition root (src/Valafind.App/Services/AppHost.cs).

namespace Valafind.Core.Abstractions;

// ---------------------------------------------------------------- Embedding (Valafind.Core.Embedding)

/// <summary>Text handed to the embedding model for a document chunk.</summary>
/// <param name="Title">File name (or "none"), used in the EmbeddingGemma document prompt.</param>
/// <param name="Text">Chunk text.</param>
public sealed record DocumentText(string Title, string Text);

/// <summary>
/// A local, offline embedding model. Implementations MUST NOT perform network I/O.
/// Vectors are L2-normalised and have exactly <see cref="Dimensions"/> components
/// (EmbeddingGemma supports Matryoshka truncation to 768/512/256/128 + re-normalisation).
/// Implementations must be safe to call from multiple threads (serialising internally is fine).
/// </summary>
public interface IEmbeddingModel : IDisposable
{
    /// <summary>Stable identifier of model + quantisation + dimensions, e.g. "embeddinggemma-300m/q8/256".
    /// Stored in the index; a change forces re-embedding.</summary>
    string ModelId { get; }
    int Dimensions { get; }
    int MaxInputTokens { get; }
    int CountTokens(string text);
    float[] EmbedQuery(string query, CancellationToken cancellationToken = default);
    IReadOnlyList<float[]> EmbedDocuments(IReadOnlyList<DocumentText> documents, CancellationToken cancellationToken = default);
}

// ---------------------------------------------------------------- Extraction (Valafind.Core.Extraction)

/// <summary>Per-file extraction budget. MaxChars is sized to what the first indexing pass embeds
/// (≈ ChunkerOptions.MaxChunks × TargetTokens × ~4–5 chars/token) plus headroom; parsing more is wasted CPU.</summary>
public sealed record ExtractionLimits(long MaxFileBytes = 64L * 1024 * 1024, int MaxChars = 32_000);

/// <summary>Plain text pulled out of a file. <paramref name="Text"/> may be empty for files that only
/// carry a name (images, media) — callers then embed the file name/path only.</summary>
public sealed record ExtractedText(string Text, FileKind Kind);

public interface ITextExtractor
{
    bool CanHandle(string path);
    Task<ExtractedText?> ExtractAsync(string path, ExtractionLimits limits, CancellationToken cancellationToken);
}

/// <summary>Picks the right <see cref="ITextExtractor"/>; never throws for unreadable/corrupt files (returns null).</summary>
public interface IDocumentReader
{
    Task<ExtractedText?> ReadAsync(string path, CancellationToken cancellationToken);
}

public sealed record TextChunk(int Index, int Start, int Length, string Text);

public interface ITextChunker
{
    /// <summary>Splits text into overlapping chunks that each fit the embedding model's token budget.</summary>
    IReadOnlyList<TextChunk> Chunk(string text);
}

// ---------------------------------------------------------------- Storage & search (Valafind.Core.Storage / .Search)

public enum IndexPhase
{
    /// <summary>Phase 1: commonly / recently accessed files.</summary>
    Frequent = 1,
    /// <summary>Phase 2: every remaining user file.</summary>
    All = 2,
    /// <summary>Changes picked up by file watching after the initial phases.</summary>
    Incremental = 3,
}

public sealed record FileFingerprint(long Size, DateTimeOffset ModifiedUtc);

public sealed record FileEntry(string Path, long Size, DateTimeOffset ModifiedUtc, FileKind Kind, IndexPhase Phase);

public sealed record ChunkRecord(int Index, int Start, int Length, string Text, float[] Vector);

public sealed record SearchFilter(IReadOnlySet<FileKind>? Kinds = null, DateTimeOffset? ModifiedAfter = null)
{
    public static SearchFilter None { get; } = new();
}

/// <summary>A raw vector hit, aggregated per file (best chunk wins).</summary>
public sealed record StoreHit(string Path, FileKind Kind, long Size, DateTimeOffset ModifiedUtc,
    float Score, int ChunkIndex, string ChunkText);

public sealed record IndexStats(long Files, long Chunks, long FilesByPhaseFrequent, long FilesByPhaseAll);

/// <summary>Persistent index (SQLite on disk + an in-memory vector matrix for brute-force SIMD search).
/// Thread-safe: indexing writes and UI searches happen concurrently.</summary>
public interface IIndexStore : IDisposable
{
    /// <summary>Opens/creates the store. If <paramref name="modelId"/> differs from the stored one, all vectors are dropped.</summary>
    void Open(string modelId, int dimensions);
    FileFingerprint? GetFingerprint(string path);
    void UpsertFile(FileEntry file, IReadOnlyList<ChunkRecord> chunks);
    void RemoveFile(string path);
    /// <summary>Removes every indexed file whose path starts with <paramref name="directoryPrefix"/>.</summary>
    int RemoveUnder(string directoryPrefix);
    IReadOnlyList<StoreHit> Search(ReadOnlySpan<float> query, SearchFilter filter, int topK);
    IndexStats GetStats();
    string? GetMeta(string key);
    void SetMeta(string key, string value);
}

public readonly record struct TextSpan(int Start, int Length);

public sealed record SearchResult(
    string Path,
    string FileName,
    string Directory,
    FileKind Kind,
    DateTimeOffset ModifiedUtc,
    long Size,
    float Score,
    /// <summary>Short passage (≈ 240 chars) around the best-matching part of the file.</summary>
    string Snippet,
    /// <summary>Spans inside <see cref="Snippet"/> that explain the match ("why it matched").</summary>
    IReadOnlyList<TextSpan> Highlights);

public interface ISearchService
{
    /// <summary>Fast path: ranked results with snippets and LEXICAL highlights. Must not wait on extra model work
    /// beyond embedding the query.</summary>
    Task<IReadOnlyList<SearchResult>> SearchAsync(string query, SearchFilter filter, int topK, CancellationToken cancellationToken);

    /// <summary>Optional second pass the UI runs after showing <paramref name="results"/>: returns the same list (same order,
    /// same length) where the top few results carry SEMANTIC "why it matched" highlights (best-matching sentence by
    /// embedding). Results it does not improve are returned unchanged. Cheap by design (a handful of short sentences).</summary>
    Task<IReadOnlyList<SearchResult>> ExplainAsync(string query, IReadOnlyList<SearchResult> results, CancellationToken cancellationToken);
}

// ---------------------------------------------------------------- System pressure & indexing (Valafind.Core.Indexing / .Platform)

/// <param name="CpuPercent">Whole-machine CPU utilisation 0–100 over the last sampling interval.</param>
/// <param name="DiskBusyPercent">Share of time any physical disk was busy 0–100, or null when the OS cannot tell us.</param>
/// <param name="DiskBytesPerSecond">Read+write throughput across physical disks, or null when unknown.</param>
/// <param name="OnBattery">True when running on battery power.</param>
public sealed record PressureSample(DateTimeOffset At, double CpuPercent, double? DiskBusyPercent,
    double? DiskBytesPerSecond, bool OnBattery);

public interface ISystemPressureMonitor : IDisposable
{
    /// <summary>Returns utilisation since the previous call (first call may block briefly to take a baseline).</summary>
    PressureSample Sample();
}

public enum IndexingState
{
    Starting,
    Running,
    PausedSystemBusy,
    PausedOnBattery,
    PausedByUser,
    /// <summary>All phases complete; watching for changes.</summary>
    UpToDate,
    /// <summary>Model files missing or failed to load; search unavailable.</summary>
    ModelUnavailable,
}

public sealed record IndexStatus(
    IndexingState State,
    IndexPhase Phase,
    long FrequentDone,
    long FrequentTotal,
    long AllDone,
    /// <summary>Known so far; grows while discovery is still walking the disk.</summary>
    long AllTotal,
    bool DiscoveryComplete,
    PressureSample? LastSample,
    string? CurrentFile)
{
    public static IndexStatus Initial { get; } = new(IndexingState.Starting, IndexPhase.Frequent, 0, 0, 0, 0, false, null, null);
}

public interface IIndexingService : IAsyncDisposable
{
    IndexStatus Status { get; }
    /// <summary>Raised on a background thread; UI must marshal.</summary>
    event EventHandler<IndexStatus>? StatusChanged;
    Task StartAsync(CancellationToken cancellationToken);
    /// <summary>User pause/resume (persisted by the caller).</summary>
    void SetUserPaused(bool paused);
    /// <summary>"Resume now": ignore system-pressure throttling for the given duration.</summary>
    void OverrideThrottle(TimeSpan duration);
    /// <summary>True while the user is interacting with search (launcher visible). Indexing yields: no new model
    /// batches start until it is set back to false, so query embedding never queues behind indexing.</summary>
    void SetInteractive(bool active);
}

/// <summary>Finds candidate files. Phase 1 = frequently / recently used, Phase 2 = everything under user roots.</summary>
public interface IFileDiscovery
{
    IReadOnlyList<string> Roots { get; }
    bool IsExcluded(string path);
    IEnumerable<string> EnumerateFrequentFiles(CancellationToken cancellationToken);
    IEnumerable<string> EnumerateAllFiles(CancellationToken cancellationToken);
}
