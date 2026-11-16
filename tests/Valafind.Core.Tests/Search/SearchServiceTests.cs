using Valafind.Core.Abstractions;
using Valafind.Core.Search;
using Valafind.Core.Storage;
using Valafind.Core.Tests.Storage;
using Xunit;
using static Valafind.Core.Tests.Search.FixedStore;

namespace Valafind.Core.Tests.Search;

public sealed class SearchServiceTests : IDisposable
{
    private readonly TempDatabase _db = new();
    private readonly HashingEmbeddingModel _model = new();

    public void Dispose() => _db.Dispose();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t\n")]
    public async Task Empty_query_returns_nothing_without_embedding(string query)
    {
        var store = new FixedStore(Hit("/a/b.txt", 0.9f, "text"));
        var service = new SearchService(_model, store);
        Assert.Empty(await service.SearchAsync(query, SearchFilter.None, 10, Ct));
        Assert.Equal(0, _model.QueryCalls);
        Assert.Equal(0, store.Searches);
    }

    [Fact]
    public async Task End_to_end_with_sqlite_store_finds_and_explains_best_file()
    {
        using var store = new SqliteIndexStore(_db.Path, PathKeyPolicy.CaseSensitive);
        store.Open(_model.ModelId, _model.Dimensions);
        var docs = new Dictionary<string, string>
        {
            ["/home/u/finance/plan.txt"] = "Quarterly planning notes. The marketing budget for next year is set at two million. Hiring remains frozen.",
            ["/home/u/recipes/soup.md"] = "Chop the onions finely. Simmer the soup for forty minutes. Season to taste.",
            ["/home/u/travel/japan.txt"] = "Flights to Tokyo were booked in March. The hotel near the station was cheap.",
        };
        foreach (var (path, text) in docs)
        {
            store.UpsertFile(new FileEntry(path, text.Length, DateTimeOffset.UtcNow, FileKind.Text, IndexPhase.All),
                [new ChunkRecord(0, 0, text.Length, text, _model.Embed(text))]);
        }

        var service = new SearchService(_model, store);
        var results = await service.SearchAsync("  marketing budget  ", SearchFilter.None, 3, Ct);

        Assert.NotEmpty(results);
        var top = results[0];
        Assert.Equal("/home/u/finance/plan.txt", top.Path);
        Assert.Equal("plan.txt", top.FileName);
        Assert.Equal("/home/u/finance", top.Directory);
        Assert.Equal(FileKind.Text, top.Kind);
        Assert.Contains("marketing budget", top.Snippet, StringComparison.Ordinal);
        var highlight = Assert.Single(top.Highlights);
        Assert.Equal("The marketing budget for next year is set at two million.", top.Snippet.Substring(highlight.Start, highlight.Length));
        Assert.InRange(top.Score, -1f, 1f);
        Assert.Equal(1, _model.DocumentCalls);
    }

    [Fact]
    public async Task Sentence_embedding_is_one_batch_for_top_five_only()
    {
        var hits = Enumerable.Range(0, 8)
            .Select(i => Hit($"/d/file{i}.txt", 0.9f - i * 0.01f, $"First sentence about apples {i}. Second sentence about pears {i}! Third one?"))
            .ToArray();
        var service = new SearchService(_model, new FixedStore(hits));

        var results = await service.SearchAsync("pears", SearchFilter.None, 8, Ct);

        Assert.Equal(8, results.Count);
        Assert.Equal(1, _model.DocumentCalls);
        Assert.True(_model.DocumentBatches.TryDequeue(out var batch));
        Assert.Equal(5 * 3, batch.Count);
        Assert.Equal(
            Enumerable.Range(0, 5).Select(i => $"file{i}.txt").ToHashSet(),
            batch.Select(d => d.Title).ToHashSet());

        foreach (var r in results.Take(5))
        {
            var span = Assert.Single(r.Highlights);
            Assert.StartsWith("Second sentence about pears", r.Snippet.Substring(span.Start, span.Length), StringComparison.Ordinal);
        }
        foreach (var r in results.Skip(5))
        {
            // Lexical highlights: the word "pears".
            var span = Assert.Single(r.Highlights);
            Assert.Equal("pears", r.Snippet.Substring(span.Start, span.Length));
        }
    }

