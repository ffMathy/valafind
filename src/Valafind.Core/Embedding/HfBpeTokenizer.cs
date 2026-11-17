using System.Text;
using System.Text.Json;

namespace Valafind.Core.Embedding;

/// <summary>
/// A faithful re-implementation of the subset of Hugging Face <c>tokenizers</c> that Gemma 3 / EmbeddingGemma's
/// <c>tokenizer.json</c> uses: added-token splitting (leftmost-longest), a character-preserving
/// <c>Replace</c> normalizer, a <c>Split</c> pre-tokenizer, a BPE model with merge ranks and byte fallback, and a
/// <c>TemplateProcessing</c> post-processor (BOS/EOS). The merge loop mirrors <c>tokenizers::models::bpe::Word::merge_all</c>
/// (lowest rank first, ties broken by position) so token ids match the reference implementation exactly.
/// </summary>
/// <remarks>Microsoft.ML.Tokenizers' SentencePiece BPE (loaded from <c>tokenizer.model</c>) diverges from the HF
/// tokenizer on long whitespace runs, added tokens appearing in text and literal U+2581 characters, so it is only
/// used as a fallback when <c>tokenizer.json</c> is missing.</remarks>
internal sealed class HfBpeTokenizer : ITextTokenizer
{
    private readonly record struct Merge(int Rank, int NewId);

    private sealed class TrieNode
    {
        public Dictionary<char, TrieNode>? Children;
        public int TokenId = -1;
    }

    private readonly Dictionary<string, int> _vocab;
    private readonly Dictionary<string, int>.AlternateLookup<ReadOnlySpan<char>> _vocabLookup;
    private readonly Dictionary<long, Merge> _merges;
    private readonly int[] _byteIds = new int[256];
    private readonly bool _byteFallback;
    private readonly bool _fuseUnk;
    private readonly int _unkId;
    private readonly TrieNode? _rawAddedTokens;
    private readonly TrieNode? _normalizedAddedTokens;
    private readonly List<(char From, char To)> _replacements = [];
    private readonly string? _splitPattern;
    private readonly int[] _prefixIds;
    private readonly int[] _suffixIds;

    public int BosId { get; }
    public int EosId { get; }
    public int PadId { get; }

    /// <summary>Length (UTF-16 chars) of the longest vocabulary entry.</summary>
    public int MaxTokenLength { get; }

