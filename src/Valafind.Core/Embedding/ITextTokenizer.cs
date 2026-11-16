namespace Valafind.Core.Embedding;

/// <summary>Tokenizer used by <see cref="EmbeddingGemmaModel"/>. Produces model token ids without the
/// special BOS/EOS tokens; the caller adds those (so truncation can keep them).</summary>
internal interface ITextTokenizer
{
    /// <summary>Id prepended to every sequence (Gemma: &lt;bos&gt; = 2).</summary>
    int BosId { get; }

    /// <summary>Id appended to every sequence (Gemma: &lt;eos&gt; = 1).</summary>
    int EosId { get; }

    /// <summary>Id used to pad batched sequences (Gemma: &lt;pad&gt; = 0).</summary>
    int PadId { get; }

    /// <summary>Length in UTF-16 chars of the longest token text; bounds how much text one token can cover.</summary>
    int MaxTokenLength { get; }

    /// <summary>Appends the token ids for <paramref name="text"/> to <paramref name="ids"/>. When
    /// <paramref name="offsets"/> is given, appends the UTF-16 start offset (into <paramref name="text"/>) of every
    /// token. Tokens produced by byte fallback for a single character share that character's offset.</summary>
    void Encode(string text, List<int> ids, List<int>? offsets = null);

    /// <summary>Number of tokens <see cref="Encode"/> would produce (no specials).</summary>
    int CountTokens(string text);
}
