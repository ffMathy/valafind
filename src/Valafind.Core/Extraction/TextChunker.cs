using System.Globalization;
using System.Text;
using Valafind.Core.Abstractions;

namespace Valafind.Core.Extraction;

/// <summary>Options for <see cref="TextChunker"/>.</summary>
public sealed class ChunkerOptions
{
    /// <summary>Maximum tokens per chunk (as measured by the chunker's token counter).</summary>
    public int TargetTokens { get; set; } = 320;

    /// <summary>Approximate number of tokens repeated at the start of the next chunk.</summary>
    public int OverlapTokens { get; set; } = 48;

    /// <summary>Maximum number of chunks per document; the first ones are kept.</summary>
    /// <remarks>
    /// The default (12) is a first-pass budget: ≈ 12 × (320 − 48) ≈ 3.3k new tokens (≈ 3.8k with overlap), i.e. roughly
    /// the first 15–20k characters of prose, about 6 s of embedding per file at ~0.5 s per chunk on a laptop CPU. It is
    /// aligned with <see cref="ExtractionLimits.MaxChars"/> (32k chars), so extraction does not parse much more than
    /// is embedded. Limitation: content beyond that prefix of a long document (later pages of a long PDF, the tail of
    /// a large log) is not embedded and cannot be found semantically until a deeper pass indexes it.
    /// </remarks>
    public int MaxChunks { get; set; } = 12;
}

/// <summary>
/// Splits text into overlapping chunks that each fit a token budget, preferring to cut at paragraph, then
/// sentence, then line, then word boundaries (and, for a single overlong word, at grapheme boundaries).
/// </summary>
/// <remarks>
/// <para><see cref="TextChunk.Start"/>/<see cref="TextChunk.Length"/> index the original, unmodified text.
/// <see cref="TextChunk.Text"/> is that span with every whitespace run collapsed to a single character (a line break if
/// the run contained one, otherwise a space).</para>
/// <para>With a token counter, every chunk's <see cref="TextChunk.Text"/> measures at most
/// <see cref="ChunkerOptions.TargetTokens"/> (unless a single grapheme alone exceeds it). Without one, tokens
/// are estimated conservatively as ceil(chars / 3.5).</para>
/// <para>Instances are immutable and thread-safe if the token counter is.</para>
/// </remarks>
public sealed class TextChunker : ITextChunker
{
    private const double CharsPerToken = 3.5;

    /// <summary>Spans longer than this many chars per budget token are split before being measured.</summary>
    private const int MaxCharsPerTokenToMeasure = 32;

    /// <summary>Window (chars per budget token) searched when hard-splitting an overlong word.</summary>
    private const int HardSplitCharsPerToken = 8;

    /// <summary>A cut is only placed earlier than the greedy maximum if the chunk is at least this full.</summary>
    private const double MinFillForPreferredBreak = 0.5;

    private readonly int _target;
    private readonly int _overlap;
    private readonly int _maxChunks;
    private readonly Func<string, int>? _counter;
    private readonly int _overhead;

    /// <summary>Creates a chunker.</summary>
    /// <param name="options">Chunk size, overlap and count limits.</param>
    /// <param name="tokenCounter">Counts tokens of a string (e.g. <see cref="IEmbeddingModel.CountTokens"/>).
    /// Any constant per-call overhead such as BOS/EOS tokens (measured as the count of the empty string) is
    /// accounted for once per chunk.</param>
    public TextChunker(ChunkerOptions options, Func<string, int>? tokenCounter = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentOutOfRangeException.ThrowIfLessThan(options.TargetTokens, 1, nameof(options));
        ArgumentOutOfRangeException.ThrowIfLessThan(options.MaxChunks, 1, nameof(options));
        ArgumentOutOfRangeException.ThrowIfNegative(options.OverlapTokens, nameof(options));

        _target = options.TargetTokens;
        _overlap = Math.Min(options.OverlapTokens, _target / 2);
        _maxChunks = options.MaxChunks;
        _counter = tokenCounter;
        _overhead = tokenCounter is null ? 0 : Math.Clamp(tokenCounter(string.Empty), 0, _target - 1);
    }