    [Fact]
    public async Task Model_failure_during_highlighting_falls_back_to_lexical()
    {
        _model.OnEmbedDocuments = _ => throw new InvalidOperationException("onnx exploded");
        var service = new SearchService(_model, new FixedStore(
            Hit("/d/a.txt", 0.8f, "Nothing here. The invoices were filed late. Invoicing is hard."),
            Hit("/d/b.txt", 0.7f, "Other text. More text.")));

        var results = await service.SearchAsync("invoice filing", SearchFilter.None, 5, Ct);

        Assert.Equal(2, results.Count);
        var words = results[0].Highlights.Select(h => results[0].Snippet.Substring(h.Start, h.Length)).ToList();
        Assert.Equal(["invoices", "filed", "Invoicing"], words);
        Assert.Empty(results[1].Highlights);
    }

    [Fact]
    public async Task Highlight_budget_exceeded_falls_back_to_lexical_without_throwing()
    {
        _model.OnEmbedDocuments = ct => Task.Delay(Timeout.Infinite, ct).GetAwaiter().GetResult();
        var service = new SearchService(_model, new FixedStore(Hit("/d/a.txt", 0.8f, "One. Two budget lines. Three.")))
        {
            HighlightBudget = TimeSpan.FromMilliseconds(50),
        };
        var result = Assert.Single(await service.SearchAsync("budget", SearchFilter.None, 5, Ct));
        var span = Assert.Single(result.Highlights);
        Assert.Equal("budget", result.Snippet.Substring(span.Start, span.Length));
    }

    [Fact]
    public async Task Name_only_files_have_empty_snippet_and_highlights()
    {
        var service = new SearchService(_model, new FixedStore(Hit("/pics/holiday beach.jpg", 0.6f, "", FileKind.Image)));
        var result = Assert.Single(await service.SearchAsync("beach", SearchFilter.None, 5, Ct));
        Assert.Equal("", result.Snippet);
        Assert.Empty(result.Highlights);
        Assert.Equal("holiday beach.jpg", result.FileName);
        Assert.Equal(0, _model.DocumentCalls);
    }

    [Fact]
    public async Task File_name_match_boosts_and_resorts()
    {
        var service = new SearchService(_model, new FixedStore(
            Hit("/d/notes.txt", 0.50f, "Some text."),
            Hit("/d/Budget-2024.xlsx", 0.48f, "Other text.", FileKind.Spreadsheet)));
        var results = await service.SearchAsync("budgets", SearchFilter.None, 5, Ct);
        Assert.Equal("/d/Budget-2024.xlsx", results[0].Path);
        Assert.Equal(0.51f, results[0].Score, 4);
        Assert.Equal(0.50f, results[1].Score, 4);
    }

    [Fact]
    public async Task Boost_never_pushes_score_above_one()
    {
        var service = new SearchService(_model, new FixedStore(Hit("/d/budget.txt", 0.99f, "x")));
        var result = Assert.Single(await service.SearchAsync("budget", SearchFilter.None, 5, Ct));
        Assert.Equal(1f, result.Score);
    }

