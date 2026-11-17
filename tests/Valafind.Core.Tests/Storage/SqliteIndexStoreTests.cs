using static Valafind.Core.Tests.TestPaths;
using Microsoft.Data.Sqlite;
using Valafind.Core.Abstractions;
using Valafind.Core.Storage;
using Xunit;
using static Valafind.Core.Tests.Storage.Entries;

namespace Valafind.Core.Tests.Storage;

public sealed class SqliteIndexStoreTests : IDisposable
{
    private const int Dims = 8;
    private readonly TempDatabase _db = new();

    public void Dispose() => _db.Dispose();

    [Fact]
    public void Upsert_then_fingerprint_and_search()
    {
        using var store = _db.OpenStore();
        var modified = new DateTimeOffset(2024, 1, 2, 3, 4, 5, 678, TimeSpan.FromHours(2));
        store.UpsertFile(File(P("/home/a/notes.txt"), size: 1234, modified: modified), [Chunk(0, Vectors.Axis(Dims, 0), "hello world")]);

        var fp = store.GetFingerprint(P("/home/a/notes.txt"));
        Assert.NotNull(fp);
        Assert.Equal(1234, fp.Size);
        Assert.Equal(modified, fp.ModifiedUtc);
        Assert.Equal(TimeSpan.Zero, fp.ModifiedUtc.Offset);
        Assert.Null(store.GetFingerprint(P("/home/a/other.txt")));

        var hits = store.Search(Vectors.Axis(Dims, 0), SearchFilter.None, 10);
        var hit = Assert.Single(hits);
        Assert.Equal(P("/home/a/notes.txt"), hit.Path);
        Assert.Equal(FileKind.Text, hit.Kind);
        Assert.Equal(1234, hit.Size);
        Assert.Equal("hello world", hit.ChunkText);
        Assert.Equal(0, hit.ChunkIndex);
        Assert.Equal(1f, hit.Score, 4);
    }

    [Fact]
    public void Upsert_replaces_previous_chunks()
    {
        using var store = _db.OpenStore();
        store.UpsertFile(File(P("/x/a.txt")), [Chunk(0, Vectors.Axis(Dims, 0), "old 0"), Chunk(1, Vectors.Axis(Dims, 1), "old 1")]);
        store.UpsertFile(File(P("/x/a.txt"), size: 7, phase: IndexPhase.Incremental), [Chunk(0, Vectors.Axis(Dims, 2), "new")]);

        var stats = store.GetStats();
        Assert.Equal(1, stats.Files);
        Assert.Equal(1, stats.Chunks);
        Assert.Equal(7, store.GetFingerprint(P("/x/a.txt"))!.Size);

        var hit = Assert.Single(store.Search(Vectors.Axis(Dims, 2), SearchFilter.None, 5));
        Assert.Equal("new", hit.ChunkText);
        Assert.Equal(1f, hit.Score, 4);

        // The old vectors are gone: searching for them only finds the new chunk at ~0.
        var old = Assert.Single(store.Search(Vectors.Axis(Dims, 0), SearchFilter.None, 5));
        Assert.Equal("new", old.ChunkText);
        Assert.Equal(0f, old.Score, 4);
    }

    [Fact]
    public void Upsert_with_no_chunks_records_fingerprint_but_is_not_searchable()
    {
        using var store = _db.OpenStore();
        store.UpsertFile(File(P("/x/empty.bin")), []);
        Assert.NotNull(store.GetFingerprint(P("/x/empty.bin")));
        Assert.Empty(store.Search(Vectors.Axis(Dims, 0), SearchFilter.None, 5));
        Assert.Equal(new IndexStats(1, 0, 0, 1), store.GetStats());
    }

    [Fact]
    public void Upsert_rejects_wrong_dimensions_and_duplicates_without_side_effects()
    {
        using var store = _db.OpenStore();
        Assert.Throws<ArgumentException>(() => store.UpsertFile(File(P("/x/a")), [Chunk(0, new float[Dims + 1])]));
        Assert.Throws<ArgumentException>(() => store.UpsertFile(File(P("/x/a")), [Chunk(0, Vectors.Axis(Dims, 0)), Chunk(0, Vectors.Axis(Dims, 1))]));
        Assert.Null(store.GetFingerprint(P("/x/a")));
        Assert.Equal(new IndexStats(0, 0, 0, 0), store.GetStats());
    }

