using System.IO.Compression;
using System.Runtime.InteropServices;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Tokens;

namespace Valafind.Core.Extraction;

/// <summary>
/// Bounds what PdfPig may decompress. PdfPig inflates whole streams into memory with no cap, so a sub-megabyte
/// PDF can hold a FlateDecode stream that expands to gigabytes. Before PdfPig touches a stream, this guard
/// measures its decoded size itself, with counting decoders that stop at a cap and never keep the output.
/// </summary>
/// <remarks>
/// <para>Two passes:</para>
/// <list type="number">
/// <item><see cref="ScanFile"/> runs before the document is opened. It finds every stream object in the raw
/// file bytes (stream objects can never be inside object streams, so their dictionaries are always in plain
/// view), parses its dictionary structurally from the object header, reads the top-level /Filter chain and
/// measures it. Expanding filter names spelled anywhere else in the dictionary text are measured as well, and
/// an object or xref stream whose dictionary does not parse cleanly, or whose filter is indirect, rejects the
/// file. Any non-image stream that decodes to more than
/// <see cref="MaxStreamBytes"/>, or more than <see cref="MaxFileDecodedBytes"/> in total, rejects the file.
/// This covers object and cross-reference streams, which PdfPig decodes while it opens the file. Image
/// streams are not measured, because text extraction never decodes them. The exception is a stream that
/// carries object-stream or xref-stream keys, which is measured even when it is labelled as an image.</item>
/// <item><see cref="MeasurePage"/> runs before each page is parsed. It walks the streams PdfPig will decode
/// for that page (content streams, form XObjects, font programs, ToUnicode maps, Type 3 glyph procedures),
/// resolved through PdfPig's token API so the data is already decrypted. It sums their decoded sizes, so a
/// page over <see cref="MaxPageDecodedBytes"/> is skipped whatever its streams claim to be.</item>
/// </list>
/// <para>Supported measurements: FlateDecode (counting inflate), LZWDecode and RunLengthDecode (output length
/// computed without producing output), and ASCIIHex/ASCII85 as prefixes. Chains with two expanding filters
/// are treated as hostile.</para>
/// </remarks>
internal static class PdfStreamGuard
{
    /// <summary>Largest decoded size of one non-image stream anywhere in the file.</summary>
    internal const long MaxStreamBytes = 32L * 1024 * 1024;

    /// <summary>Total decoded bytes of all non-image streams the file may contain.</summary>
    internal const long MaxFileDecodedBytes = 512L * 1024 * 1024;

    /// <summary>Decoded bytes PdfPig may process for one page (content, forms, fonts).</summary>
    internal const long MaxPageDecodedBytes = 32L * 1024 * 1024;

    /// <summary>Maximum stream objects in one file.</summary>
    internal const int MaxStreams = 200_000;

    private const int MaxWalkNodes = 50_000;
    private const int MaxResourceDepth = 12;

    // ------------------------------------------------------------------ raw pre-scan

