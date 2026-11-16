#pragma warning disable CA1707 // xunit test names use underscores for readability

using Valafind.Core.Abstractions;
using Valafind.Core.Embedding;
using Xunit;

namespace Valafind.Core.Tests.Embedding;

/// <summary>End-to-end tests against the real model. Skipped unless VALAFIND_TEST_MODEL_DIR is set.</summary>
public sealed class EmbeddingGemmaModelTests(ModelFixture fixture) : IClassFixture<ModelFixture>
{
    private const string ReadmeQuery = "Which planet is known as the Red Planet?";

    private static readonly string[] ReadmeDocuments =
    [
        "Venus is often called Earth's twin because of its similar size and proximity.",
        "Mars, known for its reddish appearance, is often referred to as the Red Planet.",
        "Jupiter, the largest planet in our solar system, has a prominent red spot.",
        "Saturn, famous for its rings, is sometimes mistaken for the Red Planet.",
    ];

    private ReferenceData Reference => fixture.Reference;

    public static TheoryData<int> FixtureIndices() => new(Enumerable.Range(0, ReferenceData.Load().Texts.Length));

    [Theory]
    [MemberData(nameof(FixtureIndices))]
    public void Tokenizer_matches_reference_token_ids_exactly(int index)
    {
        fixture.RequireModel();
        var tokenizer = fixture.Full.Tokenizer;
        var ids = new List<int> { tokenizer.BosId };
        tokenizer.Encode(Reference.Texts[index], ids);
        ids.Add(tokenizer.EosId);

        Assert.Equal(Reference.TokenIds[index], ids);
        Assert.Equal(Reference.TokenIds[index], fixture.Full.BuildInput(Reference.Texts[index]));
    }

    [Fact]
    public void Tokenizer_offsets_are_monotonic_and_in_range()
    {
        fixture.RequireModel();
        var text = Reference.Texts[^1];
        var ids = new List<int>();
        var offsets = new List<int>();
        fixture.Full.Tokenizer.Encode(text, ids, offsets);

        Assert.Equal(ids.Count, offsets.Count);
        Assert.Equal(0, offsets[0]);
        for (var i = 1; i < offsets.Count; i++)
        {
            Assert.InRange(offsets[i], offsets[i - 1], text.Length - 1);
        }
    }

    [Theory]
    [MemberData(nameof(FixtureIndices))]
    public void Raw_768d_embedding_matches_reference(int index)
    {
        fixture.RequireModel();
        var input = fixture.Full.BuildInput(Reference.Texts[index]);
        var vector = fixture.Full.RunBatch([input], TestContext.Current.CancellationToken)[0];

        Assert.Equal(768, vector.Length);
        Assert.True(ModelFixture.Cosine(vector, Reference.Embeddings[index]) > 0.999,
            $"cosine {ModelFixture.Cosine(vector, Reference.Embeddings[index]):F6} for text {index}");
    }

    [Fact]
    public void Public_api_applies_prompts_exactly_like_the_reference()
    {
        fixture.RequireModel();
        var ct = TestContext.Current.CancellationToken;
        var query = fixture.Full.EmbedQuery(ReadmeQuery, ct);
        Assert.True(ModelFixture.Cosine(query, Reference.Embeddings[0]) > 0.999);

        var docs = fixture.Full.EmbedDocuments(
        [
            .. ReadmeDocuments.Select(d => new DocumentText("none", d)),
            new DocumentText("Lejekontrakt_Nørrebrogade_2024.pdf",
                "Depositum svarende til 3 måneders husleje, i alt 42.000 kr., betales ved underskrift."),
        ], ct);
        for (var i = 0; i < docs.Count; i++)
        {
            Assert.True(ModelFixture.Cosine(docs[i], Reference.Embeddings[i + 1]) > 0.999, $"document {i}");
        }

        // A blank title is rendered as "none".
        var blank = fixture.Full.EmbedDocuments([new DocumentText("  ", ReadmeDocuments[0])], ct)[0];
        Assert.True(ModelFixture.Cosine(blank, Reference.Embeddings[1]) > 0.999);
    }

