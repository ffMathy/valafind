using Valafind.Core.Abstractions;
using Valafind.Core.Storage;

namespace Valafind.Core.Search;

/// <summary>
/// Semantic search over an <see cref="IIndexStore"/>: embeds the query, fetches per-file hits and turns them into
/// display results with a snippet around the best-matching sentence and "why it matched" highlights.
/// </summary>
/// <remarks>
/// <para>The query is embedded on the thread pool. Cancellation (the launcher cancels the previous search on every
/// keystroke) is honoured before and after embedding and throws <see cref="OperationCanceledException"/>.</para>
/// <para>For the top <see cref="SemanticHighlightCount"/> results the best chunk is split into sentences that are
/// embedded in one batched call; the sentence closest to the query is centred in the snippet and highlighted.
/// The other results, or all of them when that step fails or exceeds its time budget, get lexical highlights
/// (query terms, with light stemming).</para>
/// </remarks>
public sealed class SearchService : ISearchService
{
    /// <summary>Results that get sentence-level semantic highlighting.</summary>
    internal const int SemanticHighlightCount = 5;

    /// <summary>Sentences per chunk considered for semantic highlighting.</summary>
    internal const int MaxSentencesPerChunk = 12;

    /// <summary>Added to the score when a query term appears in the file name.</summary>
    internal const float FileNameBoost = 0.03f;

    private readonly IEmbeddingModel _model;
    private readonly IIndexStore _store;

