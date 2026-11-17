using Valafind.Core.Abstractions;
using Valafind.Core.Embedding;
using Valafind.Core.Storage;

namespace Valafind.Core.Search;

/// <summary>
/// Semantic search over an <see cref="IIndexStore"/>: embeds the query, fetches per-file hits and turns them into
/// display results with a snippet around the best-matching sentence and "why it matched" highlights.
/// </summary>
/// <remarks>
/// <para><see cref="SearchAsync"/> is the fast path: one query embedding, one store search and LEXICAL snippets and
/// highlights (query terms with light stemming, compound prefixes and CJK substrings). It does no other model work.
/// The query is embedded on the thread pool. Cancellation (the launcher cancels the previous search on every
/// keystroke) is honoured before and after embedding and throws <see cref="OperationCanceledException"/>.</para>
/// <para><see cref="ExplainAsync"/> is the optional second pass the UI runs once results are shown: for the top
/// <see cref="ExplainCount"/> results it embeds up to <see cref="ExplainSentencesPerResult"/> lexically pre-selected
/// sentences of each snippet in ONE short batch and highlights the sentence closest to the query.</para>
/// </remarks>
public sealed class SearchService : ISearchService
{
    /// <summary>Results that get sentence-level semantic highlighting in <see cref="ExplainAsync"/>.</summary>
    internal const int ExplainCount = 2;

    /// <summary>Snippet sentences per result embedded by <see cref="ExplainAsync"/>.</summary>
    internal const int ExplainSentencesPerResult = 4;

    /// <summary>Results scoring below this are not explained: for a weak match the "closest sentence" is just noise.</summary>
    internal const float MinExplainResultScore = 0.30f;

    /// <summary>A sentence is highlighted only when its own similarity to the query reaches this.</summary>
    internal const float MinExplainSentenceScore = 0.25f;

    /// <summary>Added to the score when a query term matches the file name (see <see cref="NameMatches"/>).</summary>
    internal const float FileNameBoost = 0.03f;

    /// <summary>Extra store hits (beyond topK, at most this many) fetched so a file-name match ranked just below the
    /// cut can still be boosted into the results.</summary>
    internal const int MaxBoostCandidates = 32;

    private readonly IEmbeddingModel _model;
    private readonly IIndexStore _store;

    /// <summary>The last embedded query, reused by <see cref="ExplainAsync"/> for the same text.</summary>
    private volatile CachedQuery? _lastQuery;