    [Fact]
    public async Task Already_cancelled_token_throws_before_embedding()
    {
        var service = new SearchService(_model, new FixedStore(Hit("/a", 1, "x")));
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.SearchAsync("query", SearchFilter.None, 5, cts.Token));
        Assert.Equal(0, _model.QueryCalls);
    }

    [Fact]
    public async Task Cancellation_during_query_embedding_throws()
    {
        using var cts = new CancellationTokenSource();
        using var entered = new ManualResetEventSlim();
        _model.OnEmbedQuery = ct =>
        {
            entered.Set();
            ct.WaitHandle.WaitOne(TimeSpan.FromSeconds(10));
        };
        var store = new FixedStore(Hit("/a", 1, "x"));
        var service = new SearchService(_model, store);

        var task = service.SearchAsync("query", SearchFilter.None, 5, cts.Token);
        Assert.True(entered.Wait(TimeSpan.FromSeconds(5), Ct));
        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
        Assert.Equal(0, store.Searches);
    }

    [Fact]
    public async Task Cancellation_right_after_embedding_skips_store_search()
    {
        using var cts = new CancellationTokenSource();
        // The model finishes normally, but the keystroke that cancels arrives just before it returns.
        var store = new FixedStore(Hit("/a", 1, "x"));
        var model = new NonThrowingQueryModel(_model, () => cts.Cancel());
        var service = new SearchService(model, store);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.SearchAsync("query", SearchFilter.None, 5, cts.Token));
        Assert.Equal(0, store.Searches);
    }

    [Fact]
    public async Task Snippet_and_highlight_bounds_hold_for_random_texts()
    {
        var rng = new Random(1234);
        string[] vocabulary =
        [
            "alpha", "budget", "Budgets", "budgeting", "report", "naïve", "café", "Straße", "日本語", "😀", "x",
            "supercalifragilisticexpialidocious", "e.g.", "3.14", "end.", "Why?", "wow!", "…", "\n", "\n\n", "\t", "  ",
        ];
        var hits = new List<StoreHit>();
        for (var i = 0; i < 40; i++)
        {
            var words = Enumerable.Range(0, rng.Next(0, 400)).Select(_ => vocabulary[rng.Next(vocabulary.Length)]);
            var text = string.Join(rng.Next(3) == 0 ? "" : " ", words);
            if (i % 7 == 0)
                text = new string('a', rng.Next(1, 1000)); // one enormous word
            hits.Add(Hit($"/r/{i}.txt", 0.9f - i * 0.001f, text));
        }

        foreach (var query in new[] { "budget report", "café straße", "日本語", "😀 alpha", "zzz" })
        {
            var service = new SearchService(_model, new FixedStore([.. hits]));
            var results = await service.SearchAsync(query, SearchFilter.None, hits.Count, Ct);
            Assert.Equal(hits.Count, results.Count);
            foreach (var r in results)
            {
                Assert.InRange(r.Snippet.Length, 0, Snippet.TargetLength + 2);
                if (string.IsNullOrWhiteSpace(hits.Single(h => h.Path == r.Path).ChunkText))
                    Assert.Equal("", r.Snippet);
                Assert.DoesNotContain('\n', r.Snippet);
                foreach (var h in r.Highlights)
                {
                    Assert.True(h.Length > 0);
                    Assert.InRange(h.Start, 0, r.Snippet.Length - 1);
                    Assert.InRange(h.Start + h.Length, 1, r.Snippet.Length);
                    Assert.False(char.IsLowSurrogate(r.Snippet[h.Start]), "highlight splits a surrogate pair");
                }
            }
        }
    }

    /// <summary>Delegates to the hashing model but runs a callback after the query vector is computed.</summary>
    private sealed class NonThrowingQueryModel(HashingEmbeddingModel inner, Action afterEmbed) : IEmbeddingModel
    {
        public string ModelId => inner.ModelId;
        public int Dimensions => inner.Dimensions;
        public int MaxInputTokens => inner.MaxInputTokens;
        public int CountTokens(string text) => inner.CountTokens(text);

        public float[] EmbedQuery(string query, CancellationToken cancellationToken = default)
        {
            var v = inner.Embed(query);
            afterEmbed();
            return v;
        }

        public IReadOnlyList<float[]> EmbedDocuments(IReadOnlyList<DocumentText> documents, CancellationToken cancellationToken = default) =>
            inner.EmbedDocuments(documents, cancellationToken);

        public void Dispose()
        {
        }
    }
}