    /// <summary>Creates a search service over <paramref name="store"/> using <paramref name="model"/> for embeddings.</summary>
    public SearchService(IEmbeddingModel model, IIndexStore store)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(store);
        _model = model;
        _store = store;
    }

    /// <summary>Time allowed for embedding highlight sentences before falling back to lexical highlights.</summary>
    internal TimeSpan HighlightBudget { get; set; } = TimeSpan.FromSeconds(2);

    /// <inheritdoc />
    // INTEGRATION-STUB: replaced by the search-performance fix (progressive semantic highlights).
    public Task<IReadOnlyList<SearchResult>> ExplainAsync(string query, IReadOnlyList<SearchResult> results, CancellationToken cancellationToken) =>
        throw new NotImplementedException("INTEGRATION-STUB");

    public async Task<IReadOnlyList<SearchResult>> SearchAsync(string query, SearchFilter filter, int topK, CancellationToken cancellationToken)
    {
        var text = query?.Trim() ?? string.Empty;
        if (text.Length == 0 || topK <= 0)
            return [];
        filter ??= SearchFilter.None;
        cancellationToken.ThrowIfCancellationRequested();

        var queryVector = await Task.Run(() => _model.EmbedQuery(text, cancellationToken), cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();

        var hits = await Task.Run(() => _store.Search(queryVector, filter, topK), cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        if (hits.Count == 0)
            return [];

        var (stems, lastPrefix) = TextAnalysis.QueryTerms(text);

        // File-name boost, then re-sort.
        var ranked = hits
            .Select(hit => (hit, score: NameMatches(hit.Path, stems, lastPrefix) ? Math.Min(1f, hit.Score + FileNameBoost) : hit.Score))
            .OrderByDescending(x => x.score)
            .ThenBy(x => x.hit.Path, StringComparer.Ordinal)
            .ToList();

        // Sentence candidates per result (only results with text).
        var sentences = new List<TextSpan>[ranked.Count];
        for (var i = 0; i < ranked.Count; i++)
        {
            var chunk = ranked[i].hit.ChunkText;
            sentences[i] = string.IsNullOrWhiteSpace(chunk)
                ? []
                : TextAnalysis.CandidateSentences(chunk, MaxSentencesPerChunk, stems, lastPrefix);
        }

        var semanticBest = await PickSemanticSentencesAsync(ranked.Select(x => x.hit).ToList(), sentences, queryVector, cancellationToken)
            .ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();

        var results = new List<SearchResult>(ranked.Count);
        for (var i = 0; i < ranked.Count; i++)
        {
            var (hit, score) = ranked[i];
            var (snippet, highlights) = BuildSnippet(hit.ChunkText, sentences[i], semanticBest[i], stems, lastPrefix);
            results.Add(new SearchResult(
                Path: hit.Path,
                FileName: SafeFileName(hit.Path),
                Directory: SafeDirectory(hit.Path),
                Kind: hit.Kind,
                ModifiedUtc: hit.ModifiedUtc,
                Size: hit.Size,
                Score: score,
                Snippet: snippet,
                Highlights: highlights));
        }
        return results;
    }

    /// <summary>
    /// For the top results, embeds their candidate sentences in ONE batched call and returns, per result, the
    /// sentence closest to the query (or null when not computed). Model failures and the time budget fall back to null.
    /// </summary>
    private async Task<TextSpan?[]> PickSemanticSentencesAsync(
        List<StoreHit> hits, List<TextSpan>[] sentences, float[] queryVector, CancellationToken cancellationToken)
    {
        var best = new TextSpan?[hits.Count];
        var documents = new List<DocumentText>();
        var owners = new List<(int Result, TextSpan Sentence)>();
        for (var i = 0; i < Math.Min(SemanticHighlightCount, hits.Count); i++)
        {
            var hit = hits[i];
            if (sentences[i].Count == 0)
                continue;
            if (sentences[i].Count == 1)
            {
                best[i] = sentences[i][0]; // Nothing to choose from: skip the model.
                continue;
            }
            var title = SafeFileName(hit.Path);
            foreach (var span in sentences[i])
            {
                documents.Add(new DocumentText(string.IsNullOrEmpty(title) ? "none" : title, hit.ChunkText.Substring(span.Start, span.Length)));
                owners.Add((i, span));
            }
        }
        if (documents.Count == 0)
            return best;

        IReadOnlyList<float[]>? vectors = null;
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budget.CancelAfter(HighlightBudget);
        try
        {
            vectors = await Task.Run(() => _model.EmbedDocuments(documents, budget.Token), budget.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            vectors = null; // Budget exceeded: lexical fallback.
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            vectors = null; // Model failure must not break search: lexical fallback.
        }
        cancellationToken.ThrowIfCancellationRequested();

        if (vectors is null || vectors.Count != documents.Count)
            return best;

        var bestScore = new float[hits.Count];
        Array.Fill(bestScore, float.NegativeInfinity);
        for (var k = 0; k < owners.Count; k++)
        {
            var v = vectors[k];
            if (v is null || v.Length != queryVector.Length)
                return new TextSpan?[hits.Count]; // Inconsistent model output: do not trust any of it.
            var (result, span) = owners[k];
            var score = VectorMath.Dot(queryVector, v);
            if (score > bestScore[result])
            {
                bestScore[result] = score;
                best[result] = span;
            }
        }
        return best;
    }

    private static (string Snippet, IReadOnlyList<TextSpan> Highlights) BuildSnippet(
        string chunkText, List<TextSpan> sentences, TextSpan? semanticSentence, HashSet<string> stems, string? lastPrefix)
    {
        if (string.IsNullOrWhiteSpace(chunkText) || sentences.Count == 0)
            return (string.Empty, []);

        if (semanticSentence is { } focusSentence)
        {
            var snippet = Snippet.Build(chunkText, focusSentence);
            var mapped = snippet.MapSourceSpan(focusSentence);
            return (snippet.Text, mapped is { } span ? [span] : LexicalHighlights(snippet.Text, stems, lastPrefix));
        }

        // Lexical: centre on the sentence with most term matches (first one on ties, first sentence if none match).
        var focus = sentences[0];
        var bestCount = 0;
        foreach (var sentence in sentences)
        {
            var count = TextAnalysis.CountMatches(chunkText, sentence, stems, lastPrefix);
            if (count > bestCount)
            {
                bestCount = count;
                focus = sentence;
            }
        }
        var lexicalSnippet = Snippet.Build(chunkText, focus);
        return (lexicalSnippet.Text, LexicalHighlights(lexicalSnippet.Text, stems, lastPrefix));
    }

    private static List<TextSpan> LexicalHighlights(string snippet, HashSet<string> stems, string? lastPrefix) =>
        snippet.Length == 0 ? [] : TextAnalysis.LexicalMatches(snippet, stems, lastPrefix);

    private static bool NameMatches(string path, HashSet<string> stems, string? lastPrefix)
    {
        var name = SafeFileName(path);
        if (name.Length == 0)
            return false;
        foreach (var span in TextAnalysis.Words(name))
        {
            if (TextAnalysis.Matches(name, span, stems, lastPrefix))
                return true;
        }
        // Terms glued into names ("taxreturn2024.pdf"): plain case-insensitive containment of the raw stems.
        foreach (var stem in stems)
        {
            if (stem.Length >= TextAnalysis.MinTermLength && name.Contains(stem, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    private static string SafeFileName(string path)
    {
        try
        {
            return Path.GetFileName(path);
        }
        catch (ArgumentException)
        {
            return path;
        }
    }

    private static string SafeDirectory(string path)
    {
        try
        {
            return Path.GetDirectoryName(path) ?? string.Empty;
        }
        catch (ArgumentException)
        {
            return string.Empty;
        }
    }
}