    [Fact]
    public void RemoveFile_removes_rows_and_vectors()
    {
        using var store = _db.OpenStore();
        store.UpsertFile(File(P("/x/a.txt")), [Chunk(0, Vectors.Axis(Dims, 0))]);
        store.UpsertFile(File(P("/x/b.txt")), [Chunk(0, Vectors.Axis(Dims, 1))]);

        store.RemoveFile(P("/x/a.txt"));
        store.RemoveFile(P("/x/never-indexed.txt"));

        Assert.Null(store.GetFingerprint(P("/x/a.txt")));
        var hits = store.Search(Vectors.Axis(Dims, 0), SearchFilter.None, 10);
        Assert.Equal([P("/x/b.txt")], hits.Select(h => h.Path));
        Assert.Equal(1, store.GetStats().Files);
    }

    [Theory]
    [InlineData("/home/a/docs")]
    [InlineData("/home/a/docs/")]
    [InlineData("/home/a/docs//")]
    public void RemoveUnder_matches_whole_path_components(string prefix)
    {
        using var store = _db.OpenStore();
        string[] paths =
        [
            P("/home/a/docs/x.txt"), P("/home/a/docs/sub/y.txt"), P("/home/a/docs"), // removed
            P("/home/a/docs2/x.txt"), P("/home/a/docs.txt"), P("/home/a/doc/x.txt"), P("/home/a/Docs/x.txt"), P("/home/b/docs/x.txt"), // kept
        ];
        for (var i = 0; i < paths.Length; i++)
            store.UpsertFile(File(paths[i]), [Chunk(0, Vectors.Axis(Dims, i % Dims))]);

        Assert.Equal(3, store.RemoveUnder(P(prefix)));

        Assert.Null(store.GetFingerprint(P("/home/a/docs/x.txt")));
        Assert.Null(store.GetFingerprint(P("/home/a/docs/sub/y.txt")));
        Assert.Null(store.GetFingerprint(P("/home/a/docs")));
        foreach (var kept in paths.Skip(3))
            Assert.NotNull(store.GetFingerprint(kept));
        Assert.Equal(5, store.GetStats().Files);
        Assert.Equal(5, store.Search(Vectors.Axis(Dims, 0, 0.1f), SearchFilter.None, 100).Count);
    }

    [Fact]
    public void RemoveUnder_root_removes_everything_and_unknown_prefix_nothing()
    {
        using var store = _db.OpenStore();
        store.UpsertFile(File(P("/a/x")), [Chunk(0, Vectors.Axis(Dims, 0))]);
        store.UpsertFile(File(P("/b/y")), [Chunk(0, Vectors.Axis(Dims, 1))]);
        Assert.Equal(0, store.RemoveUnder(P("/c")));
        Assert.Equal(2, store.RemoveUnder(P("/")));
        Assert.Equal(new IndexStats(0, 0, 0, 0), store.GetStats());
    }

    [Fact]
    public void Case_insensitive_policy_treats_case_variants_as_one_file()
    {
        using var store = _db.OpenStore(policy: PathKeyPolicy.CaseInsensitiveNfc);
        store.UpsertFile(File(P("/Users/A/Report.PDF")), [Chunk(0, Vectors.Axis(Dims, 0))]);
        store.UpsertFile(File(P("/users/a/report.pdf")), [Chunk(0, Vectors.Axis(Dims, 1))]);

        Assert.Equal(1, store.GetStats().Files);
        Assert.NotNull(store.GetFingerprint(P("/USERS/a/REPORT.pdf")));
        // The latest spelling is what is reported.
        Assert.Equal(P("/users/a/report.pdf"), Assert.Single(store.Search(Vectors.Axis(Dims, 1), SearchFilter.None, 5)).Path);

        store.UpsertFile(File(P("/Users/A/Docs2/x")), [Chunk(0, Vectors.Axis(Dims, 2))]);
        Assert.Equal(2, store.RemoveUnder(P("/USERS/a/")));
        Assert.Equal(0, store.GetStats().Files);
    }

