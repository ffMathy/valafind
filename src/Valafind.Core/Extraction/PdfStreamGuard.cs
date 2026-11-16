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
/// view), parses its /Filter chain and measures it. Any non-image stream that decodes to more than
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
        while (position < file.Length)
        {
            var found = file.AsSpan(position).IndexOf("stream"u8);
            if (found < 0)
            {
                break;
            }

            var keyword = position + found;
            position = keyword + 6;
            if (!TryGetStreamStart(file, keyword, out var dataStart, out var dict))
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

            var filters = ParseFilters(dict);
            if (filters is null || filters.Count == 0)
            {
                continue; // no filter (no expansion) or an indirect /Filter (checked per page)
            }

            if (HasName(dict, "Subtype", "Image") && !HasKey(dict, "First") && !HasKey(dict, "W"))
            {
                continue; // image data is never decoded for text extraction
            }

            var size = MeasureDecoded(file, dataStart, file.Length - dataStart, filters, EarlyChange(dict), MaxStreamBytes, scratch);
            if (size is null)
            {
                return "stream with chained expanding filters";
            }

            if (size > MaxStreamBytes)
            {
                return $"stream decodes to more than {MaxStreamBytes} bytes";
            }

            total += size.Value;
            if (total > MaxFileDecodedBytes)
            {
                return $"streams decode to more than {MaxFileDecodedBytes} bytes";
            }

            position = dataStart;
        }

        return null;
    }

    /// <summary>Checks that "stream" at <paramref name="keyword"/> is a stream keyword (after "&gt;&gt;", before
    /// an end-of-line) and finds its dictionary text.</summary>
    private static bool TryGetStreamStart(byte[] file, int keyword, out int dataStart, out ReadOnlySpan<byte> dict)
    {
        dataStart = 0;
        dict = default;
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

        var windowStart = Math.Max(0, b - 64 * 1024);
        var window = file.AsSpan(windowStart, b + 1 - windowStart);
        var obj = window.LastIndexOf("obj"u8);
        dict = obj >= 0 ? window[obj..] : window;
        dataStart = j;
        return true;
    }

    private static bool IsWhite(byte c) => c is (byte)' ' or (byte)'\r' or (byte)'\n' or (byte)'\t' or (byte)'\f' or 0;

    private static bool IsDelimiter(byte c) => IsWhite(c) || c is (byte)'/' or (byte)'[' or (byte)']' or (byte)'<' or (byte)'>' or (byte)'(' or (byte)')';

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

    private static bool HasKey(ReadOnlySpan<byte> dict, string key) => FindName(dict, key) >= 0;

    private static bool HasName(ReadOnlySpan<byte> dict, string key, string value)
    {
        var at = FindName(dict, key);
        if (at < 0)
        {
            return false;
        }

        while (at < dict.Length && IsWhite(dict[at]))
        {
            at++;
        }

        return FindName(dict[at..], value) == value.Length + 1;
    }

    private static int EarlyChange(ReadOnlySpan<byte> dict)
    {
        var at = FindName(dict, "EarlyChange");
        if (at < 0)
        {
            return 1;
        }

        while (at < dict.Length && IsWhite(dict[at]))
        {
            at++;
        }

        return at < dict.Length && dict[at] == '0' ? 0 : 1;
    }

    /// <summary>The /Filter names in order; empty for none; null when the value is not a name or array of names.</summary>
    private static List<string>? ParseFilters(ReadOnlySpan<byte> dict)
    {
        var at = FindName(dict, "Filter");
        if (at < 0)
        {
            return [];
        }

        while (at < dict.Length && IsWhite(dict[at]))
        {
            at++;
        }

        if (at >= dict.Length)
        {
            return null;
        }

        var result = new List<string>();
        if (dict[at] == '/')
        {
            result.Add(ReadName(dict, at));
            return result;
        }

        if (dict[at] != '[')
        {
            return null;
        }

        at++;
        while (at < dict.Length && dict[at] != ']' && result.Count < 16)
        {
            if (dict[at] == '/')
            {
                var name = ReadName(dict, at);
                result.Add(name);
                at += name.Length + 1;
            }
            else if (IsWhite(dict[at]))
            {
                at++;
            }
            else
            {
                return null; // indirect reference or junk inside the array
            }
        }

        return result;
    }

    private static string ReadName(ReadOnlySpan<byte> text, int slash)
    {
        var end = slash + 1;
        while (end < text.Length && !IsDelimiter(text[end]) && end - slash < 64)
        {
            end++;
        }

        return System.Text.Encoding.ASCII.GetString(text[(slash + 1)..end]);
    }

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

            var v = HexValue(c);
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

        static int HexValue(byte c) => c switch
        {
            >= (byte)'0' and <= (byte)'9' => c - '0',
            >= (byte)'a' and <= (byte)'f' => c - 'a' + 10,
            >= (byte)'A' and <= (byte)'F' => c - 'A' + 10,
            _ => -1,
        };
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