    /// <summary>
    /// Returns null when the file's streams stay within limits, or the reason the file must not be parsed.
    /// </summary>
    internal static string? ScanFile(byte[] file, CancellationToken cancellationToken)
    {
        var total = 0L;
        var streams = 0;
        var position = 0;
        var scratch = new byte[64 * 1024];
        var chains = new List<IReadOnlyList<string>>();
        while (position < file.Length)
        {
            var found = file.AsSpan(position).IndexOf("stream"u8);
            if (found < 0)
            {
                break;
            }

            var keyword = position + found;
            position = keyword + 6;
            if (!TryGetStreamStart(file, keyword, out var raw))
            {
                continue;
            }

            if ((++streams & 0x3F) == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            if (streams > MaxStreams)
            {
                return $"more than {MaxStreams} streams";
            }

            // Names anywhere in the dictionary text, nested or not: a safety net under the structural parse.
            // Whatever PdfPig decodes with must be spelled somewhere in this text (or be an indirect /Filter).
            var text = file.AsSpan(raw.TextStart, raw.DictEnd - raw.TextStart);
            var textStructural = FindName(text, "ObjStm") >= 0 || FindName(text, "XRef") >= 0
                || FindName(text, "First") >= 0 || FindName(text, "W") >= 0;
            chains.Clear();
            var earlyChange = 1;
            if (raw.Dictionary is { } dict)
            {
                var structural = textStructural || IsStructural(dict);
                var filters = FiltersOf(dict);
                if (filters is null && structural)
                {
                    // PdfPig decodes object and xref streams while it opens the file, before any page check.
                    return "object or cross-reference stream with an undeterminable filter";
                }

                var extras = ExpandingNamesIn(text, filters);
                if (filters is { Count: 0 } && extras.Count == 0)
                {
                    continue; // nothing that expands
                }

                if (!structural && extras.Count == 0 && IsImage(dict))
                {
                    continue; // image data is never decoded for text extraction
                }

                if (filters is { Count: > 0 })
                {
                    chains.Add(filters);
                }

                // An indirect /Filter on another stream is measured per page if PdfPig ever decodes it.
                chains.AddRange(extras.Select(name => (IReadOnlyList<string>)[name]));
                earlyChange = EarlyChange(dict);
            }
            else
            {
                // The dictionary does not parse cleanly (comments, duplicate keys, broken syntax): only what
                // the text shows can be trusted, and an object or xref stream is not given the benefit of the doubt.
                if (textStructural && FindName(text, "Filter") >= 0)
                {
                    return "object or cross-reference stream with an unparseable dictionary";
                }

                chains.AddRange(ExpandingNamesIn(text, null).Select(name => (IReadOnlyList<string>)[name]));
            }

            long size = 0;
            foreach (var chain in chains)
            {
                var measured = MeasureDecoded(file, raw.DataStart, file.Length - raw.DataStart, chain, earlyChange, MaxStreamBytes, scratch);
                if (measured is null)
                {
                    return "stream with chained expanding filters";
                }

                size = Math.Max(size, measured.Value);
            }

            if (size > MaxStreamBytes)
            {
                return $"stream decodes to more than {MaxStreamBytes} bytes";
            }

            total += size;
            if (total > MaxFileDecodedBytes)
            {
                return $"streams decode to more than {MaxFileDecodedBytes} bytes";
            }

            position = raw.DataStart;
        }

        return null;
    }

    /// <summary>A stream keyword found in the raw file, with its dictionary.</summary>
    /// <param name="DataStart">First byte of the stream data.</param>
    /// <param name="Dictionary">Top-level entries of the stream dictionary, or null when it does not parse
    /// cleanly from an object header.</param>
    /// <param name="TextStart">Start of the dictionary text that was examined (the object header when parsed).</param>
    /// <param name="DictEnd">Just past the closing "&gt;&gt;".</param>
    private readonly record struct RawStream(int DataStart, Dictionary<string, RawValue>? Dictionary, int TextStart, int DictEnd);

    private const int MaxDictionaryWindow = 64 * 1024;
    private const int MaxHeaderCandidates = 32;

    /// <summary>
    /// Checks that "stream" at <paramref name="keyword"/> is a stream keyword (after "&gt;&gt;", before an
    /// end-of-line) and parses its dictionary structurally: from an <c>N G obj</c> header, balanced
    /// <c>&lt;&lt; &gt;&gt;</c> with literal and hex strings skipped, and the parse must end exactly at the
    /// "&gt;&gt;" before the keyword. Headers are tried nearest first; one inside a string of the real
    /// dictionary cannot produce such a parse, because the real string's closing parenthesis is unbalanced
    /// from inside it, and comments (which could swallow it) fail the parse.
    /// </summary>
    private static bool TryGetStreamStart(byte[] file, int keyword, out RawStream raw)
    {
        raw = default;
        if (keyword >= 3 && file.AsSpan(keyword - 3, 3).SequenceEqual("end"u8))
        {
            return false;
        }

        var j = keyword + 6;
        if (j >= file.Length)
        {
            return false;
        }

        if (file[j] == '\r')
        {
            j++;
            if (j < file.Length && file[j] == '\n')
            {
                j++;
            }
        }
        else if (file[j] == '\n')
        {
            j++;
        }
        else
        {
            return false;
        }

        var b = keyword - 1;
        while (b >= 0 && IsWhite(file[b]))
        {
            b--;
        }

        if (b < 1 || file[b] != '>' || file[b - 1] != '>')
        {
            return false;
        }

        var dictEnd = b + 1;
        var floor = Math.Max(0, b - MaxDictionaryWindow);
        var textStart = floor;
        var searchEnd = b;
        var candidates = 0;
        while (candidates < MaxHeaderCandidates && searchEnd > floor)
        {
            var at = file.AsSpan(floor, searchEnd - floor).LastIndexOf("obj"u8);
            if (at < 0)
            {
                break;
            }

            at += floor;
            searchEnd = at;
            if (!TryGetObjectHeader(file, at, out var headerStart))
            {
                continue;
            }

            candidates++;
            textStart = headerStart;
            var parser = new RawParser(file.AsSpan(0, dictEnd), at + 3);
            if (parser.SkipWhite()
                && parser.AtDictionary
                && parser.ParseValue(0, out var value)
                && value.Entries is { } entries
                && parser.Position == dictEnd)
            {
                raw = new RawStream(j, entries, headerStart, dictEnd);
                return true;
            }
        }

        raw = new RawStream(j, null, textStart, dictEnd);
        return true;
    }

    /// <summary>True when "obj" at <paramref name="obj"/> ends an <c>N G obj</c> header.</summary>
    private static bool TryGetObjectHeader(byte[] file, int obj, out int headerStart)
    {
        headerStart = 0;
        if (obj + 3 < file.Length && !IsDelimiter(file[obj + 3]))
        {
            return false;
        }

        var i = obj - 1;
        if (i < 0 || !IsWhite(file[i]))
        {
            return false;
        }

        for (var number = 0; number < 2; number++)
        {
            while (i >= 0 && IsWhite(file[i]))
            {
                i--;
            }

            var digitsEnd = i;
            while (i >= 0 && file[i] is >= (byte)'0' and <= (byte)'9' && digitsEnd - i < 12)
            {
                i--;
            }

            if (i == digitsEnd)
            {
                return false;
            }
        }

        if (i >= 0 && !IsDelimiter(file[i]))
        {
            return false;
        }

        headerStart = i + 1;
        return true;
    }

    private static bool IsWhite(byte c) => c is (byte)' ' or (byte)'\r' or (byte)'\n' or (byte)'\t' or (byte)'\f' or 0;

    private static bool IsDelimiter(byte c) => IsWhite(c) || c is (byte)'/' or (byte)'[' or (byte)']' or (byte)'<' or (byte)'>'
        or (byte)'(' or (byte)')' or (byte)'{' or (byte)'}' or (byte)'%';

    /// <summary>Index just past the name token <c>/key</c> (matched as a whole name), or -1.</summary>
    private static int FindName(ReadOnlySpan<byte> text, string key, int from = 0)
    {
        Span<byte> name = stackalloc byte[key.Length + 1];
        name[0] = (byte)'/';
        for (var i = 0; i < key.Length; i++)
        {
            name[i + 1] = (byte)key[i];
        }

        var start = from;
        while (start < text.Length)
        {
            var at = text[start..].IndexOf(name);
            if (at < 0)
            {
                return -1;
            }

            var end = start + at + name.Length;
            if (end >= text.Length || IsDelimiter(text[end]))
            {
                return end;
            }

            start = end;
        }

        return -1;
    }

    private static readonly string[] ExpandingFilterNames = ["FlateDecode", "Fl", "LZWDecode", "LZW", "RunLengthDecode", "RL"];

    /// <summary>Expanding filter names spelled anywhere in <paramref name="text"/> whose kind is not already in
    /// <paramref name="chain"/>.</summary>
    private static List<string> ExpandingNamesIn(ReadOnlySpan<byte> text, IReadOnlyList<string>? chain)
    {
        var result = new List<string>();
        foreach (var name in ExpandingFilterNames)
        {
            var kind = Classify(name);
            if (FindName(text, name) >= 0
                && (chain is null || !chain.Any(f => Classify(f) == kind))
                && !result.Any(f => Classify(f) == kind))
            {
                result.Add(name);
            }
        }

        return result;
    }

    private static bool IsStructural(Dictionary<string, RawValue> dict) =>
        dict.ContainsKey("First") || dict.ContainsKey("W")
        || (dict.TryGetValue("Type", out var type) && type.Name is "ObjStm" or "XRef");

    private static bool IsImage(Dictionary<string, RawValue> dict) =>
        dict.TryGetValue("Subtype", out var subtype) && subtype.Name == "Image";

    /// <summary>The /Filter names in order; empty for none; null when the value is not a name or array of names.</summary>
    private static List<string>? FiltersOf(Dictionary<string, RawValue> dict)
    {
        if (!dict.TryGetValue("Filter", out var filter))
        {
            return [];
        }

        if (filter.Name is { } single)
        {
            return [single];
        }

        if (filter.Items is not { } items || items.Count > 16)
        {
            return null;
        }

        var result = new List<string>(items.Count);
        foreach (var item in items)
        {
            if (item.Name is not { } name)
            {
                return null; // indirect reference or junk inside the array
            }

            result.Add(name);
        }

        return result;
    }

    private static int EarlyChange(Dictionary<string, RawValue> dict)
    {
        if (!dict.TryGetValue("DecodeParms", out var parms))
        {
            return 1;
        }

        IEnumerable<RawValue> all = parms.Items is { } items ? items : [parms];
        return all.Any(p => p.Entries is { } e && e.TryGetValue("EarlyChange", out var ec) && ec.Number == 0) ? 0 : 1;
    }

    /// <summary>A parsed raw PDF value; only names, arrays, dictionaries and integers keep their content.</summary>
    private sealed record RawValue(string? Name = null, List<RawValue>? Items = null, Dictionary<string, RawValue>? Entries = null, long? Number = null);

    private static readonly RawValue OtherValue = new();

    /// <summary>
    /// A strict parser for PDF dictionary syntax in raw file bytes. It fails (rather than guesses) on comments,
    /// duplicate keys, unbalanced delimiters and non-hex characters in hex strings, so a dictionary it accepts
    /// means the same thing to PdfPig. Name escapes (<c>#xx</c>) are decoded, as PdfPig does.
    /// </summary>
    private ref struct RawParser(ReadOnlySpan<byte> text, int position)
    {
        private readonly ReadOnlySpan<byte> _text = text;
        private int _budget = 100_000;

        internal int Position = position;

        internal readonly bool AtDictionary => Position + 1 < _text.Length && _text[Position] == '<' && _text[Position + 1] == '<';

        /// <summary>Skips whitespace; false at the end of the text or at a comment.</summary>
        internal bool SkipWhite()
        {
            while (Position < _text.Length && IsWhite(_text[Position]))
            {
                Position++;
            }

            return Position < _text.Length && _text[Position] != '%';
        }

        internal bool ParseValue(int depth, out RawValue value)
        {
            value = OtherValue;
            if (--_budget < 0 || depth > 32 || !SkipWhite())
            {
                return false;
            }

            switch (_text[Position])
            {
                case (byte)'<' when AtDictionary:
                    return ParseDictionary(depth, out value);
                case (byte)'<':
                    return SkipHexString();
                case (byte)'[':
                    return ParseArray(depth, out value);
                case (byte)'(':
                    return SkipLiteralString();
                case (byte)'/':
                    if (!ReadName(out var name))
                    {
                        return false;
                    }

                    value = new RawValue(Name: name);
                    return true;
                case (byte)')' or (byte)'>' or (byte)']' or (byte)'{' or (byte)'}':
                    return false;
            }

            var token = ReadToken();
            if (token.IsEmpty)
            {
                return false;
            }

            if (!TryParseInteger(token, out var number))
            {
                return true; // true, false, null, a real number, or an unknown keyword
            }

            // "N G R" is an indirect reference.
            var save = Position;
            if (SkipWhite() && TryParseInteger(ReadToken(), out _) && SkipWhite() && ReadToken().SequenceEqual("R"u8))
            {
                return true;
            }

            Position = save;
            value = new RawValue(Number: number);
            return true;
        }

        private bool ParseDictionary(int depth, out RawValue value)
        {
            value = OtherValue;
            Position += 2;
            var entries = new Dictionary<string, RawValue>(StringComparer.Ordinal);
            while (true)
            {
                if (!SkipWhite())
                {
                    return false;
                }

                if (_text[Position] == '>')
                {
                    if (Position + 1 >= _text.Length || _text[Position + 1] != '>')
                    {
                        return false;
                    }

                    Position += 2;
                    value = new RawValue(Entries: entries);
                    return true;
                }

                if (_text[Position] != '/' || !ReadName(out var key)
                    || !ParseValue(depth + 1, out var item) || !entries.TryAdd(key, item))
                {
                    return false; // not a key, a broken value, or a duplicate key (readers disagree on which wins)
                }
            }
        }

        private bool ParseArray(int depth, out RawValue value)
        {
            value = OtherValue;
            Position++;
            var items = new List<RawValue>();
            while (true)
            {
                if (!SkipWhite())
                {
                    return false;
                }

                if (_text[Position] == ']')
                {
                    Position++;
                    value = new RawValue(Items: items);
                    return true;
                }

                if (!ParseValue(depth + 1, out var item))
                {
                    return false;
                }

                items.Add(item);
            }
        }

        private bool SkipLiteralString()
        {
            Position++;
            var nesting = 1;
            while (Position < _text.Length)
            {
                var c = _text[Position++];
                if (c == '\\')
                {
                    Position++;
                }
                else if (c == '(')
                {
                    nesting++;
                }
                else if (c == ')' && --nesting == 0)
                {
                    return true;
                }
            }

            return false;
        }

        private bool SkipHexString()
        {
            Position++;
            while (Position < _text.Length)
            {
                var c = _text[Position++];
                if (c == '>')
                {
                    return true;
                }

                if (!IsWhite(c) && HexDigit(c) < 0)
                {
                    return false;
                }
            }

            return false;
        }

        private bool ReadName(out string name)
        {
            name = string.Empty;
            Position++; // the slash
            Span<byte> buffer = stackalloc byte[128];
            var length = 0;
            while (Position < _text.Length && !IsDelimiter(_text[Position]))
            {
                var c = _text[Position++];
                if (c == '#' && Position + 1 < _text.Length && HexDigit(_text[Position]) >= 0 && HexDigit(_text[Position + 1]) >= 0)
                {
                    c = (byte)((HexDigit(_text[Position]) << 4) | HexDigit(_text[Position + 1]));
                    Position += 2;
                }

                if (length == buffer.Length)
                {
                    return false;
                }

                buffer[length++] = c;
            }

            name = System.Text.Encoding.Latin1.GetString(buffer[..length]);
            return true;
        }

        private ReadOnlySpan<byte> ReadToken()
        {
            var start = Position;
            while (Position < _text.Length && !IsDelimiter(_text[Position]) && Position - start < 64)
            {
                Position++;
            }

            return _text[start..Position];
        }

        private static bool TryParseInteger(ReadOnlySpan<byte> token, out long number)
        {
            number = 0;
            var i = token.Length > 0 && token[0] is (byte)'+' or (byte)'-' ? 1 : 0;
            if (i == token.Length || token.Length > 19)
            {
                return false;
            }

            for (; i < token.Length; i++)
            {
                if (token[i] is < (byte)'0' or > (byte)'9')
                {
                    return false;
                }

                number = (number * 10) + (token[i] - '0');
            }

            if (token[0] == '-')
            {
                number = -number;
            }

            return true;
        }
    }

    private static int HexDigit(byte c) => c switch
    {
        >= (byte)'0' and <= (byte)'9' => c - '0',
        >= (byte)'a' and <= (byte)'f' => c - 'a' + 10,
        >= (byte)'A' and <= (byte)'F' => c - 'A' + 10,
        _ => -1,
    };

    // ------------------------------------------------------------------ per-page check

    /// <summary>Per-document state for <see cref="MeasurePage"/>: decoded sizes of streams already measured.</summary>
    internal sealed class DocumentState(PdfDocument document)
    {
        internal PdfDocument Document { get; } = document;

        internal Dictionary<IndirectReference, long?> Sizes { get; } = [];

        internal byte[] Scratch { get; } = new byte[64 * 1024];
    }

    /// <summary>
    /// The page dictionaries in page-tree order, each with its inherited /Resources. At most
    /// <paramref name="maxPages"/> + 1 entries are returned.
    /// </summary>
    internal static List<(DictionaryToken Page, IToken? Resources)> GetPages(PdfDocument document, int maxPages)
    {
        var pages = new List<(DictionaryToken, IToken?)>();
        if (!document.Structure.Catalog.CatalogDictionary.Data.TryGetValue("Pages", out var rootToken)
            || Resolve(document, rootToken) is not DictionaryToken root)
        {
            return pages;
        }

        var visited = new HashSet<IndirectReference>();
        var stack = new Stack<(DictionaryToken Node, IToken? Resources, int Depth)>();
        stack.Push((root, null, 0));
        var nodes = 0;
        while (stack.Count > 0 && pages.Count <= maxPages && ++nodes < MaxWalkNodes)
        {
            var (node, inherited, depth) = stack.Pop();
            var resources = node.Data.TryGetValue("Resources", out var r) ? r : inherited;
            if (node.Data.TryGetValue("Kids", out var kidsToken) && Resolve(document, kidsToken) is ArrayToken kids)
            {
                if (depth > 64)
                {
                    continue;
                }

                for (var i = kids.Data.Count - 1; i >= 0; i--)
                {
                    var kid = kids.Data[i];
                    if (kid is IndirectReferenceToken reference && !visited.Add(reference.Data))
                    {
                        continue;
                    }

                    if (Resolve(document, kid) is DictionaryToken child)
                    {
                        stack.Push((child, resources, depth + 1));
                    }
                }
            }
            else
            {
                pages.Add((node, resources));
            }
        }

        return pages;
    }

    /// <summary>
    /// Decoded bytes PdfPig would process for the page, capped just above <see cref="MaxPageDecodedBytes"/>;
    /// null when a stream cannot be measured (chained expanding filters).
    /// </summary>
    internal static long? MeasurePage(DocumentState state, DictionaryToken page, IToken? resources, CancellationToken cancellationToken)
    {
        var total = 0L;
        var visited = new HashSet<IndirectReference>();
        var nodes = 0;
        var ok = true;

        if (page.Data.TryGetValue("Contents", out var contents))
        {
            Walk(contents, 0, StreamRole.Content);
        }

        if (resources is not null)
        {
            WalkResources(resources, 0);
        }

        return ok ? total : null;

        void WalkResources(IToken token, int depth)
        {
            if (!ok || total > MaxPageDecodedBytes || depth > MaxResourceDepth || Resolve(state.Document, token, visited) is not DictionaryToken dict)
            {
                return;
            }

            if (dict.Data.TryGetValue("XObject", out var xobjects) && Resolve(state.Document, xobjects, visited) is DictionaryToken xdict)
            {
                foreach (var value in xdict.Data.Values)
                {
                    Walk(value, depth, StreamRole.XObject);
                }
            }

            if (dict.Data.TryGetValue("Font", out var fonts) && Resolve(state.Document, fonts, visited) is DictionaryToken fdict)
            {
                foreach (var font in fdict.Data.Values)
                {
                    WalkFont(font, depth);
                }
            }
        }

        void WalkFont(IToken token, int depth)
        {
            if (!ok || total > MaxPageDecodedBytes || depth > MaxResourceDepth || Resolve(state.Document, token, visited) is not DictionaryToken font)
            {
                return;
            }

            foreach (var key in (ReadOnlySpan<string>)["ToUnicode", "Encoding"])
            {
                if (font.Data.TryGetValue(key, out var stream))
                {
                    Walk(stream, depth, StreamRole.FontData);
                }
            }

            if (font.Data.TryGetValue("FontDescriptor", out var descriptorToken)
                && Resolve(state.Document, descriptorToken, visited) is DictionaryToken descriptor)
            {
                foreach (var key in (ReadOnlySpan<string>)["FontFile", "FontFile2", "FontFile3"])
                {
                    if (descriptor.Data.TryGetValue(key, out var file))
                    {
                        Walk(file, depth, StreamRole.FontData);
                    }
                }
            }

            if (font.Data.TryGetValue("DescendantFonts", out var descendants) && Resolve(state.Document, descendants, visited) is ArrayToken array)
            {
                foreach (var descendant in array.Data)
                {
                    WalkFont(descendant, depth + 1);
                }
            }

            if (font.Data.TryGetValue("CharProcs", out var procs) && Resolve(state.Document, procs, visited) is DictionaryToken procDict)
            {
                foreach (var proc in procDict.Data.Values)
                {
                    Walk(proc, depth + 1, StreamRole.Content);
                }
            }

            if (font.Data.TryGetValue("Resources", out var fontResources))
            {
                WalkResources(fontResources, depth + 1);
            }
        }

        // Streams referenced from the page: measure, and for form XObjects descend into their resources.
        void Walk(IToken token, int depth, StreamRole role)
        {
            if (!ok || total > MaxPageDecodedBytes || ++nodes > MaxWalkNodes)
            {
                ok = ok && nodes <= MaxWalkNodes;
                return;
            }

            cancellationToken.ThrowIfCancellationRequested();
            var reference = token is IndirectReferenceToken r ? r.Data : (IndirectReference?)null;
            if (reference is { } seen && !visited.Add(seen))
            {
                return; // already counted for this page
            }

            var resolved = Resolve(state.Document, token);
            switch (resolved)
            {
                case ArrayToken array:
                    foreach (var item in array.Data)
                    {
                        Walk(item, depth, role);
                    }

                    break;
                case StreamToken stream:
                    var dict = stream.StreamDictionary;
                    // PdfPig dispatches XObjects by /Subtype and never decodes image data for text; page contents
                    // and glyph procedures are decoded whatever their dictionary claims.
                    if (role == StreamRole.XObject
                        && dict.Data.TryGetValue("Subtype", out var subtype) && subtype is NameToken { Data: "Image" })
                    {
                        return;
                    }

                    long? size;
                    if (reference is { } key && state.Sizes.TryGetValue(key, out var cached))
                    {
                        size = cached;
                    }
                    else
                    {
                        size = Measure(stream, state.Scratch);
                        if (reference is { } k)
                        {
                            state.Sizes[k] = size;
                        }
                    }

                    if (size is null)
                    {
                        ok = false;
                        return;
                    }

                    total += size.Value;
                    if (role != StreamRole.FontData && dict.Data.TryGetValue("Resources", out var formResources))
                    {
                        WalkResources(formResources, depth + 1);
                    }

                    break;
            }
        }
    }

    private enum StreamRole
    {
        Content,
        XObject,
        FontData,
    }

    private static long? Measure(StreamToken stream, byte[] scratch)
    {
        var filters = new List<string>();
        var earlyChange = 1;
        if (stream.StreamDictionary.Data.TryGetValue("Filter", out var filter))
        {
            switch (filter)
            {
                case NameToken name:
                    filters.Add(name.Data);
                    break;
                case ArrayToken array:
                    foreach (var item in array.Data)
                    {
                        if (item is not NameToken n)
                        {
                            return null;
                        }

                        filters.Add(n.Data);
                    }

                    break;
                default:
                    return null; // indirect filter values: PdfPig resolves them, we cannot cheaply
            }
        }

        if (stream.StreamDictionary.Data.TryGetValue("DecodeParms", out var parms)
            && parms is DictionaryToken p && p.Data.TryGetValue("EarlyChange", out var ec) && ec is NumericToken { Int: 0 })
        {
            earlyChange = 0;
        }

        var data = stream.Data;
        if (filters.Count == 0)
        {
            return data.Length;
        }

        if (!MemoryMarshal.TryGetArray<byte>(data, out var segment) || segment.Array is null)
        {
            segment = new ArraySegment<byte>(data.ToArray());
        }

        return MeasureDecoded(segment.Array!, segment.Offset, segment.Count, filters, earlyChange, MaxPageDecodedBytes, scratch);
    }

    private static IToken? Resolve(PdfDocument document, IToken? token, HashSet<IndirectReference>? visited = null)
    {
        for (var hops = 0; token is IndirectReferenceToken reference && hops < 8; hops++)
        {
            if (visited is not null && !visited.Add(reference.Data))
            {
                return null;
            }

            try
            {
                token = document.Structure.GetObject(reference.Data)?.Data;
            }
            catch (Exception ex) when (ex is not OperationCanceledException and not OutOfMemoryException)
            {
                return null;
            }
        }

        return token is IndirectReferenceToken ? null : token;
    }

    // ------------------------------------------------------------------ decoded-size measurement

    private enum FilterKind
    {
        Unknown,
        Flate,
        Lzw,
        RunLength,
        AsciiHex,
        Ascii85,
        NonExpanding, // image codecs and Crypt: no expansion of interest here
    }


    private static FilterKind Classify(string name) => name switch
    {
        "FlateDecode" or "Fl" => FilterKind.Flate,
        "LZWDecode" or "LZW" => FilterKind.Lzw,
        "RunLengthDecode" or "RL" => FilterKind.RunLength,
        "ASCIIHexDecode" or "AHx" => FilterKind.AsciiHex,
        "ASCII85Decode" or "A85" => FilterKind.Ascii85,
        "DCTDecode" or "DCT" or "JPXDecode" or "JBIG2Decode" or "CCITTFaxDecode" or "CCF" or "Crypt" => FilterKind.NonExpanding,
        _ => FilterKind.Unknown, // PdfPig cannot decode it either
    };

    private static bool IsExpanding(FilterKind kind) => kind is FilterKind.Flate or FilterKind.Lzw or FilterKind.RunLength;

    /// <summary>
    /// Decoded size of <paramref name="count"/> bytes run through <paramref name="filters"/>, reported as soon as
    /// it exceeds <paramref name="cap"/>; null when the chain has two expanding filters (never legitimate, and not
    /// measurable without materializing the intermediate output). Filters PdfPig does not know cannot be decoded
    /// by it either, so they count as non-expanding.
    /// </summary>
    internal static long? MeasureDecoded(byte[] data, int offset, int count, IReadOnlyList<string> filters, int earlyChange, long cap, byte[] scratch)
    {
        var kinds = filters.Select(Classify).ToArray();
        if (kinds.Count(IsExpanding) > 1)
        {
            return null;
        }

        long size = count;
        foreach (var kind in kinds)
        {
            switch (kind)
            {
                case FilterKind.AsciiHex or FilterKind.Ascii85:
                    // Shrinking text encodings: decode (bounded by the input size) and keep measuring.
                    data = kind == FilterKind.AsciiHex ? DecodeAsciiHex(data.AsSpan(offset, count)) : DecodeAscii85(data.AsSpan(offset, count));
                    offset = 0;
                    count = data.Length;
                    size = count;
                    break;
                case FilterKind.Flate:
                    return InflatedSize(data, offset, count, cap, scratch);
                case FilterKind.Lzw:
                    return LzwDecodedSize(data.AsSpan(offset, count), earlyChange, cap);
                case FilterKind.RunLength:
                    return RunLengthDecodedSize(data.AsSpan(offset, count), cap);
            }
        }

        return size;
    }

    /// <summary>Inflates (zlib, or raw deflate when there is no zlib header) into a scratch buffer, counting.</summary>
    internal static long InflatedSize(byte[] data, int offset, int count, long cap, byte[] scratch)
    {
        if (count < 2)
        {
            return 0;
        }

        var zlib = (data[offset] & 0x0F) == 8 && ((data[offset] << 8) | data[offset + 1]) % 31 == 0;
        using var input = new MemoryStream(data, offset, count, writable: false);
        using Stream inflater = zlib
            ? new ZLibStream(input, CompressionMode.Decompress)
            : new DeflateStream(input, CompressionMode.Decompress);
        long total = 0;
        try
        {
            int n;
            while ((n = inflater.Read(scratch)) > 0)
            {
                total += n;
                if (total > cap)
                {
                    return total;
                }
            }
        }
        catch (InvalidDataException)
        {
            // Damaged or truncated data: PdfPig gets no further than this either.
        }

        return total;
    }

    /// <summary>Output length of PDF LZW data, computed from code-table string lengths without decoding.</summary>
    internal static long LzwDecodedSize(ReadOnlySpan<byte> data, int earlyChange, long cap)
    {
        const int clear = 256;
        const int end = 257;
        Span<int> lengths = stackalloc int[4096];
        for (var i = 0; i < 256; i++)
        {
            lengths[i] = 1;
        }

        var tableSize = 258;
        var codeLength = 9;
        var previous = -1;
        long total = 0;
        long bitBuffer = 0;
        var bits = 0;
        var position = 0;
        while (true)
        {
            while (bits < codeLength && position < data.Length)
            {
                bitBuffer = (bitBuffer << 8) | data[position++];
                bits += 8;
            }

            if (bits < codeLength)
            {
                return total;
            }

            var code = (int)((bitBuffer >> (bits - codeLength)) & ((1 << codeLength) - 1));
            bits -= codeLength;
            if (code == clear)
            {
                tableSize = 258;
                codeLength = 9;
                previous = -1;
                continue;
            }

            if (code == end)
            {
                return total;
            }

            int entryLength;
            if (previous < 0)
            {
                if (code > 255)
                {
                    return total;
                }

                entryLength = 1;
            }
            else if (code < tableSize)
            {
                entryLength = lengths[code];
                if (tableSize < 4096)
                {
                    lengths[tableSize++] = lengths[previous] + 1;
                }
            }
            else if (code == tableSize && tableSize < 4096)
            {
                entryLength = lengths[previous] + 1;
                lengths[tableSize++] = entryLength;
            }
            else
            {
                return total; // corrupt
            }

            total += entryLength;
            if (total > cap)
            {
                return total;
            }

            previous = code;
            if (tableSize + earlyChange >= 1 << codeLength && codeLength < 12)
            {
                codeLength++;
            }
        }
    }

    internal static long RunLengthDecodedSize(ReadOnlySpan<byte> data, long cap)
    {
        long total = 0;
        var i = 0;
        while (i < data.Length && total <= cap)
        {
            var length = data[i];
            if (length == 128)
            {
                break;
            }

            if (length < 128)
            {
                total += length + 1;
                i += length + 2;
            }
            else
            {
                total += 257 - length;
                i += 2;
            }
        }

        return total;
    }

    private static byte[] DecodeAsciiHex(ReadOnlySpan<byte> data)
    {
        var output = new List<byte>(data.Length / 2);
        var high = -1;
        foreach (var c in data)
        {
            if (c == '>')
            {
                break;
            }

            var v = HexDigit(c);
            if (v < 0)
            {
                continue;
            }

            if (high < 0)
            {
                high = v;
            }
            else
            {
                output.Add((byte)((high << 4) | v));
                high = -1;
            }
        }

        if (high >= 0)
        {
            output.Add((byte)(high << 4));
        }

        return [.. output];
    }

    private static byte[] DecodeAscii85(ReadOnlySpan<byte> data)
    {
        var output = new List<byte>(data.Length);
        Span<int> group = stackalloc int[5];
        var n = 0;
        for (var i = 0; i < data.Length; i++)
        {
            var c = data[i];
            if (c == '~')
            {
                break;
            }

            if (c == 'z' && n == 0)
            {
                output.AddRange([0, 0, 0, 0]);
                continue;
            }

            if (c < '!' || c > 'u')
            {
                continue;
            }

            group[n++] = c - '!';
            if (n == 5)
            {
                Emit(group, 4, output);
                n = 0;
            }
        }

        if (n > 1)
        {
            for (var i = n; i < 5; i++)
            {
                group[i] = 84;
            }

            Emit(group, n - 1, output);
        }

        return [.. output];

        static void Emit(Span<int> group, int bytes, List<byte> output)
        {
            uint value = 0;
            foreach (var digit in group)
            {
                value = unchecked((value * 85) + (uint)digit);
            }

            for (var i = 0; i < bytes; i++)
            {
                output.Add((byte)(value >> (24 - (8 * i))));
            }
        }
    }
}
