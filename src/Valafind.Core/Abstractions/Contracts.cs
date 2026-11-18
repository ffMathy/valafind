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
    /// <summary>Stable identifier of model + quantisation + dimensions, e.g. "embeddinggemma-2/model_quantized/256".
    /// Stored in the index; a change forces re-embedding.</summary>
    string ModelId { get; }
    int Dimensions { get; }
    int MaxInputTokens { get; }
    int CountTokens(string text);
    float[] EmbedQuery(string query, CancellationToken cancellationToken = default);
    IReadOnlyList<float[]> EmbedDocuments(IReadOnlyList<DocumentText> documents, CancellationToken cancellationToken = default);
}

/// <summary>A decoded picture or video frame: 8-bit RGB, row-major, no padding (<c>Rgb.Length == Width * Height * 3</c>),
/// already turned upright (EXIF orientation / video rotation applied).</summary>
public sealed record DecodedImage(int Width, int Height, byte[] Rgb)
{
    /// <summary>Throws when the buffer does not match the size.</summary>
    public void Validate()
    {
        if (Width <= 0 || Height <= 0 || Rgb is null || Rgb.LongLength != (long)Width * Height * 3)
            throw new ArgumentException($"Invalid {Width}x{Height} RGB image ({Rgb?.LongLength ?? 0} bytes).");
    }
}

/// <summary>A piece of audio for the audio encoder: mono 16 kHz samples in [-1, 1], at most
/// <see cref="MaxSeconds"/> long, starting at <paramref name="Offset"/> in the file.</summary>
public sealed record AudioClip(float[] Samples, TimeSpan Offset)
{
    public const int SampleRate = 16_000;

    /// <summary>The audio encoder reads at most 30 s per input (the feature extractor's <c>max_length</c>).</summary>
    public const int MaxSeconds = 30;
}

/// <summary>Frames sampled from a video (at most one per second, in time order) and their time stamps.</summary>
public sealed record VideoFrames(IReadOnlyList<DecodedImage> Frames, IReadOnlyList<TimeSpan> Timestamps, TimeSpan Duration);

/// <summary>Kinds of media an <see cref="IMediaEmbeddingModel"/> can embed.</summary>
[Flags]
public enum MediaCapabilities
{
    None = 0,
    Image = 1,
    Audio = 2,
    Video = 4,
}

/// <summary>
/// Optional capability of an <see cref="IEmbeddingModel"/>: embeds pictures, audio and video into the same vector space
/// as text, so a text query finds them by what they show or say. Same threading rules as the text calls (background
/// priority; queries preempt). Implementations MUST NOT perform network I/O.
/// </summary>
public interface IMediaEmbeddingModel
{
    /// <summary>What this install can embed (the vision and audio encoders are separate, optional files).</summary>
    MediaCapabilities MediaCapabilities { get; }

    float[] EmbedImage(DecodedImage image, CancellationToken cancellationToken = default);

    float[] EmbedAudio(AudioClip clip, CancellationToken cancellationToken = default);

    /// <summary>One vector for the frames together (they are one input, in order).</summary>
    float[] EmbedVideo(IReadOnlyList<DecodedImage> frames, CancellationToken cancellationToken = default);

    /// <summary>Frees the media encoders' memory (they are loaded again on the next media call). Called when
    /// indexing goes idle.</summary>
    void ReleaseMediaResources();
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

/// <summary>
/// Decodes pictures, audio and video for <see cref="IMediaEmbeddingModel"/> with the operating system's codecs (or a
/// bundled/installed decoder). Never throws for missing, unreadable or unsupported files: it returns null. Only
/// <see cref="OperationCanceledException"/> for the caller's token propagates. No network I/O.
/// </summary>
public interface IMediaDecoder
{
    /// <summary>The picture, upright, scaled down (never up) so that neither side exceeds
    /// <paramref name="maxSide"/> pixels.</summary>
    Task<DecodedImage?> DecodeImageAsync(string path, int maxSide, CancellationToken cancellationToken);

    /// <summary>Up to <paramref name="maxClips"/> clips of up to <see cref="AudioClip.MaxSeconds"/> each: consecutive
    /// from the start when the whole file fits, otherwise spread evenly over the file. Empty when the file has no
    /// audio track.</summary>
    Task<IReadOnlyList<AudioClip>?> DecodeAudioAsync(string path, int maxClips, CancellationToken cancellationToken);

    /// <summary>Frames at whole seconds (one per second, like the model's 1 fps sampling), thinned out evenly to at
    /// most <paramref name="maxFrames"/>, each scaled down to at most <paramref name="maxSide"/> pixels.</summary>
    Task<VideoFrames?> DecodeVideoAsync(string path, int maxFrames, int maxSide, CancellationToken cancellationToken);
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

/// <param name="ContentPending">The file was online-only (a cloud placeholder) and could not be downloaded, so it is
/// indexed by name only; its content is indexed once it can be read.</param>
public sealed record FileEntry(string Path, long Size, DateTimeOffset ModifiedUtc, FileKind Kind, IndexPhase Phase, bool ContentPending = false);

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
    /// <summary>Indexed files per <see cref="FileKind"/>; kinds without files may be left out. Cheap (kept in memory).</summary>
    IReadOnlyDictionary<FileKind, long> GetFileCountsByKind() => EmptyKindCounts.Instance;
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
    /// <summary>No longer raised: system load slows indexing down (<see cref="Indexing.IndexingPace"/>) instead of
    /// pausing it. Kept so stored values and the UI's texts for it stay valid.</summary>
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

    /// <summary>Files currently in the index per <see cref="FileKind"/> (kinds without files may be left out).</summary>
    public IReadOnlyDictionary<FileKind, long> IndexedByKind { get; init; } = EmptyKindCounts.Instance;
}

/// <summary>Shared empty per-kind count map.</summary>
public static class EmptyKindCounts
{
    /// <summary>The empty map.</summary>
    public static IReadOnlyDictionary<FileKind, long> Instance { get; } = new Dictionary<FileKind, long>();
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
    /// batches start until it is set back to false, so query embedding never queues behind indexing. As a safety net
    /// (e.g. the launcher left open), indexing resumes on its own after the coordinator's interactive timeout (2 minutes).</summary>
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

/// <summary>A file found by a walk, with the size and last write time the directory listing reported for it.</summary>
/// <param name="Path">Full path.</param>
/// <param name="Size">Size in bytes.</param>
/// <param name="ModifiedUtc">Last write time.</param>
public readonly record struct DiscoveredFile(string Path, long Size, DateTimeOffset ModifiedUtc);

/// <summary>Optional capability of an <see cref="IFileDiscovery"/>: the Phase 2 walk with each file's size and last
/// write time as the directory listing reported them. Windows and macOS return those with the listing itself, so
/// the indexer can tell an unchanged file from the index without a stat call of its own.</summary>
public interface IFileStatDiscovery
{
    /// <summary>The same files, in the same order, as <see cref="IFileDiscovery.EnumerateAllFiles"/>.</summary>
    IEnumerable<DiscoveredFile> EnumerateAllFilesWithStats(CancellationToken cancellationToken);
}
