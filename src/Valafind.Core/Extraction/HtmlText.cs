using System.Net;

namespace Valafind.Core.Extraction;

/// <summary>
/// Linear-time, allocation-light HTML/XHTML to plain-text conversion. Not a full HTML parser: it drops
/// comments, scripts and styles, turns block-level elements into line breaks, decodes entities and collapses
/// whitespace. Malformed markup degrades gracefully (unterminated constructs consume the rest of the input).
/// </summary>
internal static class HtmlText
{
    private static readonly HashSet<string> SkipContentTags = new(StringComparer.OrdinalIgnoreCase)
    {
        "script", "style", "noscript", "template", "svg", "math", "canvas", "iframe", "object",
    };

    private static readonly HashSet<string> BlockTags = new(StringComparer.OrdinalIgnoreCase)
    {
        "p", "div", "br", "li", "ul", "ol", "dl", "dt", "dd", "h1", "h2", "h3", "h4", "h5", "h6", "tr", "table",
        "thead", "tbody", "tfoot", "section", "article", "header", "footer", "nav", "aside", "blockquote", "pre",
        "hr", "title", "form", "fieldset", "figure", "figcaption", "main", "address", "caption", "details",
        "summary", "body", "html", "head", "option", "legend",
    };

    private static readonly HashSet<string> CellTags = new(StringComparer.OrdinalIgnoreCase) { "td", "th" };

    private static readonly HashSet<string>.AlternateLookup<ReadOnlySpan<char>> SkipLookup =
        SkipContentTags.GetAlternateLookup<ReadOnlySpan<char>>();

    private static readonly HashSet<string>.AlternateLookup<ReadOnlySpan<char>> BlockLookup =
        BlockTags.GetAlternateLookup<ReadOnlySpan<char>>();

    private static readonly HashSet<string>.AlternateLookup<ReadOnlySpan<char>> CellLookup =
        CellTags.GetAlternateLookup<ReadOnlySpan<char>>();

    internal static string ToText(string html, int maxChars)
    {
        var sink = new TextAccumulator(maxChars);
        AppendText(html, sink);
        return sink.ToString();
    }

    internal static void AppendText(ReadOnlySpan<char> html, TextAccumulator sink)
    {
        var i = 0;
        var textStart = 0;
        while (i < html.Length && !sink.IsFull)
        {
            if (html[i] != '<')
            {
                i++;
                continue;
            }

            AppendSegment(html[textStart..i], sink);
            var rest = html[i..];

            if (rest.StartsWith("<!--", StringComparison.Ordinal))
            {
                i = SkipPast(html, i + 4, "-->");
                textStart = i;
                continue;
            }

            if (rest.StartsWith("<![CDATA[", StringComparison.Ordinal))
            {
                var end = html[(i + 9)..].IndexOf("]]>", StringComparison.Ordinal);
                var content = end < 0 ? html[(i + 9)..] : html.Slice(i + 9, end);
                AppendSegment(content, sink, decode: false);
                i = end < 0 ? html.Length : i + 9 + end + 3;
                textStart = i;
                continue;
            }

            if (rest.Length > 1 && rest[1] is '!' or '?')
            {
                i = SkipPast(html, i + 2, ">");
                textStart = i;
                continue;
            }

            var j = i + 1;
            var closing = j < html.Length && html[j] == '/';
            if (closing)
            {
                j++;
            }

            var nameStart = j;
            while (j < html.Length && (char.IsAsciiLetterOrDigit(html[j]) || html[j] is '-' or ':' or '_'))
            {
                j++;
            }

            if (j == nameStart || !char.IsAsciiLetter(html[nameStart]))
            {
                // A literal '<' (e.g. "a < b"); keep it as text.
                textStart = i;
                i++;
                continue;
            }

            var name = html[nameStart..j];
            var colon = name.LastIndexOf(':');
            if (colon >= 0)
            {
                name = name[(colon + 1)..]; // xhtml:p → p
            }

            var tagEnd = FindTagEnd(html, j);
            var selfClosing = tagEnd > 0 && tagEnd < html.Length && html[tagEnd - 1] == '/';
            i = tagEnd >= html.Length ? html.Length : tagEnd + 1;
            textStart = i;

            if (!closing && !selfClosing && SkipLookup.Contains(name))
            {
                i = SkipElementContent(html, i, name);
                textStart = i;
                AppendSpace(sink);
                continue;
            }

            if (BlockLookup.Contains(name))
            {
                sink.EnsureLineBreak();
            }
            else if (CellLookup.Contains(name))
            {
                AppendSpace(sink);
            }
        }

        if (textStart < html.Length)
        {
            AppendSegment(html[textStart..], sink);
        }
    }

    private static int SkipPast(ReadOnlySpan<char> html, int from, string terminator)
    {
        if (from >= html.Length)
        {
            return html.Length;
        }

        var idx = html[from..].IndexOf(terminator, StringComparison.Ordinal);
        return idx < 0 ? html.Length : from + idx + terminator.Length;
    }

    /// <summary>Returns the index of the '&gt;' closing a tag, honouring quoted attribute values.</summary>
    private static int FindTagEnd(ReadOnlySpan<char> html, int from)
    {
        char quote = '\0';
        for (var k = from; k < html.Length; k++)
        {
            var c = html[k];
            if (quote != '\0')
            {
                if (c == quote)
                {
                    quote = '\0';
                }
            }
            else if (c is '"' or '\'')
            {
                quote = c;
            }
            else if (c == '>')
            {
                return k;
            }
            else if (c == '<')
            {
                // Broken tag such as "<a href=x <b>": end it before the next one.
                return k - 1;
            }
        }

        return html.Length;
    }

    private static int SkipElementContent(ReadOnlySpan<char> html, int from, ReadOnlySpan<char> name)
    {
        var k = from;
        while (k < html.Length)
        {
            var idx = html[k..].IndexOf("</", StringComparison.Ordinal);
            if (idx < 0)
            {
                return html.Length;
            }

            var at = k + idx + 2;
            if (html.Length - at >= name.Length
                && html.Slice(at, name.Length).Equals(name, StringComparison.OrdinalIgnoreCase)
                && (at + name.Length == html.Length || !char.IsAsciiLetterOrDigit(html[at + name.Length])))
            {
                return SkipPast(html, at + name.Length, ">");
            }

            k = at;
        }

        return html.Length;
    }

    private static void AppendSpace(TextAccumulator sink)
    {
        if (!sink.EndsWithWhitespace)
        {
            sink.Append(' ');
        }
    }

    private static void AppendSegment(ReadOnlySpan<char> segment, TextAccumulator sink, bool decode = true)
    {
        if (segment.IsEmpty)
        {
            return;
        }

        ReadOnlySpan<char> text = segment;
        if (decode && segment.Contains('&'))
        {
            text = WebUtility.HtmlDecode(segment.ToString());
        }

        AppendCollapsed(text, sink);
    }

    /// <summary>Appends <paramref name="text"/> with every whitespace run collapsed to one space.</summary>
    internal static void AppendCollapsed(ReadOnlySpan<char> text, TextAccumulator sink)
    {
        var start = 0;
        var i = 0;
        while (i < text.Length && !sink.IsFull)
        {
            if (!char.IsWhiteSpace(text[i]))
            {
                i++;
                continue;
            }

            sink.Append(text[start..i]);
            AppendSpace(sink);
            while (i < text.Length && char.IsWhiteSpace(text[i]))
            {
                i++;
            }

            start = i;
        }

        if (start < text.Length)
        {
            sink.Append(text[start..]);
        }
    }
}
