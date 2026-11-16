using System.Text;
using Valafind.Core.Abstractions;

namespace Valafind.Core.Search;

/// <summary>
/// A display snippet cut from a chunk: a window of ≈ <see cref="TargetLength"/> characters around a focus span,
/// trimmed at word boundaries, with whitespace runs collapsed to single spaces and "…" marking cut ends.
/// Keeps a map from source offsets to snippet offsets so spans in the chunk can be projected into the snippet.
/// </summary>
internal sealed class Snippet
{
    public const int TargetLength = 240;

    /// <summary>How far a window edge may move to reach a word boundary.</summary>
    private const int MaxBoundaryShift = 24;

    private const char Ellipsis = '…';

    private readonly int[] _map;

    private Snippet(string text, int sourceStart, int sourceEnd, int[] map)
    {
        Text = text;
        SourceStart = sourceStart;
        SourceEnd = sourceEnd;
        _map = map;
    }

    public static Snippet Empty { get; } = new(string.Empty, 0, 0, []);

    public string Text { get; }

    /// <summary>Window start in the source text (inclusive).</summary>
    public int SourceStart { get; }

    /// <summary>Window end in the source text (exclusive).</summary>
    public int SourceEnd { get; }

    /// <summary>Builds a window of <paramref name="source"/> centred on the focus span.</summary>
    public static Snippet Build(string source, TextSpan focus, int targetLength = TargetLength)
    {
        if (string.IsNullOrEmpty(source))
            return Empty;

        var focusStart = Math.Clamp(focus.Start, 0, source.Length);
        var focusEnd = Math.Clamp(focus.Start + focus.Length, focusStart, source.Length);

        int start;
        if (focusEnd - focusStart >= targetLength)
            start = focusStart;
        else
            start = (focusStart + focusEnd) / 2 - targetLength / 2;
        start = Math.Clamp(start, 0, Math.Max(0, source.Length - targetLength));
        var end = Math.Min(source.Length, start + targetLength);

        // Never begin or end in the middle of a word (unless the word is absurdly long).
        if (start > 0 && !IsBoundary(source, start))
        {
            var s = start;
            while (s < end && s - start <= MaxBoundaryShift && !char.IsWhiteSpace(source[s]))
                s++;
            if (s < end && char.IsWhiteSpace(source[s]))
                start = s;
        }
        if (end < source.Length && !IsBoundary(source, end))
        {
            var e = end;
            while (e > start && end - e <= MaxBoundaryShift && !char.IsWhiteSpace(source[e - 1]))
                e--;
            if (e > start && char.IsWhiteSpace(source[e - 1]))
                end = e;
        }
        // Do not split surrogate pairs.
        if (start > 0 && start < source.Length && char.IsLowSurrogate(source[start]))
            start++;
        if (end > start && end < source.Length && char.IsLowSurrogate(source[end]))
            end--;

        var map = new int[end - start];
        var sb = new StringBuilder(end - start + 2);
        if (start > 0)
            sb.Append(Ellipsis);
        var contentStart = sb.Length;
        var pendingSpace = false;

        for (var i = start; i < end; i++)
        {
            var c = source[i];
            if (char.IsWhiteSpace(c) || (char.IsControl(c) && !char.IsSurrogate(c)))
            {
                // Leading whitespace is dropped; inner runs collapse into the single space emitted before the next word.
                pendingSpace = sb.Length > contentStart;
                map[i - start] = -1;
                continue;
            }
            if (pendingSpace)
            {
                sb.Append(' ');
                pendingSpace = false;
            }
            map[i - start] = sb.Length;
            sb.Append(c);
        }

        if (sb.Length == contentStart)
            return Empty; // Window held only whitespace.
        if (end < source.Length)
            sb.Append(Ellipsis);
        return new Snippet(sb.ToString(), start, end, map);
    }

    private static bool IsBoundary(string s, int index) =>
        index <= 0 || index >= s.Length || char.IsWhiteSpace(s[index - 1]) || char.IsWhiteSpace(s[index]);

    /// <summary>Projects a span of the source text into the snippet (clipped to the window).
    /// Returns null when nothing of it is visible.</summary>
    public TextSpan? MapSourceSpan(TextSpan span)
    {
        var s = Math.Max(span.Start, SourceStart);
        var e = Math.Min(span.Start + span.Length, SourceEnd);
        if (e <= s)
            return null;

        var first = -1;
        for (var i = s; i < e && first < 0; i++)
            first = _map[i - SourceStart];
        var last = -1;
        for (var i = e - 1; i >= s && last < 0; i--)
            last = _map[i - SourceStart];
        if (first < 0 || last < first)
            return null;
        return new TextSpan(first, last - first + 1);
    }
}