    [Fact]
    public void Case_sensitive_policy_keeps_case_variants_apart()
    {
        using var store = _db.OpenStore(policy: PathKeyPolicy.CaseSensitive);
        store.UpsertFile(File(P("/a/Report.pdf")), [Chunk(0, Vectors.Axis(Dims, 0))]);
        store.UpsertFile(File(P("/a/report.pdf")), [Chunk(0, Vectors.Axis(Dims, 1))]);
        Assert.Equal(2, store.GetStats().Files);
        Assert.Null(store.GetFingerprint(P("/a/REPORT.pdf")));
    }

    [Fact]
    public void Data_persists_across_reopen_with_same_model()
    {
        var rng = new Random(1);
        var vectors = Enumerable.Range(0, 20).Select(_ => Vectors.Random(rng, Dims)).ToArray();
        using (var store = _db.OpenStore())
        {
            for (var i = 0; i < 10; i++)
                store.UpsertFile(File(P($"/f/{i}"), phase: i < 4 ? IndexPhase.Frequent : IndexPhase.All),
                    [Chunk(0, vectors[2 * i]), Chunk(1, vectors[2 * i + 1])]);
            store.SetMeta("custom", "value");
        }

        using var reopened = _db.OpenStore();
        Assert.Equal(new IndexStats(10, 20, 4, 6), reopened.GetStats());
        Assert.Equal("value", reopened.GetMeta("custom"));
        var hit = reopened.Search(vectors[7], SearchFilter.None, 1)[0];
        Assert.Equal(P("/f/3"), hit.Path);
        Assert.Equal(1, hit.ChunkIndex);
        Assert.Equal(1f, hit.Score, 4);
    }

    [Theory]
    [InlineData("other-model", Dims)]
    [InlineData("test-model", 16)]
    public void Changing_model_or_dimensions_wipes_vectors(string modelId, int dims)
    {
        using (var store = _db.OpenStore())
        {
            store.UpsertFile(File(P("/f/a")), [Chunk(0, Vectors.Axis(Dims, 0))]);
            store.SetMeta("keep", "me");
        }

        using var reopened = _db.OpenStore(modelId, dims);
        Assert.Equal(new IndexStats(0, 0, 0, 0), reopened.GetStats());
        Assert.Null(reopened.GetFingerprint(P("/f/a")));
        Assert.Equal(modelId, reopened.GetMeta("model_id"));
        Assert.Equal(dims.ToString(System.Globalization.CultureInfo.InvariantCulture), reopened.GetMeta("dimensions"));
        Assert.Equal("me", reopened.GetMeta("keep"));
        Assert.Empty(reopened.Search(Vectors.Axis(dims, 0), SearchFilter.None, 5));
    }

    [Fact]
    public void Search_aggregates_per_file_using_best_chunk()
    {
        using var store = _db.OpenStore();
        var q = Vectors.Axis(Dims, 0);
        store.UpsertFile(File(P("/f/many")), [
            Chunk(0, Vectors.Axis(Dims, 1), "far"),
            Chunk(1, Vectors.Normalize([0.9f, 0.1f, 0, 0, 0, 0, 0, 0]), "best"),
            Chunk(2, Vectors.Normalize([0.5f, 0.5f, 0, 0, 0, 0, 0, 0]), "middle"),
        ]);
        store.UpsertFile(File(P("/f/one")), [Chunk(0, Vectors.Normalize([0.7f, 0.3f, 0, 0, 0, 0, 0, 0]), "single")]);

        var hits = store.Search(q, SearchFilter.None, 10);
        Assert.Equal(2, hits.Count);
        Assert.Equal(P("/f/many"), hits[0].Path);
        Assert.Equal(1, hits[0].ChunkIndex);
        Assert.Equal("best", hits[0].ChunkText);
        Assert.Equal(Vectors.Normalize([0.9f, 0.1f, 0, 0, 0, 0, 0, 0])[0], hits[0].Score, 4);
        Assert.Equal(P("/f/one"), hits[1].Path);
        Assert.True(hits[0].Score >= hits[1].Score);

        Assert.Single(store.Search(q, SearchFilter.None, 1));
    }

