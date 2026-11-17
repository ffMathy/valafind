using static Valafind.Core.Tests.TestPaths;
using Valafind.Core.Abstractions;
using Valafind.Core.Embedding;
using Valafind.Core.Search;
using Valafind.Core.Storage;
using Valafind.Core.Tests.Storage;
using Xunit;
using static Valafind.Core.Tests.Search.FixedStore;

namespace Valafind.Core.Tests.Search;

public sealed class SearchServiceTests : IDisposable
{
    // The hashing test model scores lower than EmbeddingGemma 2: explain thresholds that suit it.
    private const float HashingResultScore = 0.30f;
    private const float HashingSentenceScore = 0.25f;

    private static SearchService Service(IEmbeddingModel model, IIndexStore store) =>
        new(model, store, HashingResultScore, HashingSentenceScore);

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
        var store = new FixedStore(Hit(P("/a/b.txt"), 0.9f, "text"));
        var service = Service(_model, store);
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
            [P("/home/u/finance/plan.txt")] = "Quarterly planning notes. The marketing budget for next year is set at two million. Hiring remains frozen.",
            [P("/home/u/recipes/soup.md")] = "Chop the onions finely. Simmer the soup for forty minutes. Season to taste.",
            [P("/home/u/travel/japan.txt")] = "Flights to Tokyo were booked in March. The hotel near the station was cheap.",
        };
        foreach (var (path, text) in docs)
        {
            store.UpsertFile(new FileEntry(path, text.Length, DateTimeOffset.UtcNow, FileKind.Text, IndexPhase.All),
                [new ChunkRecord(0, 0, text.Length, text, _model.Embed(text))]);
        }

        var service = Service(_model, store);
        var results = await service.SearchAsync("  marketing budget  ", SearchFilter.None, 3, Ct);

        Assert.NotEmpty(results);
        var top = results[0];
        Assert.Equal(P("/home/u/finance/plan.txt"), top.Path);
        Assert.Equal("plan.txt", top.FileName);
        Assert.Equal(P("/home/u/finance"), top.Directory);
        Assert.Equal(FileKind.Text, top.Kind);
        Assert.Contains("marketing budget", top.Snippet, StringComparison.Ordinal);
        Assert.InRange(top.Score, -1f, 1f);
        // Fast path: lexical highlights only, no model work beyond the query.
        Assert.Equal(["marketing", "budget"], top.Highlights.Select(h => top.Snippet.Substring(h.Start, h.Length)));
        Assert.Equal(0, _model.DocumentCalls);
        Assert.Equal(1, _model.QueryCalls);

        // Second pass: the semantically best sentence.
        var explained = await service.ExplainAsync("  marketing budget  ", results, Ct);
        Assert.Equal(results.Count, explained.Count);
        var highlight = Assert.Single(explained[0].Highlights);
        Assert.Equal("The marketing budget for next year is set at two million.", explained[0].Snippet.Substring(highlight.Start, highlight.Length));
        Assert.Equal(top with { Highlights = explained[0].Highlights }, explained[0]);
        Assert.Equal(1, _model.DocumentCalls);
        Assert.Equal(1, _model.QueryCalls); // query vector reused from the search
    }

    [Fact]
    public async Task Search_never_waits_on_document_embedding()
    {
        _model.OnEmbedDocuments = ct => Task.Delay(Timeout.Infinite, ct).GetAwaiter().GetResult();
        var hits = Enumerable.Range(0, 8)
            .Select(i => Hit(P($"/d/file{i}.txt"), 0.9f - i * 0.01f, $"First sentence about apples {i}. Second sentence about pears {i}! Third one?"))
            .ToArray();
        var service = Service(_model, new FixedStore(hits));

        var results = await service.SearchAsync("pears", SearchFilter.None, 8, Ct);

        Assert.Equal(8, results.Count);
        Assert.Equal(0, _model.DocumentCalls);
        foreach (var r in results)
        {
            var span = Assert.Single(r.Highlights);
            Assert.Equal("pears", r.Snippet.Substring(span.Start, span.Length));
        }
    }

    [Fact]
    public async Task Explain_embeds_one_small_batch_for_the_top_two_without_title()
    {
        var hits = Enumerable.Range(0, 8)
            .Select(i => Hit(P($"/d/file{i}.txt"), 0.9f - i * 0.01f, $"First sentence about apples {i}. Second sentence about pears {i}! Third one?"))
            .ToArray();
        var service = Service(_model, new FixedStore(hits));
        var results = await service.SearchAsync("pears", SearchFilter.None, 8, Ct);

        var explained = await service.ExplainAsync("pears", results, Ct);

        Assert.Equal(8, explained.Count);
        Assert.Equal(1, _model.DocumentCalls);
        Assert.True(_model.DocumentBatches.TryDequeue(out var batch));
        Assert.Equal(SearchService.ExplainCount * 3, batch.Count);
        Assert.All(batch, d => Assert.Equal("none", d.Title));
        Assert.Equal(results.Select(r => r.Path), explained.Select(r => r.Path));
        foreach (var r in explained.Take(SearchService.ExplainCount))
        {
            var span = Assert.Single(r.Highlights);
            Assert.StartsWith("Second sentence about pears", r.Snippet.Substring(span.Start, span.Length), StringComparison.Ordinal);
        }
        for (var i = SearchService.ExplainCount; i < results.Count; i++)
            Assert.Same(results[i], explained[i]);
    }

    [Fact]
    public async Task Explain_embeds_at_most_four_lexically_preselected_sentences_per_result()
    {
        var sentences = Enumerable.Range(0, 12).Select(i => i is 2 or 9 ? $"Line {i} has the budget." : $"Line {i} filler.");
        var service = Service(_model, new FixedStore(Hit(P("/d/a.txt"), 0.8f, string.Join(' ', sentences))));
        var results = await service.SearchAsync("budget", SearchFilter.None, 5, Ct);

        await service.ExplainAsync("budget", results, Ct);

        Assert.True(_model.DocumentBatches.TryDequeue(out var batch));
        Assert.InRange(batch.Count, 2, SearchService.ExplainSentencesPerResult);
        Assert.Contains(batch, d => d.Text.Contains("budget", StringComparison.Ordinal));
        Assert.All(batch, d => Assert.DoesNotContain("…", d.Text, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Explain_returns_input_unchanged_when_the_model_fails()
    {
        var service = Service(_model, new FixedStore(
            Hit(P("/d/a.txt"), 0.8f, "Nothing here. The invoices were filed late. Invoicing is hard."),
            Hit(P("/d/b.txt"), 0.7f, "Other text. More text.")));
        var results = await service.SearchAsync("invoice filing", SearchFilter.None, 5, Ct);
        var words = results[0].Highlights.Select(h => results[0].Snippet.Substring(h.Start, h.Length)).ToList();
        Assert.Equal(["invoices", "filed", "Invoicing"], words);
        Assert.Empty(results[1].Highlights);

        _model.OnEmbedDocuments = _ => throw new InvalidOperationException("onnx exploded");
        Assert.Equal(results, await service.ExplainAsync("invoice filing", results, Ct));

        _model.OnEmbedDocuments = _ => throw new OperationCanceledException("internal timeout, not ours");
        Assert.Equal(results, await service.ExplainAsync("invoice filing", results, Ct));
    }

    [Fact]
    public async Task Explain_uses_interactive_priority_when_the_model_offers_it()
    {
        // ExplainAsync runs while the user looks at results: it must not queue behind an indexing batch.
        using var model = new InteractiveModel(_model);
        var service = Service(model, new FixedStore(
            Hit(P("/d/a.txt"), 0.8f, "Nothing here. The invoices were filed late. Invoicing is hard.")));
        // The hashing test model only relates identical words, so the query reuses the sentence's words.
        var results = await service.SearchAsync("invoices filed late", SearchFilter.None, 5, Ct);

        var explained = await service.ExplainAsync("invoices filed late", results, Ct);

        Assert.Equal(1, model.InteractiveCalls);
        Assert.Equal(0, model.BackgroundCalls);
        var why = Assert.Single(explained[0].Highlights);
        Assert.Equal("The invoices were filed late.", explained[0].Snippet.Substring(why.Start, why.Length));
    }

    [Fact]
    public async Task Mail_headers_are_separate_sentences_so_the_explanation_is_the_body_line()
    {
        // Chunk text keeps line breaks; without them the headers and the first sentence fused into one "sentence".
        const string mail = "Subject: Re: Depositum\nFrom: Henrik Larsen\nTo: Alex\nDate: 2024-02-21\nHi Alex, we can accept two months of deposit instead of three.";
        var service = Service(_model, new FixedStore(Hit(P("/m/re.eml"), 0.8f, mail)));
        var results = await service.SearchAsync("accept two months of deposit instead of three", SearchFilter.None, 5, Ct);
        var explained = await service.ExplainAsync("accept two months of deposit instead of three", results, Ct);

        var why = Assert.Single(explained[0].Highlights);
        var sentence = explained[0].Snippet.Substring(why.Start, why.Length);
        Assert.StartsWith("Hi Alex", sentence, StringComparison.Ordinal);
        Assert.DoesNotContain("Subject", sentence, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Explain_invents_no_reason_for_weak_results_or_unrelated_sentences()
    {
        var service = Service(_model, new FixedStore(
            Hit(P("/d/strong.txt"), 0.8f, "Unrelated opener here. Quarterly budget review notes. Another thing entirely."),
            Hit(P("/d/weak.txt"), HashingResultScore - 0.05f, "Some words. Quarterly budget review notes. More words.")));
        var results = await service.SearchAsync("quarterly budget review", SearchFilter.None, 5, Ct);
        var explained = await service.ExplainAsync("quarterly budget review", results, Ct);

        var strong = explained.Single(r => r.Path.EndsWith("strong.txt", StringComparison.Ordinal));
        var why = Assert.Single(strong.Highlights);
        Assert.Equal("Quarterly budget review notes.", strong.Snippet.Substring(why.Start, why.Length));
        // Below the relevance bar the result keeps its lexical highlights untouched.
        var weakBefore = results.Single(r => r.Path.EndsWith("weak.txt", StringComparison.Ordinal));
        Assert.Equal(weakBefore.Highlights, explained.Single(r => r.Path.EndsWith("weak.txt", StringComparison.Ordinal)).Highlights);

        // No sentence reaches the similarity bar: lexical highlights are kept rather than picking a random sentence.
        var none = Service(_model, new FixedStore(Hit(P("/d/x.txt"), 0.8f, "Alpha beta gamma. Delta epsilon zeta. Eta theta iota.")));
        var r2 = await none.SearchAsync("quarterly budget review", SearchFilter.None, 5, Ct);
        Assert.Equal(r2[0].Highlights, (await none.ExplainAsync("quarterly budget review", r2, Ct))[0].Highlights);
    }

    [Fact]
    public async Task Explain_honours_cancellation()
    {
        var service = Service(_model, new FixedStore(Hit(P("/d/a.txt"), 0.8f, "One. Two budget lines. Three.")));
        var results = await service.SearchAsync("budget", SearchFilter.None, 5, Ct);
        using var cts = new CancellationTokenSource();
        using var entered = new ManualResetEventSlim();
        _model.OnEmbedDocuments = ct =>
        {
            entered.Set();
            ct.WaitHandle.WaitOne(TimeSpan.FromSeconds(10));
        };

        var task = service.ExplainAsync("budget", results, cts.Token);
        Assert.True(entered.Wait(TimeSpan.FromSeconds(5), Ct));
        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
    }

    [Fact]
    public async Task Explain_without_text_or_with_single_sentences_does_no_model_work()
    {
        var service = Service(_model, new FixedStore(
            Hit(P("/pics/beach.jpg"), 0.9f, "", FileKind.Image),
            Hit(P("/d/one.txt"), 0.8f, "Just one budget sentence")));
        var results = await service.SearchAsync("budget", SearchFilter.None, 5, Ct);
        Assert.Same(results, await service.ExplainAsync("budget", results, Ct));
        Assert.Same(results, await service.ExplainAsync("", results, Ct));
        Assert.Empty(await service.ExplainAsync("budget", [], Ct));
        Assert.Equal(0, _model.DocumentCalls);
    }

    [Fact]
    public async Task Name_only_files_have_empty_snippet_and_highlights()
    {
        var service = Service(_model, new FixedStore(Hit(P("/pics/holiday beach.jpg"), 0.6f, "", FileKind.Image)));
        var result = Assert.Single(await service.SearchAsync("beach", SearchFilter.None, 5, Ct));
        Assert.Equal("", result.Snippet);
        Assert.Empty(result.Highlights);
        Assert.Equal("holiday beach.jpg", result.FileName);
        Assert.Equal(0, _model.DocumentCalls);
    }

    [Fact]
    public async Task File_name_match_boosts_and_resorts()
    {
        var service = Service(_model, new FixedStore(
            Hit(P("/d/notes.txt"), 0.50f, "Some text."),
            Hit(P("/d/Budget-2024.xlsx"), 0.48f, "Other text.", FileKind.Spreadsheet)));
        var results = await service.SearchAsync("budgets", SearchFilter.None, 5, Ct);
        Assert.Equal(P("/d/Budget-2024.xlsx"), results[0].Path);
        Assert.Equal(0.51f, results[0].Score, 4);
        Assert.Equal(0.50f, results[1].Score, 4);
    }

    [Fact]
    public async Task Boost_never_pushes_score_above_one()
    {
        var service = Service(_model, new FixedStore(Hit(P("/d/budget.txt"), 0.99f, "x")));
        var result = Assert.Single(await service.SearchAsync("budget", SearchFilter.None, 5, Ct));
        Assert.Equal(1f, result.Score);
    }

    [Fact]
    public async Task File_name_boost_ignores_stopwords_and_substrings()
    {
        var service = Service(_model, new FixedStore(
            Hit(P("/d/incident-2024-11.pdf"), 0.50f, "Postmortem.", FileKind.Pdf),
            Hit(P("/d/Brother HL-L2350 manual.pdf"), 0.48f, "Printer.", FileKind.Pdf),
            Hit(P("/d/Mother's day.jpg"), 0.47f, "", FileKind.Image),
            Hit(P("/d/Fortress and Theory.txt"), 0.46f, "Other.")));

        var results = await service.SearchAsync("outage on the payment service and for", SearchFilter.None, 5, Ct);

        Assert.Equal(
            [P("/d/incident-2024-11.pdf"), P("/d/Brother HL-L2350 manual.pdf"), P("/d/Mother's day.jpg"), P("/d/Fortress and Theory.txt")],
            results.Select(r => r.Path));
        Assert.Equal([0.50f, 0.48f, 0.47f, 0.46f], results.Select(r => r.Score));
    }

    [Theory]
    [InlineData("tax return", "/d/TaxReturn2024.pdf")]
    [InlineData("2024 report", "/d/report_2024.docx")]
    [InlineData("parser", "/d/HTMLParser.cs")]
    [InlineData("invoices", "/d/invoice-march.pdf")]
    [InlineData("契約", "/d/賃貸契約書.pdf")]
    [InlineData("budget", "/d/budgetplan.xlsx")]
    [InlineData("tax return", "/d/taxreturn2024.pdf")]
    [InlineData("mietvertrag", "/d/Mietvertragsentwurf.pdf")]
    [InlineData("lejekontrakt", "/d/lejekontrakten.pdf")]
    public async Task File_name_boost_matches_whole_name_tokens(string query, string path)
    {
        var service = Service(_model, new FixedStore(
            Hit(P("/d/zzz.txt"), 0.50f, "x"),
            Hit(P(path), 0.48f, "y")));
        var results = await service.SearchAsync(query, SearchFilter.None, 5, Ct);
        Assert.Equal(P(path), results[0].Path);
        Assert.Equal(0.51f, results[0].Score, 4);
    }

    [Theory]
    [InlineData("tax", "/d/Taxonomy.pdf")]
    [InlineData("port", "/d/report.pdf")]
    [InlineData("pdf", "/d/notes.pdf")]
    [InlineData("plan", "/d/explanation.txt")]
    [InlineData("report", "/d/notes.report")]
    public async Task File_name_boost_does_not_fire_inside_tokens_or_on_the_extension(string query, string path)
    {
        var service = Service(_model, new FixedStore(Hit(P(path), 0.48f, "y")));
        var result = Assert.Single(await service.SearchAsync(query, SearchFilter.None, 5, Ct));
        Assert.Equal(0.48f, result.Score);
    }

    [Fact]
    public async Task File_name_match_just_below_top_k_is_promoted()
    {
        var service = Service(_model, new FixedStore(
            Hit(P("/d/a.txt"), 0.50f, "x"),
            Hit(P("/d/b.txt"), 0.49f, "x"),
            Hit(P("/d/budget.xlsx"), 0.48f, "x", FileKind.Spreadsheet)));
        var results = await service.SearchAsync("budget", SearchFilter.None, 2, Ct);
        Assert.Equal([P("/d/budget.xlsx"), P("/d/a.txt")], results.Select(r => r.Path));
    }

    [Fact]
    public async Task Already_cancelled_token_throws_before_embedding()
    {
        var service = Service(_model, new FixedStore(Hit(P("/a"), 1, "x")));
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
        var store = new FixedStore(Hit(P("/a"), 1, "x"));
        var service = Service(_model, store);

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
        var store = new FixedStore(Hit(P("/a"), 1, "x"));
        var model = new NonThrowingQueryModel(_model, () => cts.Cancel());
        var service = Service(model, store);

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
            hits.Add(Hit(P($"/r/{i}.txt"), 0.9f - i * 0.001f, text));
        }

        foreach (var query in new[] { "budget report", "café straße", "日本語", "😀 alpha", "zzz" })
        {
            var service = Service(_model, new FixedStore([.. hits]));
            var results = await service.SearchAsync(query, SearchFilter.None, hits.Count, Ct);
            Assert.Equal(hits.Count, results.Count);
            foreach (var r in results)
            {
                Assert.InRange(r.Snippet.Length, 0, Snippet.TargetLength + 2);
                if (string.IsNullOrWhiteSpace(hits.Single(h => h.Path == r.Path).ChunkText))
                    Assert.Equal("", r.Snippet);
                Assert.DoesNotContain("\n\n", r.Snippet, StringComparison.Ordinal);
                Assert.DoesNotContain('\r', r.Snippet);
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

    /// <summary>Delegates to the hashing model and counts interactive vs. background document calls.</summary>
    private sealed class InteractiveModel(HashingEmbeddingModel inner) : IEmbeddingModel, IInteractiveEmbedding
    {
        private int _interactiveCalls;
        private int _backgroundCalls;

        public int InteractiveCalls => _interactiveCalls;
        public int BackgroundCalls => _backgroundCalls;
        public string ModelId => inner.ModelId;
        public int Dimensions => inner.Dimensions;
        public int MaxInputTokens => inner.MaxInputTokens;
        public int CountTokens(string text) => inner.CountTokens(text);

        public float[] EmbedQuery(string query, CancellationToken cancellationToken = default) => inner.EmbedQuery(query, cancellationToken);

        public IReadOnlyList<float[]> EmbedDocuments(IReadOnlyList<DocumentText> documents, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _backgroundCalls);
            return inner.EmbedDocuments(documents, cancellationToken);
        }

        public IReadOnlyList<float[]> EmbedDocumentsInteractive(IReadOnlyList<DocumentText> documents, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _interactiveCalls);
            return inner.EmbedDocuments(documents, cancellationToken);
        }

        public void Dispose()
        {
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