    private HfBpeTokenizer(JsonElement root)
    {
        var model = root.GetProperty("model");
        var type = model.TryGetProperty("type", out var t) ? t.GetString() : null;
        if (type != "BPE")
            throw new InvalidDataException($"tokenizer.json model type '{type}' is not supported (expected BPE).");
        if (model.TryGetProperty("dropout", out var dropout) && dropout.ValueKind == JsonValueKind.Number && dropout.GetDouble() > 0)
            throw new InvalidDataException("BPE dropout is not supported.");
        if (model.TryGetProperty("ignore_merges", out var im) && im.ValueKind == JsonValueKind.True)
            throw new InvalidDataException("BPE ignore_merges is not supported.");
        if (HasNonNullString(model, "continuing_subword_prefix") || HasNonNullString(model, "end_of_word_suffix"))
            throw new InvalidDataException("BPE subword prefixes/suffixes are not supported.");

        var vocabElement = model.GetProperty("vocab");
        _vocab = new Dictionary<string, int>(vocabElement.GetPropertyCount(), StringComparer.Ordinal);
        var maxLen = 0;
        foreach (var p in vocabElement.EnumerateObject())
        {
            _vocab[p.Name] = p.Value.GetInt32();
            maxLen = Math.Max(maxLen, p.Name.Length);
        }
        _vocabLookup = _vocab.GetAlternateLookup<ReadOnlySpan<char>>();

        _byteFallback = model.TryGetProperty("byte_fallback", out var bf) && bf.ValueKind == JsonValueKind.True;
        _fuseUnk = model.TryGetProperty("fuse_unk", out var fu) && fu.ValueKind == JsonValueKind.True;
        _unkId = model.TryGetProperty("unk_token", out var unk) && unk.ValueKind == JsonValueKind.String
                 && _vocab.TryGetValue(unk.GetString()!, out var unkId) ? unkId : -1;
        for (var b = 0; b < 256; b++)
            _byteIds[b] = _vocab.TryGetValue($"<0x{b:X2}>", out var id) ? id : -1;

        var mergesElement = model.GetProperty("merges");
        _merges = new Dictionary<long, Merge>(mergesElement.GetArrayLength());
        var rank = 0;
        foreach (var m in mergesElement.EnumerateArray())
        {
            string left, right;
            if (m.ValueKind == JsonValueKind.Array)
            {
                left = m[0].GetString()!;
                right = m[1].GetString()!;
            }
            else
            {
                var s = m.GetString()!;
                var sp = s.IndexOf(' ', StringComparison.Ordinal);
                if (sp <= 0)
                    throw new InvalidDataException($"Malformed merge '{s}'.");
                left = s[..sp];
                right = s[(sp + 1)..];
            }
            if (!_vocab.TryGetValue(left, out var l) || !_vocab.TryGetValue(right, out var r)
                || !_vocab.TryGetValue(left + right, out var merged))
                throw new InvalidDataException($"Merge '{left} {right}' refers to tokens missing from the vocabulary.");
            _merges.TryAdd(Key(l, r), new Merge(rank, merged));
            rank++;
        }

        // Added tokens: matched on the raw text (normalized=false) or on the normalised text (normalized=true).
        if (root.TryGetProperty("added_tokens", out var added) && added.ValueKind == JsonValueKind.Array)
        {
            foreach (var a in added.EnumerateArray())
            {
                var content = a.GetProperty("content").GetString()!;
                if (content.Length == 0)
                    continue;
                if (IsTrue(a, "lstrip") || IsTrue(a, "rstrip") || IsTrue(a, "single_word"))
                    throw new InvalidDataException($"Added token '{content}' uses lstrip/rstrip/single_word, which is not supported.");
                var id = a.GetProperty("id").GetInt32();
                maxLen = Math.Max(maxLen, content.Length);
                if (IsTrue(a, "normalized"))
                    Insert(_normalizedAddedTokens ??= new TrieNode(), content, id);
                else
                    Insert(_rawAddedTokens ??= new TrieNode(), content, id);
            }
        }
        MaxTokenLength = maxLen;

        ParseNormalizer(root.TryGetProperty("normalizer", out var n) ? n : default);
        // Normalised added tokens are matched against normalised text, so normalise their content too.
        if (_normalizedAddedTokens is not null && _replacements.Count > 0)
            _normalizedAddedTokens = RebuildNormalized(added);

        _splitPattern = ParsePreTokenizer(root.TryGetProperty("pre_tokenizer", out var pt) ? pt : default);
        (_prefixIds, _suffixIds) = ParsePostProcessor(root.TryGetProperty("post_processor", out var pp) ? pp : default);

        BosId = _prefixIds.Length > 0 ? _prefixIds[^1] : LookupOrDefault("<bos>", 2);
        EosId = _suffixIds.Length > 0 ? _suffixIds[0] : LookupOrDefault("<eos>", 1);
        PadId = root.TryGetProperty("padding", out var pad) && pad.ValueKind == JsonValueKind.Object
                && pad.TryGetProperty("pad_id", out var padId) ? padId.GetInt32() : LookupOrDefault("<pad>", 0);
    }

