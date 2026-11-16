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

    /// <summary>Distinct stemmed query terms (≥ 3 chars; ≥ 2 chars for words in scripts written without spaces, where
    /// two characters are often a whole word, e.g. 契約). <paramref name="lastTermPrefix"/> is the raw last word
    /// (lower-cased) when it is long enough, so the word the user is still typing can match as a prefix.</summary>
    public static (HashSet<string> Stems, string? LastTermPrefix) QueryTerms(string query)
    {
        var stems = new HashSet<string>(StringComparer.Ordinal);
        string? last = null;
        var words = Words(query);
        foreach (var span in words)
        {
            var word = query.Substring(span.Start, span.Length);
            if (word.Length < MinTermLength && !(word.Length >= MinUnspacedTermLength && ContainsUnspacedScript(word)))
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

    /// <summary>
    /// True when the word at <paramref name="span"/> matches a query term: same stem, starts with the word the user is
    /// still typing, or, for long terms (≥ <see cref="MinCompoundPrefixLength"/> chars), starts with the term, so
    /// compounds match their first part ("depositum" → "Depositumbeløbet", "lejekontrakt" → "Lejekontrakten").
    /// </summary>
    public static bool Matches(string text, TextSpan span, HashSet<string> stems, string? lastTermPrefix)
    {
        if (span.Length < 2)
            return false;
        var word = text.Substring(span.Start, span.Length);
        if (stems.Contains(Stem(word)))
            return true;
        if (lastTermPrefix is not null && word.StartsWith(lastTermPrefix, StringComparison.OrdinalIgnoreCase))
            return true;
        if (word.Length > MinCompoundPrefixLength)
        {
            foreach (var stem in stems)
            {
                if (stem.Length >= MinCompoundPrefixLength && stem.Length < word.Length && word.StartsWith(stem, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
        }
        return false;
    }

    /// <summary>
    /// All spans of <paramref name="text"/> matching the query terms: whole words (see <see cref="Matches"/>) and, inside
    /// runs of scripts written without spaces (CJK, Thai), every stretch of ≥ 2 characters that also occurs in a query
    /// term (本賃貸借契約は with query 賃貸契約 → 賃貸, 契約).
    /// </summary>
    public static List<TextSpan> LexicalMatches(string text, HashSet<string> stems, string? lastTermPrefix)
    {
        var result = new List<TextSpan>();
        if (stems.Count == 0 && lastTermPrefix is null)
            return result;
        List<string>? unspacedTerms = null;
        foreach (var span in Words(text))
        {
            if (span.Length >= MinUnspacedTermLength && ContainsUnspacedScript(text.AsSpan(span.Start, span.Length)))
            {
                unspacedTerms ??= UnspacedTerms(stems, lastTermPrefix);
                if (unspacedTerms.Count > 0)
                {
                    AddSubstringMatches(text, span, unspacedTerms, result);
                    continue;
                }
            }
            if (Matches(text, span, stems, lastTermPrefix))
                result.Add(span);
        }
        return result;
    }

    /// <summary>Shortest term matched inside runs of scripts written without spaces.</summary>
    public const int MinUnspacedTermLength = 2;

    /// <summary>Terms at least this long also match as the first part of a longer (compound) word.</summary>
    public const int MinCompoundPrefixLength = 5;

    private static List<string> UnspacedTerms(HashSet<string> stems, string? lastTermPrefix)
    {
        var terms = new List<string>();
        foreach (var stem in stems)
        {
            if (stem.Length >= MinUnspacedTermLength && ContainsUnspacedScript(stem))
                terms.Add(stem);
        }
        if (lastTermPrefix is not null && lastTermPrefix.Length >= MinUnspacedTermLength && ContainsUnspacedScript(lastTermPrefix))
            terms.Add(lastTermPrefix);
        return terms;
    }

    /// <summary>Greedy left-to-right: at each position take the longest stretch (≥ 2 chars) found in any term.</summary>
    private static void AddSubstringMatches(string text, TextSpan word, List<string> terms, List<TextSpan> result)
    {
        var maxLength = terms.Max(t => t.Length);
        var end = word.Start + word.Length;
        var i = word.Start;
        while (i < end)
        {
            var matched = 0;
            for (var length = Math.Min(maxLength, end - i); length >= MinUnspacedTermLength && matched == 0; length--)
            {
                if (char.IsHighSurrogate(text[i + length - 1]))
                    continue; // never end inside a surrogate pair
                var piece = text.AsSpan(i, length);
                foreach (var term in terms)
                {
                    if (term.AsSpan().Contains(piece, StringComparison.OrdinalIgnoreCase))
                    {
                        matched = length;
                        break;
                    }
                }
            }
            if (matched > 0)
            {
                result.Add(new TextSpan(i, matched));
                i += matched;
            }
            else
            {
                i += char.IsHighSurrogate(text[i]) && i + 1 < end ? 2 : 1;
            }
        }
    }

    /// <summary>True when <paramref name="text"/> contains a character of a script normally written without spaces
    /// between words: Han, Hiragana, Katakana, Hangul or Thai.</summary>
    public static bool ContainsUnspacedScript(ReadOnlySpan<char> text)
    {
        for (var i = 0; i < text.Length; i++)
        {
            int cp = text[i];
            if (char.IsHighSurrogate(text[i]) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]))
            {
                cp = char.ConvertToUtf32(text[i], text[i + 1]);
                i++;
            }
            if (IsUnspacedScript(cp))
                return true;
        }
        return false;
    }

    private static bool IsUnspacedScript(int cp) =>
        cp is (>= 0x0E00 and <= 0x0E7F) // Thai
            or (>= 0x1100 and <= 0x11FF) // Hangul Jamo
            or (>= 0x3040 and <= 0x30FF) // Hiragana, Katakana
            or (>= 0x3130 and <= 0x318F) // Hangul compatibility Jamo
            or (>= 0x31F0 and <= 0x31FF) // Katakana phonetic extensions
            or (>= 0x3400 and <= 0x4DBF) // CJK extension A
            or (>= 0x4E00 and <= 0x9FFF) // CJK unified ideographs
            or (>= 0xAC00 and <= 0xD7AF) // Hangul syllables
            or (>= 0xF900 and <= 0xFAFF) // CJK compatibility ideographs
            or (>= 0xFF66 and <= 0xFF9F) // half-width Katakana
            or (>= 0x20000 and <= 0x3134F); // CJK extensions B-G

    /// <summary>
    /// Whole tokens of a file name stem for name matching: words split further at letter/digit changes and camelCase
    /// humps ("TaxReturn2024" → Tax, Return, 2024; "HTMLParser" → HTML, Parser).
    /// </summary>
    public static List<string> NameTokens(string name)
    {
        var tokens = new List<string>();
        foreach (var span in Words(name))
        {
            var start = span.Start;
            var end = span.Start + span.Length;
            for (var i = start + 1; i < end; i++)
            {
                var prev = name[i - 1];
                var c = name[i];
                var cut = (char.IsLetter(prev) && char.IsDigit(c))
                    || (char.IsDigit(prev) && char.IsLetter(c))
                    || (char.IsLower(prev) && char.IsUpper(c))
                    || (char.IsUpper(prev) && char.IsUpper(c) && i + 1 < end && char.IsLower(name[i + 1]));
                if (cut)
                {
                    tokens.Add(name[start..i]);
                    start = i;
                }
            }
            tokens.Add(name[start..end]);
        }
        return tokens;
    }

    /// <summary>True for common function words (en, da, nb, sv, de, nl, fr, es, it, pt) that are no evidence of a
    /// match on their own, e.g. for the file-name boost.</summary>
    public static bool IsStopword(string word) => Stopwords.Contains(word);

    private static readonly HashSet<string> Stopwords = new(StringComparer.OrdinalIgnoreCase)
    {
        // English
        "the", "and", "for", "from", "with", "about", "into", "onto", "over", "under", "that", "this", "these", "those",
        "are", "was", "were", "been", "being", "have", "has", "had", "not", "but", "any", "all", "can", "you", "your",
        "our", "their", "his", "her", "its", "who", "what", "when", "where", "which", "why", "how", "there", "here",
        "then", "than", "also", "out", "off", "via", "per", "some", "more", "most", "does", "did", "will", "would",
        "should", "could", "may", "might", "must", "shall", "them", "they", "she", "him", "one",
        // Danish / Norwegian / Swedish
        "til", "fra", "med", "som", "det", "den", "der", "har", "ikke", "eller", "efter", "etter", "mellem", "mellom",
        "mellan", "hvor", "hvad", "hva", "vad", "hvem", "vem", "når", "när", "kan", "skal", "ska", "vil", "var",
        "blev", "ble", "blir", "bliver", "hos", "mod", "mot", "uden", "uten", "utan", "och", "jeg", "mig", "meg",
        "dig", "deg", "han", "hun", "hon", "vores", "vår", "våre", "deres", "dere", "denne", "dette", "disse",
        "dessa", "detta", "sin", "sit", "sitt", "sine", "sina", "alle", "alla", "også", "också", "men", "end",
        "enn", "inte", "ingen", "noget", "noe", "något",
        // German / Dutch
        "und", "die", "das", "dem", "des", "ein", "eine", "einen", "einem", "einer", "eines", "mit", "von", "vom",
        "zum", "zur", "für", "auf", "aus", "bei", "nach", "über", "unter", "oder", "nicht", "ist", "sind", "war",
        "wie", "wer", "auch", "noch", "aber", "dass", "het", "een", "van", "voor", "met", "niet", "naar", "bij",
        "ook", "zijn", "wat",
        // French / Spanish / Italian / Portuguese
        "les", "une", "dans", "pour", "par", "sur", "avec", "sans", "est", "sont", "qui", "que", "quoi", "aux",
        "ces", "son", "ses", "leur", "leurs", "pas", "plus", "los", "las", "del", "una", "uno", "unos", "unas",
        "con", "para", "por", "sobre", "entre", "como", "pero", "más", "este", "esta", "esto", "estos",
        "estas", "che", "della", "delle", "degli", "dei", "nel", "nella", "gli", "non", "uma", "dos", "nos",
        "nas", "pelo", "pela", "mais", "não",
    };

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
