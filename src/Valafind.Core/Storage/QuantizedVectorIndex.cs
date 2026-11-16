using Valafind.Core.Abstractions;

namespace Valafind.Core.Storage;

/// <summary>
/// In-memory brute-force vector index over int8-quantised chunk vectors.
/// </summary>
/// <remarks>
/// <para>Rows live in fixed-size segments (no giant re-allocations, no copying on growth). Each row holds
/// <c>Dimensions</c> int8 codes plus a float scale (symmetric per-vector quantisation, scale = maxabs/127) and,
/// in parallel arrays, the owning file id, file kind and modification time so filters run without SQL.</para>
/// <para>Memory: <c>rows × (dims + 4 + 8 + 8 + 1)</c> bytes, e.g. 446k files × 3 chunks × 256 dims ≈ 340 MB of codes
/// plus ≈ 28 MB of per-row metadata and ≈ 20 MB for the file → row-range map.</para>
/// <para>All rows of one file are appended contiguously; removal leaves tombstones (file id 0) which
/// <see cref="Compact"/> drops. The class is NOT thread-safe: <see cref="SqliteIndexStore"/> serialises writers and
/// guards readers with a reader/writer lock.</para>
/// </remarks>
internal sealed class QuantizedVectorIndex
{
    internal const int DefaultSegmentRows = 16 * 1024;

    /// <summary>Scan segments in parallel once the index holds at least this many rows.</summary>
    internal const int ParallelThresholdRows = 128 * 1024;

    private readonly int _segmentRows;
    private readonly List<Segment> _segments = [];
    private readonly Dictionary<long, RowRange> _fileRows = [];
    private int _rowCount;
    private int _deadRows;

    public QuantizedVectorIndex(int dimensions, int segmentRows = DefaultSegmentRows)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(dimensions, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(segmentRows, 1);
        Dimensions = dimensions;
        _segmentRows = segmentRows;
    }

    public int Dimensions { get; }

    /// <summary>Rows including tombstones.</summary>
    public int RowCount => _rowCount;

    public int DeadRows => _deadRows;

    public int LiveRows => _rowCount - _deadRows;

    public int FileCount => _fileRows.Count;

    /// <summary>True when more than 25 % of the rows are tombstones.</summary>
    public bool NeedsCompaction => _deadRows > 0 && _deadRows * 4L > _rowCount;

    public bool Contains(long fileId) => _fileRows.ContainsKey(fileId);

    /// <summary>Replaces all rows of <paramref name="fileId"/> with <paramref name="count"/> vectors taken
    /// contiguously from <paramref name="vectors"/> (count × Dimensions floats, ideally L2-normalised).</summary>
    public void SetFile(long fileId, FileKind kind, long modifiedUtcTicks, ReadOnlySpan<float> vectors, int count)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(fileId, 0);
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        if (vectors.Length != count * Dimensions)
            throw new ArgumentException("Vector buffer length must equal count × dimensions.", nameof(vectors));

        RemoveFile(fileId);
        if (count == 0)
            return;