    /// <inheritdoc />
    public IReadOnlyList<TextChunk> Chunk(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var chunks = new List<TextChunk>();
        var units = new UnitScanner(this, text);
        if (!units.Ensure(0))
        {
            return chunks;
        }

        var start = 0;      // first unit of the current chunk
        var prevEnd = -1;   // last unit of the previous chunk
        while (chunks.Count < _maxChunks && units.Ensure(start))
        {
            // Greedy: extend while the estimated size fits.
            double size = _overhead + units[start].Cost;
            var last = start;
            while (units.Ensure(last + 1) && size + units[last + 1].Cost <= _target)
            {
                last++;
                size += units[last].Cost;
            }

            var atEnd = !units.Ensure(last + 1);
            if (last <= prevEnd)
            {
                // The overlap left no room for new content: restart right after the previous chunk.
                if (atEnd || start == prevEnd + 1)
                {
                    break;
                }

                start = prevEnd + 1;
                continue;
            }

            var end = atEnd ? last : ChooseCut(units, start, last, prevEnd);
            string chunkText;
            while (true)
            {
                chunkText = Collapse(text.AsSpan(units[start].Start, units[end].End - units[start].Start));
                if (end == start || end - 1 <= prevEnd || MeasureChunk(chunkText) <= _target)
                {
                    break;
                }

                end--; // the additive estimate was slightly optimistic for this tokenizer
            }

            var chunkStart = units[start].Start;
            chunks.Add(new TextChunk(chunks.Count, chunkStart, units[end].End - chunkStart, chunkText));
            prevEnd = end;
            if (!units.Ensure(end + 1))
            {
                break;
            }

            // Overlap: step back over trailing units worth at most OverlapTokens.
            var next = end + 1;
            double overlap = 0;
            while (next - 1 > start && overlap + units[next - 1].Cost <= _overlap)
            {
                next--;
                overlap += units[next].Cost;
            }

            start = next;
        }

        return chunks;
    }

    /// <summary>Picks the strongest boundary within [start, last] that leaves the chunk reasonably full.</summary>
    private int ChooseCut(UnitScanner units, int start, int last, int prevEnd)
    {
        var best = last;
        var bestStrength = -1;
        double size = _overhead;
        var minFill = _target * MinFillForPreferredBreak;
        for (var k = start; k <= last; k++)
        {
            size += units[k].Cost;
            if (k <= prevEnd || (size < minFill && k != last))
            {
                continue;
            }

            var strength = (int)units[k + 1].Break;
            if (strength >= bestStrength)
            {
                bestStrength = strength;
                best = k;
            }
        }

        return best;
    }

    private int MeasureChunk(string chunkText) =>
        _counter is null ? (int)Math.Ceiling(chunkText.Length / CharsPerToken) : _counter(chunkText);

    /// <summary>
    /// Estimated budget consumption of a span (excluding the per-chunk overhead), or +∞ when the span is too
    /// long to be worth measuring as a whole.
    /// </summary>
    private double Cost(ReadOnlySpan<char> span)
    {
        if (_counter is null)
        {
            // +1 accounts for the separator space between units in the collapsed chunk text.
            return (span.Length + 1) / CharsPerToken;
        }

        if (span.Length > (long)_target * MaxCharsPerTokenToMeasure)
        {
            return double.PositiveInfinity;
        }

        return Math.Max(0, _counter(Collapse(span)) - _overhead);
    }

    private double Budget => _target - _overhead;