    /// <summary>Loads <c>tokenizer.json</c>. Throws <see cref="InvalidDataException"/> for unsupported features.</summary>
    public static HfBpeTokenizer Load(string tokenizerJsonPath)
    {
        var bytes = File.ReadAllBytes(tokenizerJsonPath);
        using var doc = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 128 });
        return new HfBpeTokenizer(doc.RootElement);
    }

    /// <summary>Special-token ids added around a single sequence by the post-processor.</summary>
    public ReadOnlySpan<int> PrefixIds => _prefixIds;

    /// <inheritdoc cref="PrefixIds"/>
    public ReadOnlySpan<int> SuffixIds => _suffixIds;

    public int CountTokens(string text)
    {
        var ids = new List<int>(Math.Min(text.Length, 4096));
        Encode(text, ids);
        return ids.Count;
    }

    public void Encode(string text, List<int> ids, List<int>? offsets = null)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(ids);
        if (text.Length == 0)
            return;

        var segStart = 0;
        var i = 0;
        while (i < text.Length)
        {
            if (_rawAddedTokens is not null && Match(_rawAddedTokens, text.AsSpan(i), out var len, out var id))
            {
                EncodeNormalizable(text, segStart, i - segStart, ids, offsets);
                ids.Add(id);
                offsets?.Add(i);
                i += len;
                segStart = i;
            }
            else
            {
                i++;
            }
        }
        EncodeNormalizable(text, segStart, text.Length - segStart, ids, offsets);
    }

    // Normalise a segment (1:1 character replacements, so offsets are preserved), split off normalised added
    // tokens, pre-tokenise and run BPE on each piece.
    private void EncodeNormalizable(string text, int start, int length, List<int> ids, List<int>? offsets)
    {
        if (length <= 0)
            return;

        char[]? rented = null;
        Span<char> buffer = length <= 512 ? stackalloc char[length] : (rented = System.Buffers.ArrayPool<char>.Shared.Rent(length));
        try
        {
            var normalized = buffer[..length];
            text.AsSpan(start, length).CopyTo(normalized);
            foreach (var (from, to) in _replacements)
                normalized.Replace(from, to);

            var segStart = 0;
            var i = 0;
            while (i < normalized.Length)
            {
                if (_normalizedAddedTokens is not null && Match(_normalizedAddedTokens, normalized[i..], out var len, out var id))
                {
                    EncodePreTokenized(normalized.Slice(segStart, i - segStart), start + segStart, ids, offsets);
                    ids.Add(id);
                    offsets?.Add(start + i);
                    i += len;
                    segStart = i;
                }
                else
                {
                    i++;
                }
            }
            EncodePreTokenized(normalized[segStart..], start + segStart, ids, offsets);
        }
        finally
        {
            if (rented is not null)
                System.Buffers.ArrayPool<char>.Shared.Return(rented);
        }
    }

    private void EncodePreTokenized(ReadOnlySpan<char> normalized, int baseOffset, List<int> ids, List<int>? offsets)
    {
        if (normalized.IsEmpty)
            return;
        if (_splitPattern is null)
        {
            EncodeWord(normalized, baseOffset, ids, offsets);
            return;
        }

        // Split(pattern, MergedWithPrevious): every occurrence of the pattern terminates the current piece.
        var pieceStart = 0;
        while (true)
        {
            var hit = normalized[pieceStart..].IndexOf(_splitPattern.AsSpan(), StringComparison.Ordinal);
            if (hit < 0)
                break;
            var end = pieceStart + hit + _splitPattern.Length;
            EncodeWord(normalized[pieceStart..end], baseOffset + pieceStart, ids, offsets);
            pieceStart = end;
        }
        if (pieceStart < normalized.Length)
            EncodeWord(normalized[pieceStart..], baseOffset + pieceStart, ids, offsets);
    }

    /// <summary>BPE over one pre-tokenised word, mirroring <c>tokenizers</c>' <c>Word::merge_all</c>.</summary>
    private void EncodeWord(ReadOnlySpan<char> word, int baseOffset, List<int> ids, List<int>? offsets)
    {
        if (word.IsEmpty)
            return;

        // Gemma's normaliser removes all spaces, so a "word" can be an entire chunk; the merge loop is O(n log n).
        var capacity = word.Length * 4; // worst case: every char becomes 4 byte-fallback tokens (surrogates: 4 per 2 chars)
        var sym = new int[capacity];
        var start = new int[capacity];
        var prev = new int[capacity];
        var next = new int[capacity];
        var dead = new bool[capacity];
        var isUnk = new bool[capacity];
        var n = 0;

        Span<byte> utf8 = stackalloc byte[4];
        var pos = 0;
        while (pos < word.Length)
        {
            var status = Rune.DecodeFromUtf16(word[pos..], out var rune, out var consumed);
            if (status != System.Buffers.OperationStatus.Done)
                rune = Rune.ReplacementChar; // lone surrogate; Rust strings cannot hold one either

            ReadOnlySpan<char> chars = status == System.Buffers.OperationStatus.Done ? word.Slice(pos, consumed) : "�";
            if (_vocabLookup.TryGetValue(chars, out var id))
            {
                Add(id, false);
            }
            else
            {
                var added = false;
                if (_byteFallback)
                {
                    var byteCount = rune.EncodeToUtf8(utf8);
                    var all = true;
                    for (var b = 0; b < byteCount && all; b++)
                        all = _byteIds[utf8[b]] >= 0;
                    if (all)
                    {
                        for (var b = 0; b < byteCount; b++)
                            Add(_byteIds[utf8[b]], false);
                        added = true;
                    }
                }
                if (!added && _unkId >= 0)
                {
                    if (!(_fuseUnk && n > 0 && isUnk[n - 1]))
                        Add(_unkId, true);
                }
                // No unk token configured: the character is dropped (tokenizers would raise an error).
            }
            pos += consumed;
        }

        // Merge loop.
        var queue = new PriorityQueue<(int Pos, int NewId), long>();
        for (var i = 0; i + 1 < n; i++)
            TryEnqueue(i, i + 1);

        while (queue.TryDequeue(out var top, out _))
        {
            var p = top.Pos;
            if (dead[p])
                continue;
            var nx = next[p];
            if (nx < 0)
                continue;
            if (!_merges.TryGetValue(Key(sym[p], sym[nx]), out var m) || m.NewId != top.NewId)
                continue; // stale entry

            sym[p] = top.NewId;
            isUnk[p] = false;
            dead[nx] = true;
            next[p] = next[nx];
            if (next[nx] >= 0)
                prev[next[nx]] = p;

            if (prev[p] >= 0)
                TryEnqueue(prev[p], p);
            if (next[p] >= 0)
                TryEnqueue(p, next[p]);
        }

        for (var i = 0; i < n; i++)
        {
            if (dead[i])
                continue;
            ids.Add(sym[i]);
            offsets?.Add(baseOffset + start[i]);
        }

        void Add(int id, bool unk)
        {
            sym[n] = id;
            start[n] = pos;
            prev[n] = n - 1;
            next[n] = -1;
            if (n > 0)
                next[n - 1] = n;
            isUnk[n] = unk;
            n++;
        }

        void TryEnqueue(int left, int right)
        {
            if (_merges.TryGetValue(Key(sym[left], sym[right]), out var m))
                queue.Enqueue((left, m.NewId), ((long)m.Rank << 32) | (uint)left);
        }
    }

    private static long Key(int left, int right) => ((long)left << 32) | (uint)right;

    private static bool Match(TrieNode root, ReadOnlySpan<char> text, out int length, out int id)
    {
        length = 0;
        id = -1;
        var node = root;
        for (var i = 0; i < text.Length; i++)
        {
            if (node.Children is null || !node.Children.TryGetValue(text[i], out var child))
                break;
            node = child;
            if (node.TokenId >= 0)
            {
                length = i + 1;
                id = node.TokenId;
            }
        }
        return length > 0;
    }

    private static void Insert(TrieNode root, string content, int id)
    {
        var node = root;
        foreach (var c in content)
        {
            node.Children ??= [];
            if (!node.Children.TryGetValue(c, out var child))
                node.Children[c] = child = new TrieNode();
            node = child;
        }
        node.TokenId = id;
    }

    private TrieNode RebuildNormalized(JsonElement added)
    {
        var root = new TrieNode();
        foreach (var a in added.EnumerateArray())
        {
            if (!IsTrue(a, "normalized"))
                continue;
            var content = a.GetProperty("content").GetString()!;
            if (content.Length == 0)
                continue;
            var chars = content.ToCharArray();
            foreach (var (from, to) in _replacements)
                chars.AsSpan().Replace(from, to);
            Insert(root, new string(chars), a.GetProperty("id").GetInt32());
        }
        return root;
    }

    private void ParseNormalizer(JsonElement n)
    {
        if (n.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null)
            return;
        var type = n.GetProperty("type").GetString();
        switch (type)
        {
            case "Sequence":
                foreach (var inner in n.GetProperty("normalizers").EnumerateArray())
                    ParseNormalizer(inner);
                break;
            case "Replace":
                var pattern = n.GetProperty("pattern");
                if (!pattern.TryGetProperty("String", out var from))
                    throw new InvalidDataException("Regex Replace normalizers are not supported.");
                var f = from.GetString()!;
                var to = n.GetProperty("content").GetString()!;
                if (f.Length != 1 || to.Length != 1)
                    throw new InvalidDataException("Only single-character Replace normalizers are supported.");
                _replacements.Add((f[0], to[0]));
                break;
            default:
                throw new InvalidDataException($"Normalizer '{type}' is not supported.");
        }
    }

    private static string? ParsePreTokenizer(JsonElement p)
    {
        if (p.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null)
            return null;
        var type = p.GetProperty("type").GetString();
        if (type != "Split")
            throw new InvalidDataException($"Pre-tokenizer '{type}' is not supported.");
        if (!p.GetProperty("pattern").TryGetProperty("String", out var pattern))
            throw new InvalidDataException("Regex Split pre-tokenizers are not supported.");
        if (p.GetProperty("behavior").GetString() != "MergedWithPrevious" || IsTrue(p, "invert"))
            throw new InvalidDataException("Only Split with behavior MergedWithPrevious is supported.");
        var s = pattern.GetString();
        return string.IsNullOrEmpty(s) ? null : s;
    }

    private (int[] Prefix, int[] Suffix) ParsePostProcessor(JsonElement p)
    {
        if (p.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null)
            return ([], []);
        var type = p.GetProperty("type").GetString();
        if (type != "TemplateProcessing")
            throw new InvalidDataException($"Post-processor '{type}' is not supported.");

        var specials = new Dictionary<string, int[]>(StringComparer.Ordinal);
        if (p.TryGetProperty("special_tokens", out var st))
        {
            foreach (var s in st.EnumerateObject())
                specials[s.Name] = s.Value.GetProperty("ids").EnumerateArray().Select(x => x.GetInt32()).ToArray();
        }

        var prefix = new List<int>();
        var suffix = new List<int>();
        var seenSequence = false;
        foreach (var item in p.GetProperty("single").EnumerateArray())
        {
            if (item.TryGetProperty("Sequence", out _))
            {
                seenSequence = true;
                continue;
            }
            var name = item.GetProperty("SpecialToken").GetProperty("id").GetString()!;
            var tokenIds = specials.TryGetValue(name, out var v) ? v
                : _vocab.TryGetValue(name, out var single) ? [single]
                : throw new InvalidDataException($"Unknown special token '{name}' in post-processor.");
            (seenSequence ? suffix : prefix).AddRange(tokenIds);
        }
        return (prefix.ToArray(), suffix.ToArray());
    }

    private int LookupOrDefault(string token, int fallback) => _vocab.TryGetValue(token, out var id) ? id : fallback;

    private static bool IsTrue(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;

    private static bool HasNonNullString(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String && !string.IsNullOrEmpty(v.GetString());
}