        var start = _rowCount;
        for (var r = 0; r < count; r++)
        {
            var row = _rowCount;
            var seg = EnsureSegment(row / _segmentRows);
            var offset = row % _segmentRows;
            var codes = seg.Codes.AsSpan(offset * Dimensions, Dimensions);
            seg.Scales[offset] = VectorMath.Quantize(vectors.Slice(r * Dimensions, Dimensions), codes);
            seg.FileIds[offset] = fileId;
            seg.Kinds[offset] = (byte)kind;
            seg.ModifiedTicks[offset] = modifiedUtcTicks;
            _rowCount++;
        }
        _fileRows[fileId] = new RowRange(start, count);
    }

    /// <summary>Tombstones every row of <paramref name="fileId"/>. Returns false if the file had no rows.</summary>
    public bool RemoveFile(long fileId)
    {
        if (!_fileRows.Remove(fileId, out var range))
            return false;
        for (var row = range.Start; row < range.Start + range.Count; row++)
            _segments[row / _segmentRows].FileIds[row % _segmentRows] = 0;
        _deadRows += range.Count;
        return true;
    }

    /// <summary>Builds a new index containing only live rows (file order preserved). Does not modify this instance.</summary>
    public QuantizedVectorIndex Compact()
    {
        var result = new QuantizedVectorIndex(Dimensions, _segmentRows);
        for (var row = 0; row < _rowCount; row++)
        {
            var seg = _segments[row / _segmentRows];
            var offset = row % _segmentRows;
            var fileId = seg.FileIds[offset];
            if (fileId == 0)
                continue;
            result.AppendRaw(fileId, seg, offset, Dimensions);
        }
        return result;
    }

    private void AppendRaw(long fileId, Segment source, int sourceOffset, int dims)
    {
        var row = _rowCount;
        var seg = EnsureSegment(row / _segmentRows);
        var offset = row % _segmentRows;
        source.Codes.AsSpan(sourceOffset * dims, dims).CopyTo(seg.Codes.AsSpan(offset * dims, dims));
        seg.Scales[offset] = source.Scales[sourceOffset];
        seg.FileIds[offset] = fileId;
        seg.Kinds[offset] = source.Kinds[sourceOffset];
        seg.ModifiedTicks[offset] = source.ModifiedTicks[sourceOffset];
        _rowCount++;

        ref var range = ref System.Runtime.InteropServices.CollectionsMarshal.GetValueRefOrAddDefault(_fileRows, fileId, out var exists);
        range = exists ? range with { Count = range.Count + 1 } : new RowRange(row, 1);
    }

    private Segment EnsureSegment(int index)
    {
        while (_segments.Count <= index)
            _segments.Add(new Segment(_segmentRows, Dimensions));
        return _segments[index];
    }

    /// <summary>
    /// Approximate search: returns up to <paramref name="maxFiles"/> distinct files with the highest approximate
    /// (quantised) per-file max score, unordered. Safe to call concurrently with other readers.
    /// </summary>
    /// <param name="query">L2-normalised query with <see cref="Dimensions"/> components.</param>
    /// <param name="allowedKinds">Lookup indexed by (byte)FileKind, or null for no kind filter.</param>
    /// <param name="modifiedAfterTicks">Only rows with a strictly later modification time (UTC ticks) qualify.</param>
    public List<Candidate> Search(ReadOnlySpan<float> query, bool[]? allowedKinds, long modifiedAfterTicks, int maxFiles)
    {
        if (query.Length != Dimensions)
            throw new ArgumentException("Query dimensionality does not match the index.", nameof(query));
        if (maxFiles <= 0 || LiveRows == 0)
            return [];

        var queryCodes = new sbyte[Dimensions];
        var queryScale = VectorMath.Quantize(query, queryCodes);
        if (queryScale == 0f)
            return [];

        var segmentCount = (_rowCount + _segmentRows - 1) / _segmentRows;
        var perSegment = new PriorityQueue<long, float>?[segmentCount];
        var scan = new ScanContext(this, queryCodes, queryScale, allowedKinds, modifiedAfterTicks, maxFiles);

        if (_rowCount >= ParallelThresholdRows && Environment.ProcessorCount > 1)
        {
            Parallel.For(0, segmentCount, s => perSegment[s] = scan.ScanSegment(s));
        }
        else
        {
            for (var s = 0; s < segmentCount; s++)
                perSegment[s] = scan.ScanSegment(s);
        }

        // Merge: a file whose rows straddle a segment boundary may appear twice; keep its max.
        var best = new Dictionary<long, float>();
        foreach (var heap in perSegment)
        {
            if (heap is null)
                continue;
            while (heap.TryDequeue(out var fileId, out var score))
            {
                if (!best.TryGetValue(fileId, out var existing) || score > existing)
                    best[fileId] = score;
            }
        }

        var merged = new PriorityQueue<long, float>(maxFiles + 1);
        foreach (var (fileId, score) in best)
            PushBounded(merged, fileId, score, maxFiles);

        var result = new List<Candidate>(merged.Count);
        while (merged.TryDequeue(out var fileId, out var score))
            result.Add(new Candidate(fileId, score));
        return result;
    }

    private static void PushBounded(PriorityQueue<long, float> heap, long fileId, float score, int capacity)
    {
        if (heap.Count < capacity)
            heap.Enqueue(fileId, score);
        else if (heap.TryPeek(out _, out var min) && score > min)
            heap.EnqueueDequeue(fileId, score);
    }

    /// <summary>Approximate score of one row, for tests and diagnostics.</summary>
    internal float ApproximateScore(int row, ReadOnlySpan<float> query)
    {
        var seg = _segments[row / _segmentRows];
        var offset = row % _segmentRows;
        var q = new sbyte[Dimensions];
        var qs = VectorMath.Quantize(query, q);
        return VectorMath.DotInt8(seg.Codes.AsSpan(offset * Dimensions, Dimensions), q) * qs * seg.Scales[offset];
    }

    internal readonly record struct Candidate(long FileId, float ApproximateScore);

    private readonly record struct RowRange(int Start, int Count);

    private sealed class Segment(int rows, int dims)
    {
        public readonly sbyte[] Codes = new sbyte[rows * dims];
        public readonly float[] Scales = new float[rows];
        public readonly long[] FileIds = new long[rows];
        public readonly long[] ModifiedTicks = new long[rows];
        public readonly byte[] Kinds = new byte[rows];
    }

    private sealed class ScanContext(
        QuantizedVectorIndex index, sbyte[] queryCodes, float queryScale, bool[]? allowedKinds, long modifiedAfterTicks, int maxFiles)
    {
        public PriorityQueue<long, float>? ScanSegment(int segmentIndex)
        {
            var seg = index._segments[segmentIndex];
            var dims = index.Dimensions;
            var rows = Math.Min(index._segmentRows, index._rowCount - segmentIndex * index._segmentRows);
            var q = queryCodes.AsSpan();
            PriorityQueue<long, float>? heap = null;

            long currentFile = 0;
            var currentBest = float.NegativeInfinity;

            for (var offset = 0; offset < rows; offset++)
            {
                var fileId = seg.FileIds[offset];
                if (fileId == 0)
                    continue;
                if (allowedKinds is not null && !allowedKinds[seg.Kinds[offset]])
                    continue;
                if (seg.ModifiedTicks[offset] <= modifiedAfterTicks)
                    continue;

                var dot = VectorMath.DotInt8(seg.Codes.AsSpan(offset * dims, dims), q);
                var score = dot * queryScale * seg.Scales[offset];

                if (fileId != currentFile)
                {
                    if (currentFile != 0)
                        PushBounded(heap ??= new PriorityQueue<long, float>(maxFiles + 1), currentFile, currentBest, maxFiles);
                    currentFile = fileId;
                    currentBest = score;
                }
                else if (score > currentBest)
                {
                    currentBest = score;
                }
            }

            if (currentFile != 0)
                PushBounded(heap ??= new PriorityQueue<long, float>(maxFiles + 1), currentFile, currentBest, maxFiles);
            return heap;
        }
    }
}
