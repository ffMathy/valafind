using System.Globalization;
using Valafind.Core.Abstractions;

namespace Valafind.Core.Search;

/// <summary>Unicode-aware tokenisation, light English stemming and sentence splitting used for snippets and highlights.</summary>
internal static class TextAnalysis
{
    /// <summary>Query terms shorter than this are ignored for lexical matching.</summary>
    public const int MinTermLength = 3;

    /// <summary>Sentences longer than this are split further (at word boundaries).</summary>
    public const int MaxSentenceChars = 240;

    /// <summary>Enumerates word spans: runs of letters, digits and combining marks (surrogate pairs included).</summary>
    public static List<TextSpan> Words(string text)
    {
        var words = new List<TextSpan>();
        var i = 0;
        while (i < text.Length)
        {
            var width = WordCharWidth(text, i);
            if (width == 0)
            {
                i += char.IsHighSurrogate(text[i]) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]) ? 2 : 1;
                continue;
            }
            var start = i;
            while (i < text.Length && (width = WordCharWidth(text, i)) > 0)
                i += width;
            words.Add(new TextSpan(start, i - start));
        }
        return words;
    }

    /// <summary>Returns the UTF-16 width of the word character at <paramref name="index"/>, or 0 if it is not one.</summary>
    private static int WordCharWidth(string text, int index)
    {
        var c = text[index];
        if (char.IsHighSurrogate(c))
        {
            if (index + 1 < text.Length && char.IsLowSurrogate(text[index + 1]))
                return IsWordCategory(CharUnicodeInfo.GetUnicodeCategory(text, index)) ? 2 : 0;
            return 0;
        }
        if (char.IsLowSurrogate(c))
            return 0;
        return IsWordCategory(CharUnicodeInfo.GetUnicodeCategory(c)) ? 1 : 0;
    }

    private static bool IsWordCategory(UnicodeCategory category) => category switch
    {
        UnicodeCategory.UppercaseLetter or UnicodeCategory.LowercaseLetter or UnicodeCategory.TitlecaseLetter
            or UnicodeCategory.ModifierLetter or UnicodeCategory.OtherLetter
            or UnicodeCategory.DecimalDigitNumber or UnicodeCategory.LetterNumber or UnicodeCategory.OtherNumber
            or UnicodeCategory.NonSpacingMark or UnicodeCategory.SpacingCombiningMark => true,
        _ => false,
    };

    /// <summary>
    /// Very small English stemmer: lower-cases, strips one of -ing / -ed / -es / -s, undoubles a final consonant
    /// ("running" → "run") and drops a final silent "e" so "file", "files", "filed" and "filing" share a stem.
    /// Words that would end up shorter than three characters keep their suffix.
    /// </summary>
    public static string Stem(string word)
    {
        var w = word.ToLowerInvariant();
        var stripped = false;

        if (w.Length >= 6 && w.EndsWith("ing", StringComparison.Ordinal))
        {
            w = w[..^3];
            stripped = true;
        }
        else if (w.Length >= 5 && w.EndsWith("ed", StringComparison.Ordinal))
        {
            w = w[..^2];
            stripped = true;
        }
        else if (w.Length >= 5 && w.EndsWith("es", StringComparison.Ordinal) && IsSibilantEnding(w.AsSpan(0, w.Length - 2)))
        {
            w = w[..^2];
        }
        else if (w.Length >= 4 && w[^1] == 's' && w[^2] != 's' && w[^2] != 'u' && w[^2] != 'i')
        {
            w = w[..^1];
        }

        if (stripped && w.Length >= 4 && w[^1] == w[^2] && IsUndoublable(w[^1]))
            w = w[..^1];
        if (w.Length >= 4 && w[^1] == 'e')
            w = w[..^1];
        return w;
    }

    private static bool IsSibilantEnding(ReadOnlySpan<char> stem) =>
        stem.EndsWith("ss") || stem.EndsWith("x") || stem.EndsWith("z") || stem.EndsWith("ch") || stem.EndsWith("sh");

    private static bool IsUndoublable(char c) => c is >= 'a' and <= 'z' && "aeioulsz".IndexOf(c) < 0;

    /// <summary>Distinct stemmed query terms (≥ 3 chars). <paramref name="lastTermPrefix"/> is the raw last word
    /// (lower-cased) when it is long enough, so the word the user is still typing can match as a prefix.</summary>
    public static (HashSet<string> Stems, string? LastTermPrefix) QueryTerms(string query)
    {
        var stems = new HashSet<string>(StringComparer.Ordinal);
        string? last = null;
        var words = Words(query);
        foreach (var span in words)
        {
            var word = query.Substring(span.Start, span.Length);
            if (word.Length < MinTermLength)
                continue;
            stems.Add(Stem(word));
        }
        if (words.Count > 0)
        {
            var lastSpan = words[^1];
            // Only when the query ends inside that word, i.e. the user is still typing it.
            if (lastSpan.Length >= MinTermLength && lastSpan.Start + lastSpan.Length == query.Length)
                last = query.Substring(lastSpan.Start, lastSpan.Length).ToLowerInvariant();
        }
        return (stems, last);
    }

    /// <summary>True when the word at <paramref name="span"/> matches a query term.</summary>
    public static bool Matches(string text, TextSpan span, HashSet<string> stems, string? lastTermPrefix)
    {
        if (span.Length < 2)
            return false;
        var word = text.Substring(span.Start, span.Length);
        if (stems.Contains(Stem(word)))
            return true;
        return lastTermPrefix is not null && word.StartsWith(lastTermPrefix, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>All word spans of <paramref name="text"/> matching the query terms.</summary>
    public static List<TextSpan> LexicalMatches(string text, HashSet<string> stems, string? lastTermPrefix)
    {
        var result = new List<TextSpan>();
        if (stems.Count == 0 && lastTermPrefix is null)
            return result;
        foreach (var span in Words(text))
        {
            if (Matches(text, span, stems, lastTermPrefix))
                result.Add(span);
        }
        return result;
    }

    /// <summary>
    /// Splits text into sentence spans (trimmed, each containing at least one word character). Boundaries are
    /// sentence punctuation followed by whitespace, and line breaks. Sentences longer than
    /// <see cref="MaxSentenceChars"/> are cut at word boundaries.
    /// </summary>
    public static List<TextSpan> Sentences(string text)
    {
        var result = new List<TextSpan>();
        var start = 0;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            var boundary = false;
            var end = i;
            if (c is '\n' or '\r' or '\u2029' or '\u2028')
            {
                boundary = true;
            }
            else if (c is '.' or '!' or '?' or '…' or '。' or '！' or '？' or '؟' or '।')
            {
                // Swallow runs like "?!" or "...", plus closing quotes / brackets.
                var j = i + 1;
                while (j < text.Length && (text[j] is '.' or '!' or '?' or '…' or '"' or '\'' or ')' or ']' or '”' or '’' or '»'))
                    j++;
                var fullWidth = c is '。' or '！' or '？';
                if (j >= text.Length || char.IsWhiteSpace(text[j]) || fullWidth)
                {
                    boundary = true;
                    end = j;
                    i = j - 1;
                }
            }

            if (boundary)
            {
                AddSentence(text, start, end, result);
                start = end;
            }
        }
        AddSentence(text, start, text.Length, result);
        return result;
    }

    private static void AddSentence(string text, int start, int end, List<TextSpan> result)
    {
        while (start < end && (char.IsWhiteSpace(text[start]) || char.IsControl(text[start])))
            start++;
        while (end > start && (char.IsWhiteSpace(text[end - 1]) || char.IsControl(text[end - 1])))
            end--;
        if (end <= start)
            return;

        var hasWord = false;
        for (var i = start; i < end && !hasWord; i++)
            hasWord = WordCharWidth(text, i) > 0;
        if (!hasWord)
            return;

        while (end - start > MaxSentenceChars)
        {
            var cut = start + MaxSentenceChars;
            var back = cut;
            while (back > start + MaxSentenceChars / 2 && !char.IsWhiteSpace(text[back]))
                back--;
            if (back > start + MaxSentenceChars / 2)
                cut = back;
            else if (char.IsLowSurrogate(text[cut]))
                cut--;
            var pieceEnd = cut;
            while (pieceEnd > start && char.IsWhiteSpace(text[pieceEnd - 1]))
                pieceEnd--;
            result.Add(new TextSpan(start, pieceEnd - start));
            start = cut;
            while (start < end && char.IsWhiteSpace(text[start]))
                start++;
        }
        if (end > start)
            result.Add(new TextSpan(start, end - start));
    }

    /// <summary>
    /// Up to <paramref name="max"/> sentences of <paramref name="text"/>, in text order. When there are more,
    /// sentences containing query terms are preferred, then earlier ones.
    /// </summary>
    public static List<TextSpan> CandidateSentences(string text, int max, HashSet<string> stems, string? lastTermPrefix)
    {
        var all = Sentences(text);
        if (all.Count <= max)
            return all;
        return all
            .Select((span, position) => (span, position, matches: CountMatches(text, span, stems, lastTermPrefix)))
            .OrderByDescending(x => x.matches)
            .ThenBy(x => x.position)
            .Take(max)
            .OrderBy(x => x.position)
            .Select(x => x.span)
            .ToList();
    }

    /// <summary>Number of query-term matches inside <paramref name="span"/> of <paramref name="text"/>.</summary>
    public static int CountMatches(string text, TextSpan span, HashSet<string> stems, string? lastTermPrefix)
    {
        if (stems.Count == 0 && lastTermPrefix is null)
            return 0;
        var sentence = text.Substring(span.Start, span.Length);
        return LexicalMatches(sentence, stems, lastTermPrefix).Count;
    }
}
