using Microsoft.ML.Tokenizers;

namespace Valafind.Core.Embedding;

/// <summary>
/// Fallback tokenizer for model directories that ship only <c>tokenizer.model</c> (no <c>tokenizer.json</c>).
/// Uses Microsoft.ML.Tokenizers' SentencePiece BPE, which matches the reference tokenizer on ordinary prose but can
/// differ on long whitespace runs, literal special-token strings and U+2581 characters. <see cref="HfBpeTokenizer"/>
/// is preferred whenever <c>tokenizer.json</c> is available.
/// </summary>
internal sealed class SentencePieceTextTokenizer : ITextTokenizer
{
    private readonly SentencePieceTokenizer _tokenizer;

    private SentencePieceTextTokenizer(SentencePieceTokenizer tokenizer)
    {
        _tokenizer = tokenizer;
        BosId = tokenizer.BeginningOfSentenceId;
        EosId = tokenizer.EndOfSentenceId;
        PadId = tokenizer.Vocabulary.TryGetValue("<pad>", out var pad) ? pad : 0;
        MaxTokenLength = Math.Max(1, tokenizer.Vocabulary.Keys.Max(k => k.Length));
    }

    public int BosId { get; }
    public int EosId { get; }
    public int PadId { get; }
    public int MaxTokenLength { get; }

    public static SentencePieceTextTokenizer Load(string tokenizerModelPath)
    {
        using var stream = File.OpenRead(tokenizerModelPath);
        return new SentencePieceTextTokenizer(SentencePieceTokenizer.Create(stream, false, false));
    }

    public int CountTokens(string text) =>
        string.IsNullOrEmpty(text) ? 0 : _tokenizer.CountTokens(text, considerPreTokenization: true, considerNormalization: true);

    public void Encode(string text, List<int> ids, List<int>? offsets = null)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (text.Length == 0)
            return;
        if (offsets is null)
        {
            ids.AddRange(_tokenizer.EncodeToIds(text, considerPreTokenization: true, considerNormalization: true));
            return;
        }

        var tokens = _tokenizer.EncodeToTokens(text, out _, considerPreTokenization: true, considerNormalization: true);
        foreach (var token in tokens)
        {
            ids.Add(token.Id);
            // Offsets refer to the normalised text; for Gemma the normaliser only maps ' ' to U+2581 (1:1).
            offsets.Add(Math.Clamp(token.Offset.Start.GetOffset(text.Length), 0, text.Length));
        }
    }
}
