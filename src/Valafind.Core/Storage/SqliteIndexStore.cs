using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using Microsoft.Data.Sqlite;
using Valafind.Core.Abstractions;

namespace Valafind.Core.Storage;

/// <summary>
/// Persistent semantic index: SQLite (WAL) on disk for files, chunks and float32 vectors, plus an in-memory
/// int8-quantised matrix for fast brute-force SIMD search with exact float re-scoring of the best candidates.
/// </summary>
/// <remarks>
/// <para>Thread safety: writes (<see cref="UpsertFile"/>, <see cref="RemoveFile"/>, <see cref="RemoveUnder"/>,
/// <see cref="SetMeta"/>) are serialised; searches run concurrently with each other and with writes. The in-memory
/// matrix is guarded by a reader/writer lock whose write sections only cover the (microsecond) row updates, and
/// searches read SQLite through their own read-only connections, so indexing never blocks a search for long.</para>
/// <para>Memory: about <c>chunks × (dimensions + 21)</c> bytes, e.g. ≈ 370 MB for 1.3 M chunks at 256 dimensions.
/// <see cref="Open"/> loads every vector once (seconds for a large index).</para>
/// <para>Paths are stored as full paths. Uniqueness uses a <c>path_key</c> column which is lower-cased on Windows and
/// macOS (case-insensitive file systems; NFC on macOS) and the exact path on Linux.</para>
/// </remarks>
public sealed class SqliteIndexStore : IIndexStore
{
    private const int SchemaVersion = 1;
    private const int BusyTimeoutMs = 10_000;
    private const int MaxPooledReaders = 8;
    private const string ModelIdKey = "model_id";
    private const string DimensionsKey = "dimensions";
    private const string SchemaVersionKey = "schema_version";

    private readonly string _databasePath;
    private readonly PathKeyPolicy _paths;
    private readonly Lock _writeGate = new();
    private readonly ReaderWriterLockSlim _indexLock = new(LockRecursionPolicy.NoRecursion);
    private readonly ConcurrentBag<SqliteConnection> _readers = [];

    private string? _connectionString;
    private SqliteConnection? _writer;
    private QuantizedVectorIndex? _index;
    private int _dimensions;
    private long _fileCount;
    private long _frequentCount;
    private long _allCount;
    private volatile bool _disposed;

    /// <summary>Creates a store backed by the SQLite database at <paramref name="databasePath"/> (created on <see cref="Open"/>).</summary>
    public SqliteIndexStore(string databasePath)
        : this(databasePath, PathKeyPolicy.Current)
    {
    }