    /// <summary>Creates a search service over <paramref name="store"/> using <paramref name="model"/> for embeddings.</summary>
    public SearchService(IEmbeddingModel model, IIndexStore store)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(store);
        _model = model;
        _store = store;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<SearchResult>> SearchAsync(string query, SearchFilter filter, int topK, CancellationToken cancellationToken)
    {
        var text = query?.Trim() ?? string.Empty;
        if (text.Length == 0 || topK <= 0)
            return [];
        filter ??= SearchFilter.None;
        cancellationToken.ThrowIfCancellationRequested();

        var queryVector = await EmbedQueryAsync(text, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();

        var candidates = topK + Math.Min(topK, MaxBoostCandidates);
        var hits = await Task.Run(() => _store.Search(queryVector, filter, candidates), cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        if (hits.Count == 0)
            return [];

        var (stems, lastPrefix) = TextAnalysis.QueryTerms(text);
        var nameTerms = NameTerms(text);

        // File-name boost, then re-sort and cut to topK.
        var ranked = hits
            .Select(hit => (hit, score: NameMatches(hit.Path, nameTerms) ? Math.Min(1f, hit.Score + FileNameBoost) : hit.Score))
            .OrderByDescending(x => x.score)
            .ThenBy(x => x.hit.Path, StringComparer.Ordinal)
            .Take(topK)
            .ToList();

        var results = new List<SearchResult>(ranked.Count);
        foreach (var (hit, score) in ranked)
        {
            var (snippet, highlights) = BuildLexicalSnippet(hit.ChunkText, stems, lastPrefix);
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

    /// <inheritdoc />
    /// <remarks>Never throws for model errors (returns <paramref name="results"/> unchanged); throws
    /// <see cref="OperationCanceledException"/> when <paramref name="cancellationToken"/> is cancelled.</remarks>
    public async Task<IReadOnlyList<SearchResult>> ExplainAsync(string query, IReadOnlyList<SearchResult> results, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(results);
        cancellationToken.ThrowIfCancellationRequested();
        var text = query?.Trim() ?? string.Empty;
        if (text.Length == 0 || results.Count == 0)
            return results;

        var (stems, lastPrefix) = TextAnalysis.QueryTerms(text);
        var documents = new List<DocumentText>();
        var owners = new List<(int Result, TextSpan Sentence)>();
        for (var i = 0; i < Math.Min(ExplainCount, results.Count); i++)
        {
            var snippet = results[i]?.Snippet;
            if (string.IsNullOrWhiteSpace(snippet) || results[i].Score < MinExplainResultScore)
                continue;
            var sentences = SnippetSentences(snippet, stems, lastPrefix);
            if (sentences.Count < 2)
                continue; // Nothing to choose from: the lexical highlights already say more than one sentence would.
            // Title "none": the short prompt keeps the batch small and the file name from dominating short sentences.
            foreach (var span in sentences)
            {
                documents.Add(new DocumentText(EmbeddingPrompts.NoTitle, snippet.Substring(span.Start, span.Length)));
                owners.Add((i, span));
            }
        }
        if (documents.Count == 0)
            return results;

        float[] queryVector;
        IReadOnlyList<float[]> vectors;
        try
        {
            queryVector = await EmbedQueryAsync(text, cancellationToken).ConfigureAwait(false);
            // The user is waiting on this batch: run it with query priority (preempting an indexing batch) when the
            // model supports it, instead of queueing behind background work.
            vectors = await Task.Run(() => _model is IInteractiveEmbedding interactive
                ? interactive.EmbedDocumentsInteractive(documents, cancellationToken)
                : _model.EmbedDocuments(documents, cancellationToken), cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
#pragma warning disable CA1031 // Explaining is optional: any model failure keeps the lexical highlights.
        catch (Exception)
#pragma warning restore CA1031
        {
            return results;
        }
        cancellationToken.ThrowIfCancellationRequested();
        if (vectors is null || vectors.Count != documents.Count)
            return results;

        var best = new TextSpan?[results.Count];
        var bestScore = new float[results.Count];
        Array.Fill(bestScore, float.NegativeInfinity);
        for (var k = 0; k < owners.Count; k++)
        {
            var v = vectors[k];
            if (v is null || v.Length != queryVector.Length)
                return results; // Inconsistent model output: do not trust any of it.
            var (result, span) = owners[k];
            var score = VectorMath.Dot(queryVector, v);
            if (score >= MinExplainSentenceScore && score > bestScore[result])
            {
                bestScore[result] = score;
                best[result] = span;
            }
        }

        var explained = new SearchResult[results.Count];
        for (var i = 0; i < results.Count; i++)
            explained[i] = best[i] is { } span ? results[i] with { Highlights = [span] } : results[i];
        return explained;
    }

    private async Task<float[]> EmbedQueryAsync(string text, CancellationToken cancellationToken)
    {
        if (_lastQuery is { } cached && string.Equals(cached.Text, text, StringComparison.Ordinal))
            return cached.Vector;
        var vector = await Task.Run(() => _model.EmbedQuery(text, cancellationToken), cancellationToken).ConfigureAwait(false);
        _lastQuery = new CachedQuery(text, vector);
        return vector;
    }

    /// <summary>Up to <see cref="ExplainSentencesPerResult"/> sentences of a snippet (preferring ones with query terms),
    /// without the "…" that marks cut ends.</summary>
    internal static List<TextSpan> SnippetSentences(string snippet, HashSet<string> stems, string? lastPrefix)
    {
        var result = new List<TextSpan>();
        foreach (var span in TextAnalysis.CandidateSentences(snippet, ExplainSentencesPerResult, stems, lastPrefix))
        {
            var start = span.Start;
            var end = span.Start + span.Length;
            while (start < end && (snippet[start] == '…' || char.IsWhiteSpace(snippet[start])))
                start++;
            while (end > start && (snippet[end - 1] == '…' || char.IsWhiteSpace(snippet[end - 1])))
                end--;
            if (end > start)
                result.Add(new TextSpan(start, end - start));
        }
        return result;
    }

    /// <summary>Snippet centred on the sentence with most query-term matches (first one on ties, first sentence if
    /// none match), with every matching word highlighted.</summary>
    private static (string Snippet, IReadOnlyList<TextSpan> Highlights) BuildLexicalSnippet(string chunkText, HashSet<string> stems, string? lastPrefix)
    {
        if (string.IsNullOrWhiteSpace(chunkText))
            return (string.Empty, []);
        var sentences = TextAnalysis.Sentences(chunkText);
        if (sentences.Count == 0)
            return (string.Empty, []);

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
        var snippet = Snippet.Build(chunkText, focus);
        return (snippet.Text, snippet.Text.Length == 0 ? [] : TextAnalysis.LexicalMatches(snippet.Text, stems, lastPrefix));
    }

    /// <summary>Query terms that may trigger the file-name boost: words of ≥ 3 chars (≥ 2 in scripts without spaces)
    /// that are not stopwords, stemmed.</summary>
    internal static HashSet<string> NameTerms(string query)
    {
        var terms = new HashSet<string>(StringComparer.Ordinal);
        foreach (var span in TextAnalysis.Words(query))
        {
            var word = query.Substring(span.Start, span.Length);
            var unspaced = TextAnalysis.ContainsUnspacedScript(word);
            if (word.Length < (unspaced ? TextAnalysis.MinUnspacedTermLength : TextAnalysis.MinTermLength) || TextAnalysis.IsStopword(word))
                continue;
            terms.Add(TextAnalysis.Stem(word));
        }
        return terms;
    }

    /// <summary>True when a name term equals (by stem) a whole token of the file name without extension. Long terms
    /// (≥ <see cref="TextAnalysis.MinCompoundPrefixLength"/> chars, like compound prefixes in content highlights) and
    /// terms in scripts written without spaces also match anywhere in the extension-less name, which covers glued and
    /// compound names ("budgetplan", "taxreturn2024", "Mietvertragsentwurf") without letting short terms match inside
    /// unrelated words ("tax" in "Taxonomy", "port" in "report").</summary>
    internal static bool NameMatches(string path, HashSet<string> nameTerms)
    {
        if (nameTerms.Count == 0)
            return false;
        var name = SafeFileNameWithoutExtension(path);
        if (name.Length == 0)
            return false;
        foreach (var token in TextAnalysis.NameTokens(name))
        {
            if (nameTerms.Contains(TextAnalysis.Stem(token)))
                return true;
        }
        foreach (var term in nameTerms)
        {
            if ((term.Length >= TextAnalysis.MinCompoundPrefixLength || TextAnalysis.ContainsUnspacedScript(term))
                && name.Contains(term, StringComparison.OrdinalIgnoreCase))
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

    private static string SafeFileNameWithoutExtension(string path)
    {
        var name = SafeFileName(path);
        var dot = name.LastIndexOf('.');
        return dot > 0 ? name[..dot] : name;
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

    private sealed record CachedQuery(string Text, float[] Vector);
}