    [Fact]
    public void Search_applies_kind_and_modified_filters()
    {
        using var store = _db.OpenStore();
        var q = Vectors.Axis(Dims, 0);
        store.UpsertFile(File(P("/f/old.pdf"), FileKind.Pdf, modified: BaseTime), [Chunk(0, q)]);
        store.UpsertFile(File(P("/f/new.pdf"), FileKind.Pdf, modified: BaseTime.AddDays(10)), [Chunk(0, q)]);
        store.UpsertFile(File(P("/f/new.cs"), FileKind.Code, modified: BaseTime.AddDays(10)), [Chunk(0, q)]);

        Assert.Equal(3, store.Search(q, SearchFilter.None, 10).Count);
        Assert.Equal(3, store.Search(q, new SearchFilter(new HashSet<FileKind>()), 10).Count);

        var pdfs = store.Search(q, new SearchFilter(new HashSet<FileKind> { FileKind.Pdf }), 10);
        Assert.Equal([P("/f/new.pdf"), P("/f/old.pdf")], pdfs.Select(h => h.Path).Order(StringComparer.Ordinal));

        var recent = store.Search(q, new SearchFilter(ModifiedAfter: BaseTime), 10);
        Assert.Equal([P("/f/new.cs"), P("/f/new.pdf")], recent.Select(h => h.Path).Order(StringComparer.Ordinal));

        var both = store.Search(q, new SearchFilter(new HashSet<FileKind> { FileKind.Pdf }, BaseTime.AddDays(1)), 10);
        Assert.Equal(P("/f/new.pdf"), Assert.Single(both).Path);

        Assert.Empty(store.Search(q, new SearchFilter(new HashSet<FileKind> { FileKind.Image }), 10));
    }

    [Fact]
    public void Search_normalises_query_and_handles_degenerate_input()
    {
        using var store = _db.OpenStore();
        store.UpsertFile(File(P("/f/a")), [Chunk(0, Vectors.Axis(Dims, 3))]);

        var scaled = Vectors.Axis(Dims, 3).Select(x => x * 42f).ToArray();
        Assert.Equal(1f, Assert.Single(store.Search(scaled, SearchFilter.None, 3)).Score, 4);
        Assert.Empty(store.Search(new float[Dims], SearchFilter.None, 3));
        Assert.Empty(store.Search(Enumerable.Repeat(float.NaN, Dims).ToArray(), SearchFilter.None, 3));
        Assert.Empty(store.Search(Vectors.Axis(Dims, 3), SearchFilter.None, 0));
        Assert.Throws<ArgumentException>(() => store.Search(new float[Dims + 2], SearchFilter.None, 3));
    }

    [Fact]
    public void Search_with_large_top_k_returns_every_file()
    {
        using var store = _db.OpenStore();
        var rng = new Random(2);
        for (var i = 0; i < 700; i++)
            store.UpsertFile(File(P($"/many/{i}")), [Chunk(0, Vectors.Random(rng, Dims))]);
        var hits = store.Search(Vectors.Random(rng, Dims), SearchFilter.None, 5000);
        Assert.Equal(700, hits.Count);
        Assert.Equal(700, hits.Select(h => h.Path).Distinct().Count());
    }

    [Fact]
    public void Stats_count_files_by_phase()
    {
        using var store = _db.OpenStore();
        store.UpsertFile(File(P("/f/1"), phase: IndexPhase.Frequent), [Chunk(0, Vectors.Axis(Dims, 0)), Chunk(1, Vectors.Axis(Dims, 1))]);
        store.UpsertFile(File(P("/f/2"), phase: IndexPhase.Frequent), [Chunk(0, Vectors.Axis(Dims, 0))]);
        store.UpsertFile(File(P("/f/3"), phase: IndexPhase.All), [Chunk(0, Vectors.Axis(Dims, 0))]);
        store.UpsertFile(File(P("/f/4"), phase: IndexPhase.Incremental), [Chunk(0, Vectors.Axis(Dims, 0))]);
        Assert.Equal(new IndexStats(4, 5, 2, 1), store.GetStats());

        // Re-indexing moves a file between phases.
        store.UpsertFile(File(P("/f/2"), phase: IndexPhase.All), [Chunk(0, Vectors.Axis(Dims, 0))]);
        Assert.Equal(new IndexStats(4, 5, 1, 2), store.GetStats());
        store.RemoveFile(P("/f/1"));
        Assert.Equal(new IndexStats(3, 3, 0, 2), store.GetStats());
    }