    [Fact]
    public void Batched_embeddings_with_padding_equal_single_item_embeddings()
    {
        fixture.RequireModel();
        var ct = TestContext.Current.CancellationToken;
        DocumentText[] docs =
        [
            new("a.txt", "Short."),
            new("notes.md", string.Join(' ', Enumerable.Repeat("A considerably longer document about orbital mechanics and planets.", 12))),
            new("none", ReadmeDocuments[1]),
            new("Lejekontrakt.pdf", "Depositum svarende til 3 måneders husleje."),
            new("", "日本語テキスト — em-dash\n\tand whitespace"),
            new("x", "Mid-length text that sits between the short and the long inputs in token count."),
            new("y", "?"),
            new("z", string.Join('\n', Enumerable.Repeat("line of code();", 30))),
            new("w", "Ten documents force two batches with the default batch size of eight."),
            new("v", "Mars"),
        ];

        var batched = fixture.Full.EmbedDocuments(docs, ct);
        Assert.Equal(docs.Length, batched.Count);
        for (var i = 0; i < docs.Length; i++)
        {
            var single = fixture.Full.EmbedDocuments([docs[i]], ct)[0];
            var cos = ModelFixture.Cosine(batched[i], single);
            Assert.True(cos > 0.9999, $"document {i}: cosine {cos:F6}");
        }

        // Same through the small model: batch size 3 and truncation to 80 tokens.
        var smallBatched = fixture.Small.EmbedDocuments(docs, ct);
        for (var i = 0; i < docs.Length; i++)
        {
            var single = fixture.Small.EmbedDocuments([docs[i]], ct)[0];
            Assert.True(ModelFixture.Cosine(smallBatched[i], single) > 0.9999, $"small model document {i}");
        }
    }

    [Fact]
    public void Matryoshka_truncation_yields_unit_vectors_of_requested_size()
    {
        fixture.RequireModel();
        var ct = TestContext.Current.CancellationToken;
        var small = fixture.Small;
        Assert.Equal(256, small.Dimensions);
        Assert.Equal("embeddinggemma-300m/model_quantized/256", small.ModelId);
        Assert.Equal("embeddinggemma-300m/model_quantized/768", fixture.Full.ModelId);

        var query = small.EmbedQuery(ReadmeQuery, ct);
        Assert.Equal(256, query.Length);
        Assert.InRange(ModelFixture.Norm(query), 0.9999, 1.0001);
        var expected = Matryoshka.TruncateAndNormalize(Reference.Embeddings[0], 256);
        Assert.True(ModelFixture.Cosine(query, expected) > 0.999);

        foreach (var dims in new[] { 512, 128 })
        {
            var v = Matryoshka.TruncateAndNormalize(fixture.Full.EmbedQuery(ReadmeQuery, ct), dims);
            Assert.Equal(dims, v.Length);
            Assert.InRange(ModelFixture.Norm(v), 0.9999, 1.0001);
        }

        foreach (var v in small.EmbedDocuments([new DocumentText("t", "some text"), new DocumentText("u", "more text")], ct))
        {
            Assert.Equal(256, v.Length);
            Assert.InRange(ModelFixture.Norm(v), 0.9999, 1.0001);
        }
    }

    [Fact]
    public void Query_document_ranking_reproduces_reference_similarities()
    {
        fixture.RequireModel();
        var ct = TestContext.Current.CancellationToken;
        var query = fixture.Full.EmbedQuery(ReadmeQuery, ct);
        var docs = fixture.Full.EmbedDocuments(ReadmeDocuments.Select(d => new DocumentText("none", d)).ToList(), ct);
        var sims = docs.Select(d => ModelFixture.Cosine(query, d)).ToArray();

        var expectedOrder = Enumerable.Range(0, 4).OrderByDescending(i => Reference.ReadmeSimilarities[i]).ToArray();
        var actualOrder = Enumerable.Range(0, 4).OrderByDescending(i => sims[i]).ToArray();
        Assert.Equal(expectedOrder, actualOrder);
        Assert.Equal(1, actualOrder[0]); // Mars
        for (var i = 0; i < 4; i++)
        {
            Assert.True(Math.Abs(sims[i] - Reference.ReadmeSimilarities[i]) < 0.005, $"doc {i}: {sims[i]:F4} vs {Reference.ReadmeSimilarities[i]:F4}");
        }

        // The ranking survives Matryoshka truncation to 256 dimensions.
        var q256 = fixture.Small.EmbedQuery(ReadmeQuery, ct);
        var d256 = fixture.Small.EmbedDocuments(ReadmeDocuments.Select(d => new DocumentText("none", d)).ToList(), ct);
        Assert.Equal(1, Enumerable.Range(0, 4).MaxBy(i => ModelFixture.Cosine(q256, d256[i])));
    }