    /// <summary>
    /// Collapses every whitespace run to one character: a line break when the run contains one, otherwise a space
    /// (input spans never start/end with whitespace). Keeping line breaks preserves the structure of mail headers,
    /// lists, table rows and code, which the search layer uses as sentence boundaries for snippets.
    /// </summary>
    internal static string Collapse(ReadOnlySpan<char> span)
    {
        var builder = new StringBuilder(span.Length);
        var inWhitespace = false;
        var sawLineBreak = false;
        foreach (var c in span)
        {
            if (char.IsWhiteSpace(c))
            {
                inWhitespace = true;
                sawLineBreak |= IsLineBreak(c);
                continue;
            }

            if (inWhitespace && builder.Length > 0)
            {
                builder.Append(sawLineBreak ? '\n' : ' ');
            }

            inWhitespace = false;
            sawLineBreak = false;
            builder.Append(c);
        }

        return builder.ToString();
    }

    private static bool IsLineBreak(char c) => c is '\n' or '\r' or '\u2028' or '\u2029' or '\u0085';

    /// <summary>Boundary strength before a unit; higher is a better place to cut.</summary>
    internal enum Boundary : byte
    {
        None = 0,
        Word = 1,
        Line = 2,
        Sentence = 3,
        Paragraph = 4,
    }

    internal readonly record struct Unit(int Start, int End, Boundary Break, double Cost);

    /// <summary>Lazily segments text into sentence/line units (split further into words or grapheme runs when
    /// they exceed the budget), stopping once enough has been produced for MaxChunks chunks.</summary>
    private sealed class UnitScanner(TextChunker owner, string text)
    {
        private readonly List<Unit> _units = [];
        private int _pos;
        private bool _done;
        private bool _prevEndedSentence;
        private double _generatedCost;

        public Unit this[int index] => _units[index];

        public bool Ensure(int index)
        {
            while (_units.Count <= index && !_done)
            {
                ScanNext();
            }

            return _units.Count > index;
        }

        private bool Saturated => _generatedCost > (owner._maxChunks + 1.0) * owner._target;

        private void ScanNext()
        {
            if (Saturated)
            {
                _done = true;
                return;
            }

            // Gap: whitespace before the next unit, classified by the line breaks it contains.
            var newlines = 0;
            while (_pos < text.Length && char.IsWhiteSpace(text[_pos]))
            {
                var c = text[_pos];
                if (c == '\n' || c == '\u2028' || (c == '\r' && (_pos + 1 >= text.Length || text[_pos + 1] != '\n')))
                {
                    newlines++;
                }
                else if (c == '\u2029' || c == '\f')
                {
                    newlines += 2;
                }

                _pos++;
            }

            if (_pos >= text.Length)
            {
                _done = true;
                return;
            }

            var boundary = _units.Count == 0 ? Boundary.None
                : newlines >= 2 ? Boundary.Paragraph
                : _prevEndedSentence ? Boundary.Sentence
                : newlines == 1 ? Boundary.Line
                : Boundary.Word;

            var start = _pos;
            var end = text.Length;
            var endsSentence = false;
            var i = start;
            while (i < text.Length)
            {
                var c = text[i];
                if (c is '\n' or '\r' or '\u2028' or '\u2029' or '\f')
                {
                    end = i;
                    break;
                }

                if (IsTerminator(c))
                {
                    var j = i + 1;
                    while (j < text.Length && (IsTerminator(text[j]) || IsCloser(text[j])))
                    {
                        j++;
                    }

                    if (j >= text.Length || char.IsWhiteSpace(text[j]) || IsFullWidthTerminator(c))
                    {
                        end = j;
                        endsSentence = true;
                        break;
                    }

                    i = j;
                    continue;
                }

                i++;
            }

            _pos = end;
            while (end > start && char.IsWhiteSpace(text[end - 1]))
            {
                end--;
            }

            _prevEndedSentence = endsSentence;
            AddSpan(start, end, boundary);
        }