    [Fact]
    public void Counts_files_by_kind_live_and_after_reopening()
    {
        using (var store = _db.OpenStore())
        {
            Assert.Empty(store.GetFileCountsByKind());
            store.UpsertFile(File(P("/k/a.pdf"), FileKind.Pdf), [Chunk(0, Vectors.Axis(Dims, 0))]);
            store.UpsertFile(File(P("/k/b.pdf"), FileKind.Pdf), [Chunk(0, Vectors.Axis(Dims, 0))]);
            store.UpsertFile(File(P("/k/c.cs"), FileKind.Code), [Chunk(0, Vectors.Axis(Dims, 0))]);
            Assert.Equal(new Dictionary<FileKind, long> { [FileKind.Pdf] = 2, [FileKind.Code] = 1 }, store.GetFileCountsByKind());

            // Re-indexing a file under another kind moves it; removing drops it.
            store.UpsertFile(File(P("/k/b.pdf"), FileKind.Document), [Chunk(0, Vectors.Axis(Dims, 0))]);
            store.RemoveFile(P("/k/c.cs"));
            Assert.Equal(new Dictionary<FileKind, long> { [FileKind.Pdf] = 1, [FileKind.Document] = 1 }, store.GetFileCountsByKind());
        }

        using var reopened = _db.OpenStore();
        Assert.Equal(new Dictionary<FileKind, long> { [FileKind.Pdf] = 1, [FileKind.Document] = 1 }, reopened.GetFileCountsByKind());
        reopened.RemoveUnder(P("/k"));
        Assert.Empty(reopened.GetFileCountsByKind());
    }

    [Fact]
    public void Meta_round_trips_and_overwrites()
    {
        using var store = _db.OpenStore();
        Assert.Null(store.GetMeta("k"));
        store.SetMeta("k", "1");
        store.SetMeta("k", "2");
        Assert.Equal("2", store.GetMeta("k"));
        Assert.Equal("test-model", store.GetMeta("model_id"));
    }

    [Fact]
    public void Using_store_before_open_or_after_dispose_throws()
    {
        var store = new SqliteIndexStore(_db.Path);
        Assert.Throws<InvalidOperationException>(() => store.GetFingerprint(P("/a")));
        Assert.Throws<InvalidOperationException>(() => store.Search(new float[Dims], SearchFilter.None, 1));
        store.Open("m", Dims);
        store.Dispose();
        store.Dispose();
        Assert.Throws<ObjectDisposedException>(() => store.Search(new float[Dims], SearchFilter.None, 1));
        Assert.Throws<ObjectDisposedException>(() => store.UpsertFile(File(P("/a")), []));
    }

    [Fact]
    public void Database_uses_wal_and_expected_schema()
    {
        using (var store = _db.OpenStore())
            store.UpsertFile(File(P("/f/a")), [Chunk(0, Vectors.Axis(Dims, 0))]);

        using var conn = new SqliteConnection($"Data Source={_db.Path};Pooling=False");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "PRAGMA journal_mode;";
        Assert.Equal("wal", (string)cmd.ExecuteScalar()!);
        cmd.CommandText = "SELECT length(vector) FROM chunks;";
        Assert.Equal((long)Dims * sizeof(float), (long)cmd.ExecuteScalar()!);
        cmd.CommandText = "SELECT path, path_key, kind, phase FROM files;";
        using var reader = cmd.ExecuteReader();
        Assert.True(reader.Read());
        Assert.Equal(P("/f/a"), reader.GetString(0));
        Assert.Equal(P("/f/a"), reader.GetString(1));
        Assert.Equal((int)FileKind.Text, reader.GetInt32(2));
        Assert.Equal((int)IndexPhase.All, reader.GetInt32(3));
    }

    [Fact]
    public void Matrix_compacts_after_many_replacements_and_stays_correct()
    {
        using var store = _db.OpenStore();
        var rng = new Random(5);
        var latest = new Dictionary<string, float[]>();
        for (var round = 0; round < 6; round++)
        {
            for (var i = 0; i < 50; i++)
            {
                var v = Vectors.Random(rng, Dims);
                latest[P($"/f/{i}")] = v;
                store.UpsertFile(File(P($"/f/{i}")), [Chunk(0, v), Chunk(1, Vectors.Random(rng, Dims))]);
            }
        }

        var (rows, dead) = store.MatrixRows;
        Assert.True(dead * 4 <= rows, $"dead={dead} rows={rows}");
        Assert.Equal(100, rows - dead);

        foreach (var (path, v) in latest)
        {
            var top = store.Search(v, SearchFilter.None, 1)[0];
            Assert.Equal(path, top.Path);
            Assert.Equal(1f, top.Score, 4);
        }
    }