    [Fact]
    public void Long_inputs_are_truncated_keeping_bos_and_eos()
    {
        fixture.RequireModel();
        var model = fixture.Small; // MaxTokens = 80
        var tokenizer = model.Tokenizer;
        var longText = string.Join(' ', Enumerable.Repeat("The quick brown fox jumps over the lazy dog.", 400));
        var prompted = EmbeddingPrompts.Document("none", longText);

        var input = model.BuildInput(prompted);
        Assert.Equal(80, input.Length);
        Assert.Equal(tokenizer.BosId, input[0]);
        Assert.Equal(tokenizer.EosId, input[^1]);

        var full = new List<int>();
        tokenizer.Encode(prompted, full);
        Assert.Equal(full.Take(78), input.Skip(1).Take(78));

        Assert.Equal(80 - EmbeddingGemmaModel.ReservedPromptTokens, model.MaxInputTokens);
        Assert.Equal(512 - EmbeddingGemmaModel.ReservedPromptTokens, fixture.Full.MaxInputTokens);

        var vector = model.EmbedDocuments([new DocumentText("none", longText)], TestContext.Current.CancellationToken)[0];
        Assert.InRange(ModelFixture.Norm(vector), 0.9999, 1.0001);
    }

    [Fact]
    public void Chunk_sized_text_fits_without_truncation_even_with_a_long_title()
    {
        fixture.RequireModel();
        var model = fixture.Full;
        var words = string.Join(' ', Enumerable.Range(0, 2000).Select(i => $"word{i}"));
        var cut = model.GetPrefixLengthWithinTokens(words, model.MaxInputTokens);
        var chunk = words[..cut];
        Assert.InRange(model.CountTokens(chunk), model.MaxInputTokens - 2, model.MaxInputTokens + 1);

        var longTitle = string.Concat(Enumerable.Repeat("Very_Long_File_Name_Component_", 20)) + ".docx";
        var prompt = model.BuildDocumentPrompt(longTitle, chunk);
        var promptTokens = model.Tokenizer.CountTokens(prompt) + 2;
        Assert.True(promptTokens <= 512, $"prompt has {promptTokens} tokens");
        Assert.StartsWith("title: Very_Long_File_Name", prompt, StringComparison.Ordinal);
    }

    [Fact]
    public void CountTokens_counts_raw_text_without_prompt_or_specials()
    {
        fixture.RequireModel();
        var raw = Reference.Texts[^1]; // no prompt in this fixture text
        Assert.Equal(Reference.TokenIds[^1].Length - 2, fixture.Full.CountTokens(raw));
        Assert.Equal(0, fixture.Full.CountTokens(""));
    }