        private void AddSpan(int start, int end, Boundary boundary)
        {
            var cost = owner.Cost(text.AsSpan(start, end - start));
            if (cost <= owner.Budget)
            {
                Add(new Unit(start, end, boundary, cost));
                return;
            }

            // Too long for one chunk: split at whitespace into words.
            var i = start;
            var first = true;
            while (i < end && !Saturated)
            {
                while (i < end && char.IsWhiteSpace(text[i]))
                {
                    i++;
                }

                var wordStart = i;
                while (i < end && !char.IsWhiteSpace(text[i]))
                {
                    i++;
                }

                if (wordStart == i)
                {
                    break;
                }

                var wordBoundary = first ? boundary : Boundary.Word;
                first = false;
                var wordCost = owner.Cost(text.AsSpan(wordStart, i - wordStart));
                if (wordCost <= owner.Budget)
                {
                    Add(new Unit(wordStart, i, wordBoundary, wordCost));
                }
                else
                {
                    HardSplit(wordStart, i, wordBoundary);
                }
            }
        }

        /// <summary>Splits an overlong word into maximal pieces that fit, cutting only at grapheme boundaries.</summary>
        private void HardSplit(int start, int end, Boundary boundary)
        {
            var pos = start;
            while (pos < end && !Saturated)
            {
                var window = (int)Math.Min(end - pos, (long)Math.Max(1, owner.Budget) * HardSplitCharsPerToken + 16);
                var bounds = GraphemeBoundaries(pos, window, truncated: window < end - pos); // ascending absolute offsets
                int cut;
                if (owner._counter is null)
                {
                    var maxLen = Math.Max(1, (int)Math.Floor(owner.Budget * CharsPerToken) - 1);
                    cut = bounds[0];
                    foreach (var b in bounds)
                    {
                        if (b - pos > maxLen)
                        {
                            break;
                        }

                        cut = b;
                    }
                }
                else
                {
                    // Largest boundary whose prefix fits (binary search; cost is monotone in length).
                    int lo = 0, hi = bounds.Count - 1, found = 0;
                    while (lo <= hi)
                    {
                        var mid = (lo + hi) >>> 1;
                        if (owner.Cost(text.AsSpan(pos, bounds[mid] - pos)) <= owner.Budget)
                        {
                            found = mid;
                            lo = mid + 1;
                        }
                        else
                        {
                            hi = mid - 1;
                        }
                    }

                    cut = bounds[found];
                }

                var cost = owner.Cost(text.AsSpan(pos, cut - pos));
                Add(new Unit(pos, cut, pos == start ? boundary : Boundary.None, Math.Min(cost, owner.Budget)));
                pos = cut;
            }
        }

        private List<int> GraphemeBoundaries(int pos, int window, bool truncated)
        {
            var bounds = new List<int>();
            var span = text.AsSpan(pos, window);
            var offset = 0;
            while (offset < span.Length)
            {
                var length = StringInfo.GetNextTextElementLength(span[offset..]);
                if (length <= 0)
                {
                    length = 1;
                }

                offset += length;
                bounds.Add(pos + Math.Min(offset, span.Length));
            }

            // The window end is an artificial cut: the grapheme there may continue beyond it. Drop it unless it is
            // the only candidate, and in that case at least never split a surrogate pair.
            var lastBound = bounds[^1];
            if (truncated)
            {
                if (bounds.Count > 1)
                {
                    bounds.RemoveAt(bounds.Count - 1);
                }
                else if (char.IsLowSurrogate(text[lastBound]) && char.IsHighSurrogate(text[lastBound - 1]))
                {
                    bounds[0] = lastBound + 1;
                }
            }

            return bounds;
        }

        private void Add(Unit unit)
        {
            _units.Add(unit);
            _generatedCost += unit.Cost;
        }

        private static bool IsTerminator(char c) => c is '.' or '!' or '?' or '\u2026' || IsFullWidthTerminator(c);

        private static bool IsFullWidthTerminator(char c) => c is '\u3002' or '\uFF01' or '\uFF1F' or '\uFF0E';

        private static bool IsCloser(char c) => c is '"' or '\'' or ')' or ']' or '}' or '\u201D' or '\u2019' or '\u00BB'
            or '\u300D' or '\u300F' or '\uFF09';
    }
}