    [Fact]
    public void Quantised_search_matches_exact_brute_force_recall_at_10()
    {
        const int dims = 256;
        const int files = 5000;
        using var store = _db.OpenStore("recall", dims);
        var rng = new Random(42);
        var vectors = new float[files][];
        for (var i = 0; i < files; i++)
        {
            vectors[i] = Vectors.Random(rng, dims);
            store.UpsertFile(File(P($"/r/{i:D5}")), [Chunk(0, vectors[i])]);
        }

        var totalRecall = 0.0;
        const int queries = 50;
        for (var qi = 0; qi < queries; qi++)
        {
            // Queries near a stored vector (realistic) mixed with pure random ones.
            var query = qi % 2 == 0
                ? Vectors.Normalize(vectors[rng.Next(files)].Zip(Vectors.Random(rng, dims), (a, b) => a + 0.8f * b).ToArray())
                : Vectors.Random(rng, dims);
            var exact = Enumerable.Range(0, files)
                .OrderByDescending(i => Vectors.Dot(vectors[i], query))
                .Take(10)
                .Select(i => P($"/r/{i:D5}"))
                .ToHashSet();
            var got = store.Search(query, SearchFilter.None, 10);
            Assert.Equal(10, got.Count);
            for (var i = 1; i < got.Count; i++)
                Assert.True(got[i - 1].Score >= got[i].Score);
            totalRecall += got.Count(h => exact.Contains(h.Path)) / 10.0;
        }
        Assert.True(totalRecall / queries >= 0.98, $"recall@10 = {totalRecall / queries:F3}");
    }

    [Fact]
    public async Task Parallel_searches_while_upserting_and_removing_do_not_throw()
    {
        using var store = _db.OpenStore("conc", 32);
        var rng = new Random(9);
        for (var i = 0; i < 300; i++)
            store.UpsertFile(File(P($"/c/{i}")), [Chunk(0, Vectors.Random(rng, 32)), Chunk(1, Vectors.Random(rng, 32))]);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        // Dedicated threads (LongRunning): with blocking loops on the shared thread pool, a busy CI box running
        // the whole suite in parallel can leave a reader unscheduled for the entire window.
        var writer = Task.Factory.StartNew(() =>
        {
            var wrng = new Random(10);
            var n = 0;
            while (!cts.IsCancellationRequested)
            {
                var id = wrng.Next(400);
                if (n++ % 5 == 0)
                    store.RemoveFile(P($"/c/{id}"));
                else
                    store.UpsertFile(File(P($"/c/{id}")), [Chunk(0, Vectors.Random(wrng, 32))]);
                if (n % 97 == 0)
                    store.RemoveUnder(P($"/c/{wrng.Next(10)}"));
            }
            return n;
        }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);

        var readers = Enumerable.Range(0, 4).Select(seed => Task.Factory.StartNew(() =>
        {
            var rrng = new Random(100 + seed);
            var searches = 0;
            while (!cts.IsCancellationRequested)
            {
                var hits = store.Search(Vectors.Random(rrng, 32), SearchFilter.None, 10);
                Assert.True(hits.Count <= 10);
                Assert.Equal(hits.Count, hits.Select(h => h.Path).Distinct().Count());
                _ = store.GetStats();
                _ = store.GetFingerprint(P($"/c/{rrng.Next(400)}"));
                searches++;
            }
            return searches;
        }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default)).ToArray();

        var writes = await writer;
        var searchCounts = await Task.WhenAll(readers);
        Assert.True(writes > 10);
        Assert.All(searchCounts, c => Assert.True(c > 10));

        // In-memory state agrees with the database.
        var stats = store.GetStats();
        using var conn = new SqliteConnection($"Data Source={_db.Path};Pooling=False;Mode=ReadOnly");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT (SELECT COUNT(*) FROM files), (SELECT COUNT(*) FROM chunks);";
        using var reader = cmd.ExecuteReader();
        Assert.True(reader.Read());
        Assert.Equal(reader.GetInt64(0), stats.Files);
        Assert.Equal(reader.GetInt64(1), stats.Chunks);
    }
}