    [Fact]
    public void Cancellation_is_honoured()
    {
        fixture.RequireModel();
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() => fixture.Full.EmbedQuery("x", cts.Token));
        Assert.ThrowsAny<OperationCanceledException>(() =>
            fixture.Full.EmbedDocuments([new DocumentText("a", "b"), new DocumentText("c", "d")], cts.Token));
    }

    [Fact]
    public void Concurrent_calls_are_safe_and_deterministic()
    {
        fixture.RequireModel();
        var ct = TestContext.Current.CancellationToken;
        var expected = fixture.Full.EmbedQuery(ReadmeQuery, ct);
        var results = new float[6][];
        Parallel.For(0, results.Length, i => results[i] = fixture.Full.EmbedQuery(ReadmeQuery, ct));
        foreach (var r in results)
            Assert.True(ModelFixture.Cosine(expected, r) > 0.99999);
    }

    [Fact]
    public async Task Query_preempts_a_running_document_batch_which_is_then_rerun_correctly()
    {
        var dir = fixture.RequireModel();
        var ct = TestContext.Current.CancellationToken;
        ModelLocator.IsValid(dir, out var file);
        using var model = new EmbeddingGemmaModel(new EmbeddingOptions
        {
            ModelDirectory = dir,
            ModelFile = file,
            Dimensions = 256,
            BatchSize = 8,
            IntraOpThreads = 2,
        });

        // Eight ~400-token documents: one batch that takes seconds on a laptop-class CPU.
        var docs = Enumerable.Range(0, 8)
            .Select(i => new DocumentText($"report-{i}.txt", string.Join(' ', Enumerable.Range(0, 20)
                .Select(j => $"Section {i}.{j} describes quarterly revenue, staffing and the lease of building {i * 20 + j}."))))
            .ToArray();
        Assert.All(docs, d => Assert.InRange(model.CountTokens(d.Text), 300, model.MaxInputTokens));

        model.EnsureLoaded();
        var idle = System.Diagnostics.Stopwatch.StartNew();
        var expectedQuery = model.EmbedQuery(ReadmeQuery, ct);
        idle.Restart();
        model.EmbedQuery(ReadmeQuery, ct);
        var idleQuery = idle.Elapsed;

        var alone = System.Diagnostics.Stopwatch.StartNew();
        var expectedDocs = model.EmbedDocuments(docs, ct);
        var batchAlone = alone.Elapsed;

        var preemptedBefore = model.PreemptedRuns;
        var docTask = Task.Run(() => model.EmbedDocuments(docs, ct), ct);
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (!model.IsBackgroundRunActive && DateTime.UtcNow < deadline)
            Thread.Sleep(5);
        Assert.True(model.IsBackgroundRunActive, "document batch did not start");
        Thread.Sleep(TimeSpan.FromTicks(Math.Min(batchAlone.Ticks / 5, TimeSpan.FromMilliseconds(500).Ticks)));
        Assert.False(docTask.IsCompleted, "document batch finished before the query was issued");

        var timer = System.Diagnostics.Stopwatch.StartNew();
        var query = model.EmbedQuery(ReadmeQuery, ct);
        var queryDuringBatch = timer.Elapsed;

        var docsDuringQuery = await docTask;

        TestContext.Current.TestOutputHelper?.WriteLine(
            $"idle query {idleQuery.TotalMilliseconds:F0} ms, batch alone {batchAlone.TotalMilliseconds:F0} ms, query during batch {queryDuringBatch.TotalMilliseconds:F0} ms");
        Assert.True(model.PreemptedRuns > preemptedBefore, "the document run was not preempted");
        // Without preemption the query would wait for the rest of the batch (most of batchAlone).
        var bound = TimeSpan.FromTicks(Math.Max(TimeSpan.FromSeconds(1.5).Ticks, idleQuery.Ticks * 3));
        Assert.True(queryDuringBatch < bound, $"query took {queryDuringBatch.TotalMilliseconds:F0} ms during a batch (bound {bound.TotalMilliseconds:F0} ms)");
        Assert.True(ModelFixture.Cosine(expectedQuery, query) > 0.99999);
        Assert.Equal(docs.Length, docsDuringQuery.Count);
        for (var i = 0; i < docs.Length; i++)
            Assert.True(ModelFixture.Cosine(expectedDocs[i], docsDuringQuery[i]) > 0.99999, $"document {i}");
    }

    [Fact]
    public async Task Cancelling_a_document_batch_while_it_runs_aborts_it()
    {
        var dir = fixture.RequireModel();
        ModelLocator.IsValid(dir, out var file);
        using var model = new EmbeddingGemmaModel(new EmbeddingOptions { ModelDirectory = dir, ModelFile = file, Dimensions = 256 });
        var docs = Enumerable.Range(0, 8)
            .Select(i => new DocumentText("x", string.Join(' ', Enumerable.Repeat($"Paragraph {i} about leases and budgets.", 60))))
            .ToArray();
        model.EnsureLoaded();
        using var cts = new CancellationTokenSource();
        var task = Task.Run(() => model.EmbedDocuments(docs, cts.Token), TestContext.Current.CancellationToken);
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (!model.IsBackgroundRunActive && DateTime.UtcNow < deadline)
            Thread.Sleep(5);
        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
        Assert.False(model.IsBackgroundRunActive);
        // The model is still usable afterwards.
        Assert.Equal(256, model.EmbedQuery("still works", TestContext.Current.CancellationToken).Length);
    }

    [Fact]
    public void Empty_document_list_and_empty_text_are_handled()
    {
        fixture.RequireModel();
        var ct = TestContext.Current.CancellationToken;
        Assert.Empty(fixture.Full.EmbedDocuments([], ct));
        var v = fixture.Full.EmbedDocuments([new DocumentText("empty.txt", "")], ct)[0];
        Assert.InRange(ModelFixture.Norm(v), 0.9999, 1.0001);
        var q = fixture.Full.EmbedQuery("", ct);
        Assert.Equal(768, q.Length);
    }

    [Fact]
    public void Disposed_model_throws()
    {
        var dir = fixture.RequireModel();
        ModelLocator.IsValid(dir, out var file);
        var model = new EmbeddingGemmaModel(new EmbeddingOptions { ModelDirectory = dir, ModelFile = file });
        model.Dispose();
        model.Dispose();
        Assert.Throws<ObjectDisposedException>(() => model.EmbedQuery("x", TestContext.Current.CancellationToken));
        Assert.Throws<ObjectDisposedException>(() => model.CountTokens("x"));
    }
}