    internal SqliteIndexStore(string databasePath, PathKeyPolicy pathPolicy)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        _databasePath = Path.GetFullPath(databasePath);
        _paths = pathPolicy;
    }

    /// <summary>Number of rows (live + tombstoned) in the in-memory matrix; for diagnostics and tests.</summary>
    internal (int Rows, int DeadRows) MatrixRows
    {
        get
        {
            _indexLock.EnterReadLock();
            try
            {
                return _index is null ? (0, 0) : (_index.RowCount, _index.DeadRows);
            }
            finally
            {
                _indexLock.ExitReadLock();
            }
        }
    }

    /// <inheritdoc />
    public void Open(string modelId, int dimensions)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelId);
        ArgumentOutOfRangeException.ThrowIfLessThan(dimensions, 1);

        lock (_writeGate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            CloseConnections();

            var directory = Path.GetDirectoryName(_databasePath);
            if (!string.IsNullOrEmpty(directory))
                Directory.CreateDirectory(directory);

            _connectionString = new SqliteConnectionStringBuilder
            {
                DataSource = _databasePath,
                Mode = SqliteOpenMode.ReadWriteCreate,
                Cache = SqliteCacheMode.Private,
                Pooling = false,
                DefaultTimeout = BusyTimeoutMs / 1000,
            }.ToString();

            var writer = new SqliteConnection(_connectionString);
            try
            {
                writer.Open();
                Execute(writer, $"PRAGMA busy_timeout={BusyTimeoutMs};");
                Execute(writer, "PRAGMA journal_mode=WAL;");
                Execute(writer, "PRAGMA synchronous=NORMAL;");
                Execute(writer, "PRAGMA temp_store=MEMORY;");
                CreateSchema(writer);
                EnsureModel(writer, modelId, dimensions);
                LoadCounts(writer);
                var index = LoadIndex(writer, dimensions);

                _writer = writer;
                _dimensions = dimensions;
                _indexLock.EnterWriteLock();
                try
                {
                    _index = index;
                }
                finally
                {
                    _indexLock.ExitWriteLock();
                }
            }
            catch
            {
                writer.Dispose();
                _connectionString = null;
                throw;
            }
        }
    }

    private static void CreateSchema(SqliteConnection db)
    {
        using var tx = db.BeginTransaction();
        Execute(db, """
            CREATE TABLE IF NOT EXISTS meta(
                key   TEXT PRIMARY KEY,
                value TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS files(
                id          INTEGER PRIMARY KEY,
                path        TEXT    NOT NULL,
                path_key    TEXT    NOT NULL UNIQUE,
                size        INTEGER NOT NULL,
                mtime_utc   INTEGER NOT NULL,
                kind        INTEGER NOT NULL,
                phase       INTEGER NOT NULL,
                indexed_utc INTEGER NOT NULL);
            CREATE TABLE IF NOT EXISTS chunks(
                file_id INTEGER NOT NULL,
                idx     INTEGER NOT NULL,
                start   INTEGER NOT NULL,
                length  INTEGER NOT NULL,
                text    TEXT    NOT NULL,
                vector  BLOB    NOT NULL);
            CREATE UNIQUE INDEX IF NOT EXISTS chunks_file_idx ON chunks(file_id, idx);
            """, tx);
        tx.Commit();
    }

    private static void EnsureModel(SqliteConnection db, string modelId, int dimensions)
    {
        using var tx = db.BeginTransaction();
        var storedModel = ReadMeta(db, ModelIdKey, tx);
        var storedDims = ReadMeta(db, DimensionsKey, tx);
        var dimsText = dimensions.ToString(CultureInfo.InvariantCulture);

        if (!string.Equals(storedModel, modelId, StringComparison.Ordinal) || !string.Equals(storedDims, dimsText, StringComparison.Ordinal))
        {
            // Vectors from another model / size are meaningless for the new one: start over.
            Execute(db, "DELETE FROM chunks; DELETE FROM files;", tx);
            WriteMeta(db, ModelIdKey, modelId, tx);
            WriteMeta(db, DimensionsKey, dimsText, tx);
        }
        WriteMeta(db, SchemaVersionKey, SchemaVersion.ToString(CultureInfo.InvariantCulture), tx);
        tx.Commit();
    }

    private void LoadCounts(SqliteConnection db)
    {
        long files = 0, frequent = 0, all = 0;
        using var cmd = db.CreateCommand();
        cmd.CommandText = "SELECT phase, COUNT(*) FROM files GROUP BY phase;";
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var phase = (IndexPhase)reader.GetInt32(0);
            var count = reader.GetInt64(1);
            files += count;
            if (phase == IndexPhase.Frequent)
                frequent += count;
            else if (phase == IndexPhase.All)
                all += count;
        }
        Volatile.Write(ref _fileCount, files);
        Volatile.Write(ref _frequentCount, frequent);
        Volatile.Write(ref _allCount, all);
    }

    private static QuantizedVectorIndex LoadIndex(SqliteConnection db, int dimensions)
    {
        var index = new QuantizedVectorIndex(dimensions);
        using var cmd = db.CreateCommand();
        cmd.CommandText = """
            SELECT c.file_id, c.vector, f.kind, f.mtime_utc
            FROM chunks c JOIN files f ON f.id = c.file_id
            ORDER BY c.file_id, c.idx;
            """;
        using var reader = cmd.ExecuteReader();

        var blob = new byte[dimensions * sizeof(float)];
        var buffer = new float[dimensions * 8];
        long currentFile = 0;
        var currentKind = FileKind.Other;
        long currentTicks = 0;
        var count = 0;

        while (reader.Read())
        {
            var fileId = reader.GetInt64(0);
            if (fileId != currentFile)
            {
                if (currentFile != 0)
                    index.SetFile(currentFile, currentKind, currentTicks, buffer.AsSpan(0, count * dimensions), count);
                currentFile = fileId;
                currentKind = (FileKind)reader.GetInt32(2);
                currentTicks = reader.GetInt64(3);
                count = 0;
            }

            var length = reader.GetBytes(1, 0, null, 0, 0);
            if (length != blob.Length)
                continue; // Corrupt or foreign-sized vector: skip the chunk rather than poison the matrix.
            reader.GetBytes(1, 0, blob, 0, blob.Length);

            if ((count + 1) * dimensions > buffer.Length)
                Array.Resize(ref buffer, buffer.Length * 2);
            var slot = buffer.AsSpan(count * dimensions, dimensions);
            VectorMath.TryReadBlob(blob, slot);
            VectorMath.TryNormalize(slot);
            count++;
        }
        if (currentFile != 0)
            index.SetFile(currentFile, currentKind, currentTicks, buffer.AsSpan(0, count * dimensions), count);
        return index;
    }

    /// <inheritdoc />
    public FileFingerprint? GetFingerprint(string path)
    {
        var key = _paths.KeyForPath(path);
        return WithReader(db =>
        {
            using var cmd = db.CreateCommand();
            cmd.CommandText = "SELECT size, mtime_utc FROM files WHERE path_key = $key;";
            cmd.Parameters.AddWithValue("$key", key);
            using var reader = cmd.ExecuteReader();
            return reader.Read()
                ? new FileFingerprint(reader.GetInt64(0), new DateTimeOffset(reader.GetInt64(1), TimeSpan.Zero))
                : null;
        });
    }

    /// <inheritdoc />
    public void UpsertFile(FileEntry file, IReadOnlyList<ChunkRecord> chunks)
    {
        ArgumentNullException.ThrowIfNull(file);
        ArgumentNullException.ThrowIfNull(chunks);

        var path = PathKeyPolicy.NormalizePath(file.Path);
        var key = _paths.KeyOf(path);
        var ticks = file.ModifiedUtc.UtcTicks;

        lock (_writeGate)
        {
            var db = RequireWriter();
            var dims = _dimensions;

            // Validate and normalise outside the transaction; any bad input aborts before touching the DB.
            var vectors = new float[chunks.Count * dims];
            var seen = new HashSet<int>();
            for (var i = 0; i < chunks.Count; i++)
            {
                var chunk = chunks[i] ?? throw new ArgumentException("Chunk list contains null.", nameof(chunks));
                if (!seen.Add(chunk.Index))
                    throw new ArgumentException($"Duplicate chunk index {chunk.Index}.", nameof(chunks));
                if (chunk.Vector is null || chunk.Vector.Length != dims)
                    throw new ArgumentException($"Chunk {chunk.Index} has {chunk.Vector?.Length ?? 0} dimensions; the index uses {dims}.", nameof(chunks));
                var slot = vectors.AsSpan(i * dims, dims);
                chunk.Vector.CopyTo(slot);
                VectorMath.TryNormalize(slot);
            }

            long fileId;
            IndexPhase? previousPhase = null;
            using (var tx = db.BeginTransaction())
            {
                using (var find = db.CreateCommand())
                {
                    find.Transaction = tx;
                    find.CommandText = "SELECT id, phase FROM files WHERE path_key = $key;";
                    find.Parameters.AddWithValue("$key", key);
                    using var reader = find.ExecuteReader();
                    fileId = 0;
                    if (reader.Read())
                    {
                        fileId = reader.GetInt64(0);
                        previousPhase = (IndexPhase)reader.GetInt32(1);
                    }
                }

                using (var write = db.CreateCommand())
                {
                    write.Transaction = tx;
                    write.CommandText = fileId != 0
                        ? """
                          UPDATE files SET path = $path, size = $size, mtime_utc = $mtime, kind = $kind, phase = $phase, indexed_utc = $now
                          WHERE id = $id RETURNING id;
                          """
                        : """
                          INSERT INTO files(path, path_key, size, mtime_utc, kind, phase, indexed_utc)
                          VALUES ($path, $key, $size, $mtime, $kind, $phase, $now) RETURNING id;
                          """;
                    write.Parameters.AddWithValue("$id", fileId);
                    write.Parameters.AddWithValue("$path", path);
                    write.Parameters.AddWithValue("$key", key);
                    write.Parameters.AddWithValue("$size", file.Size);
                    write.Parameters.AddWithValue("$mtime", ticks);
                    write.Parameters.AddWithValue("$kind", (int)file.Kind);
                    write.Parameters.AddWithValue("$phase", (int)file.Phase);
                    write.Parameters.AddWithValue("$now", DateTime.UtcNow.Ticks);
                    fileId = Convert.ToInt64(write.ExecuteScalar(), CultureInfo.InvariantCulture);
                }

                if (previousPhase is not null)
                    DeleteChunks(db, tx, fileId);

                if (chunks.Count > 0)
                {
                    using var insert = db.CreateCommand();
                    insert.Transaction = tx;
                    insert.CommandText = """
                        INSERT INTO chunks(file_id, idx, start, length, text, vector)
                        VALUES ($file, $idx, $start, $length, $text, $vector);
                        """;
                    var pFile = insert.Parameters.Add("$file", SqliteType.Integer);
                    var pIdx = insert.Parameters.Add("$idx", SqliteType.Integer);
                    var pStart = insert.Parameters.Add("$start", SqliteType.Integer);
                    var pLength = insert.Parameters.Add("$length", SqliteType.Integer);
                    var pText = insert.Parameters.Add("$text", SqliteType.Text);
                    var pVector = insert.Parameters.Add("$vector", SqliteType.Blob);
                    insert.Prepare();
                    pFile.Value = fileId;
                    for (var i = 0; i < chunks.Count; i++)
                    {
                        var chunk = chunks[i];
                        pIdx.Value = chunk.Index;
                        pStart.Value = chunk.Start;
                        pLength.Value = chunk.Length;
                        pText.Value = chunk.Text ?? string.Empty;
                        pVector.Value = VectorMath.ToBlob(vectors.AsSpan(i * dims, dims));
                        insert.ExecuteNonQuery();
                    }
                }
                tx.Commit();
            }

            AdjustCounts(previousPhase, -1);
            AdjustCounts(file.Phase, +1);

            // The matrix keeps the chunk order of the database (ORDER BY idx) so a reload is identical.
            var order = Enumerable.Range(0, chunks.Count).OrderBy(i => chunks[i].Index).ToArray();
            var ordered = new float[vectors.Length];
            for (var i = 0; i < order.Length; i++)
                vectors.AsSpan(order[i] * dims, dims).CopyTo(ordered.AsSpan(i * dims, dims));

            MutateIndex(index => index.SetFile(fileId, file.Kind, ticks, ordered, chunks.Count));
        }
    }

    /// <inheritdoc />
    public void RemoveFile(string path)
    {
        var key = _paths.KeyForPath(path);
        lock (_writeGate)
        {
            var db = RequireWriter();
            var removed = new List<(long Id, IndexPhase Phase)>();
            using (var tx = db.BeginTransaction())
            {
                using (var find = db.CreateCommand())
                {
                    find.Transaction = tx;
                    find.CommandText = "SELECT id, phase FROM files WHERE path_key = $key;";
                    find.Parameters.AddWithValue("$key", key);
                    using var reader = find.ExecuteReader();
                    while (reader.Read())
                        removed.Add((reader.GetInt64(0), (IndexPhase)reader.GetInt32(1)));
                }
                DeleteFiles(db, tx, removed);
                tx.Commit();
            }
            ApplyRemoval(removed);
        }
    }

    /// <inheritdoc />
    /// <remarks>Matches whole path components: removing "/home/a/docs" removes "/home/a/docs" and "/home/a/docs/…"
    /// but not "/home/a/docs2/…". Trailing separators on <paramref name="directoryPrefix"/> are ignored.</remarks>
    public int RemoveUnder(string directoryPrefix)
    {
        var (exact, lower, upper) = _paths.DirectoryRange(directoryPrefix);
        lock (_writeGate)
        {
            var db = RequireWriter();
            var removed = new List<(long Id, IndexPhase Phase)>();
            using (var tx = db.BeginTransaction())
            {
                using (var find = db.CreateCommand())
                {
                    find.Transaction = tx;
                    find.CommandText = """
                        SELECT id, phase FROM files
                        WHERE path_key = $exact OR (path_key >= $lower AND path_key < $upper);
                        """;
                    find.Parameters.AddWithValue("$exact", exact);
                    find.Parameters.AddWithValue("$lower", lower);
                    find.Parameters.AddWithValue("$upper", upper);
                    using var reader = find.ExecuteReader();
                    while (reader.Read())
                        removed.Add((reader.GetInt64(0), (IndexPhase)reader.GetInt32(1)));
                }
                DeleteFiles(db, tx, removed);
                tx.Commit();
            }
            ApplyRemoval(removed);
            return removed.Count;
        }
    }

    private static void DeleteFiles(SqliteConnection db, SqliteTransaction tx, List<(long Id, IndexPhase Phase)> files)
    {
        if (files.Count == 0)
            return;
        using var deleteFile = db.CreateCommand();
        deleteFile.Transaction = tx;
        deleteFile.CommandText = "DELETE FROM files WHERE id = $id;";
        var pId = deleteFile.Parameters.Add("$id", SqliteType.Integer);
        deleteFile.Prepare();
        foreach (var (id, _) in files)
        {
            DeleteChunks(db, tx, id);
            pId.Value = id;
            deleteFile.ExecuteNonQuery();
        }
    }

    private static void DeleteChunks(SqliteConnection db, SqliteTransaction tx, long fileId)
    {
        using var cmd = db.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "DELETE FROM chunks WHERE file_id = $id;";
        cmd.Parameters.AddWithValue("$id", fileId);
        cmd.ExecuteNonQuery();
    }

    private void ApplyRemoval(List<(long Id, IndexPhase Phase)> removed)
    {
        if (removed.Count == 0)
            return;
        foreach (var (_, phase) in removed)
            AdjustCounts(phase, -1);
        MutateIndex(index =>
        {
            foreach (var (id, _) in removed)
                index.RemoveFile(id);
        });
    }

    private void AdjustCounts(IndexPhase? phase, int delta)
    {
        if (phase is null)
            return;
        Interlocked.Add(ref _fileCount, delta);
        if (phase == IndexPhase.Frequent)
            Interlocked.Add(ref _frequentCount, delta);
        else if (phase == IndexPhase.All)
            Interlocked.Add(ref _allCount, delta);
    }

    /// <summary>Applies a mutation under the matrix write lock, then compacts if tombstones exceed 25 %.
    /// Caller holds <see cref="_writeGate"/>, so nobody else mutates the matrix while it is rebuilt.</summary>
    private void MutateIndex(Action<QuantizedVectorIndex> mutation)
    {
        QuantizedVectorIndex index;
        _indexLock.EnterWriteLock();
        try
        {
            index = _index ?? throw new InvalidOperationException("The index store is not open.");
            mutation(index);
        }
        finally
        {
            _indexLock.ExitWriteLock();
        }

        if (!index.NeedsCompaction)
            return;

        // Built outside the write lock (searches keep running on the old matrix), swapped in atomically.
        var compacted = index.Compact();
        _indexLock.EnterWriteLock();
        try
        {
            _index = compacted;
        }
        finally
        {
            _indexLock.ExitWriteLock();
        }
    }

    /// <inheritdoc />
    /// <remarks>
    /// The query is copied and L2-normalised (a zero / non-finite query yields no hits). Candidates come from the
    /// quantised matrix (top ≈ 4 × <paramref name="topK"/> files by approximate per-file max), then every chunk of those
    /// files is re-scored exactly with the float32 vectors from SQLite. An empty <see cref="SearchFilter.Kinds"/> set is
    /// treated like null (no kind restriction). <see cref="SearchFilter.ModifiedAfter"/> is exclusive.
    /// </remarks>
    public IReadOnlyList<StoreHit> Search(ReadOnlySpan<float> query, SearchFilter filter, int topK)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        filter ??= SearchFilter.None;
        if (topK <= 0)
            return [];

        var dims = Volatile.Read(ref _dimensions);
        if (dims == 0)
            throw new InvalidOperationException("The index store is not open.");
        if (query.Length != dims)
            throw new ArgumentException($"Query has {query.Length} dimensions; the index uses {dims}.", nameof(query));

        var q = query.ToArray();
        if (!VectorMath.TryNormalize(q))
            return [];

        bool[]? allowedKinds = null;
        if (filter.Kinds is { Count: > 0 } kinds)
        {
            allowedKinds = new bool[256];
            foreach (var kind in kinds)
            {
                if ((uint)kind < 256)
                    allowedKinds[(int)kind] = true;
            }
        }
        var modifiedAfter = filter.ModifiedAfter?.UtcTicks ?? long.MinValue;
        var candidateCount = Math.Max(topK * 4, 16);

        List<QuantizedVectorIndex.Candidate> candidates;
        _indexLock.EnterReadLock();
        try
        {
            if (_index is null)
                throw new InvalidOperationException("The index store is not open.");
            if (_index.Dimensions != q.Length)
                throw new ArgumentException("Query dimensionality does not match the index.", nameof(query));
            candidates = _index.Search(q, allowedKinds, modifiedAfter, candidateCount);
        }
        finally
        {
            _indexLock.ExitReadLock();
        }

        if (candidates.Count == 0)
            return [];

        var hits = Rescore(q, candidates, allowedKinds, modifiedAfter);
        hits.Sort(static (a, b) =>
        {
            var c = b.Score.CompareTo(a.Score);
            return c != 0 ? c : string.CompareOrdinal(a.Path, b.Path);
        });
        if (hits.Count > topK)
            hits.RemoveRange(topK, hits.Count - topK);
        return hits;
    }

    /// <summary>SQLite host parameters per re-scoring query (well below SQLITE_MAX_VARIABLE_NUMBER).</summary>
    private const int RescoreBatchSize = 500;

    private List<StoreHit> Rescore(float[] query, List<QuantizedVectorIndex.Candidate> candidates, bool[]? allowedKinds, long modifiedAfter)
    {
        return WithReader(db =>
        {
            var best = new Dictionary<long, StoreHit>(candidates.Count);
            var vector = new float[query.Length];
            var blob = new byte[query.Length * sizeof(float)];

            for (var offset = 0; offset < candidates.Count; offset += RescoreBatchSize)
            {
                var batch = Math.Min(RescoreBatchSize, candidates.Count - offset);
                using var cmd = db.CreateCommand();
                var sql = new StringBuilder("""
                    SELECT c.file_id, c.idx, c.vector, c.text, f.path, f.kind, f.size, f.mtime_utc
                    FROM chunks c JOIN files f ON f.id = c.file_id
                    WHERE c.file_id IN (
                    """);
                for (var i = 0; i < batch; i++)
                {
                    var name = "$f" + i.ToString(CultureInfo.InvariantCulture);
                    sql.Append(i == 0 ? name : "," + name);
                    cmd.Parameters.AddWithValue(name, candidates[offset + i].FileId);
                }
                sql.Append(");");
                cmd.CommandText = sql.ToString();

                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    var kind = (FileKind)reader.GetInt32(5);
                    var ticks = reader.GetInt64(7);
                    // Re-check filters: the row may have changed since the matrix scan.
                    if (allowedKinds is not null && ((uint)kind >= 256 || !allowedKinds[(int)kind]))
                        continue;
                    if (ticks <= modifiedAfter)
                        continue;
                    if (reader.GetBytes(2, 0, null, 0, 0) != blob.Length)
                        continue;
                    reader.GetBytes(2, 0, blob, 0, blob.Length);
                    VectorMath.TryReadBlob(blob, vector);
                    VectorMath.TryNormalize(vector);
                    var score = VectorMath.Dot(query, vector);

                    var fileId = reader.GetInt64(0);
                    if (best.TryGetValue(fileId, out var existing) && existing.Score >= score)
                        continue;
                    best[fileId] = new StoreHit(
                        Path: reader.GetString(4),
                        Kind: kind,
                        Size: reader.GetInt64(6),
                        ModifiedUtc: new DateTimeOffset(ticks, TimeSpan.Zero),
                        Score: score,
                        ChunkIndex: reader.GetInt32(1),
                        ChunkText: reader.GetString(3));
                }
            }
            return best.Values.ToList();
        });
    }

    /// <inheritdoc />
    /// <remarks>Counts are maintained in memory (no table scans), so this is cheap enough to poll.</remarks>
    public IndexStats GetStats()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        long chunks;
        _indexLock.EnterReadLock();
        try
        {
            chunks = _index?.LiveRows ?? 0;
        }
        finally
        {
            _indexLock.ExitReadLock();
        }
        return new IndexStats(
            Interlocked.Read(ref _fileCount),
            chunks,
            Interlocked.Read(ref _frequentCount),
            Interlocked.Read(ref _allCount));
    }

    /// <inheritdoc />
    public string? GetMeta(string key)
    {
        ArgumentNullException.ThrowIfNull(key);
        return WithReader(db => ReadMeta(db, key, null));
    }

    /// <inheritdoc />
    public void SetMeta(string key, string value)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(value);
        lock (_writeGate)
        {
            var db = RequireWriter();
            WriteMeta(db, key, value, null);
        }
    }

    private static string? ReadMeta(SqliteConnection db, string key, SqliteTransaction? tx)
    {
        using var cmd = db.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "SELECT value FROM meta WHERE key = $key;";
        cmd.Parameters.AddWithValue("$key", key);
        return cmd.ExecuteScalar() as string;
    }

    private static void WriteMeta(SqliteConnection db, string key, string value, SqliteTransaction? tx)
    {
        using var cmd = db.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "INSERT INTO meta(key, value) VALUES ($key, $value) ON CONFLICT(key) DO UPDATE SET value = excluded.value;";
        cmd.Parameters.AddWithValue("$key", key);
        cmd.Parameters.AddWithValue("$value", value);
        cmd.ExecuteNonQuery();
    }

    private static void Execute(SqliteConnection db, string sql, SqliteTransaction? tx = null)
    {
        using var cmd = db.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    private SqliteConnection RequireWriter()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return _writer ?? throw new InvalidOperationException("The index store is not open; call Open first.");
    }

    /// <summary>Runs <paramref name="action"/> on a pooled read-only connection (WAL readers never block the writer).</summary>
    private T WithReader<T>(Func<SqliteConnection, T> action)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var connectionString = Volatile.Read(ref _connectionString)
            ?? throw new InvalidOperationException("The index store is not open; call Open first.");

        if (!_readers.TryTake(out var db))
        {
            db = new SqliteConnection(connectionString);
            try
            {
                db.Open();
                Execute(db, $"PRAGMA busy_timeout={BusyTimeoutMs};");
                Execute(db, "PRAGMA query_only=1;");
            }
            catch
            {
                db.Dispose();
                throw;
            }
        }

        var reusable = true;
        try
        {
            return action(db);
        }
        catch (SqliteException)
        {
            reusable = false;
            throw;
        }
        finally
        {
            if (reusable && !_disposed && _readers.Count < MaxPooledReaders
                && string.Equals(db.ConnectionString, Volatile.Read(ref _connectionString), StringComparison.Ordinal))
            {
                _readers.Add(db);
                if (_disposed && _readers.TryTake(out var late))
                    late.Dispose();
            }
            else
            {
                db.Dispose();
            }
        }
    }

    private void CloseConnections()
    {
        while (_readers.TryTake(out var reader))
            reader.Dispose();
        _writer?.Dispose();
        _writer = null;
    }

    /// <summary>Closes the database. Searches still running finish on their own connections.</summary>
    public void Dispose()
    {
        lock (_writeGate)
        {
            if (_disposed)
                return;
            _disposed = true;
            CloseConnections();
            _connectionString = null;
        }

        _indexLock.EnterWriteLock();
        try
        {
            _index = null;
        }
        finally
        {
            _indexLock.ExitWriteLock();
        }
        _indexLock.Dispose();
    }
}
