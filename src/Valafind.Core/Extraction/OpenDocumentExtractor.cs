using System.IO.Compression;
using System.Xml;
using Valafind.Core.Abstractions;

namespace Valafind.Core.Extraction;

/// <summary>
/// Extracts text from OpenDocument files (.odt/.ods/.odp and their templates) by streaming
/// <c>content.xml</c> out of the zip package. Paragraphs and headings become lines; spreadsheet cells are
/// tab-separated with one line per row; slides/sheets are separated by blank lines.
/// </summary>
/// <remarks>
/// Bounded for hostile files: <c>content.xml</c> is opened through <see cref="SafeZip"/> (entry count,
/// decompressed-size and compression-ratio limits) and text nodes are read in chunks that stop at the character
/// budget, so a single multi-gigabyte text node is never materialized.
/// </remarks>
public sealed class OpenDocumentExtractor : ITextExtractor
{
    private const string TextNs = "urn:oasis:names:tc:opendocument:xmlns:text:1.0";
    private const string TableNs = "urn:oasis:names:tc:opendocument:xmlns:table:1.0";
    private const string DrawNs = "urn:oasis:names:tc:opendocument:xmlns:drawing:1.0";
    private const string OfficeNs = "urn:oasis:names:tc:opendocument:xmlns:office:1.0";

    /// <inheritdoc />
    public bool CanHandle(string path) => GetKind(path) is not null;

    /// <inheritdoc />
    public Task<ExtractedText?> ExtractAsync(string path, ExtractionLimits limits, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(limits);
        cancellationToken.ThrowIfCancellationRequested();
        if (GetKind(path) is not { } kind)
        {
            return Task.FromResult<ExtractedText?>(null);
        }

        using var stream = ExtractionIO.OpenRead(path);
        var sink = new TextAccumulator(limits.MaxChars);
        try
        {
            using var zip = SafeZip.Open(stream);
            var entry = zip.GetEntry("content.xml");
            if (entry is null)
            {
                return Task.FromResult<ExtractedText?>(null);
            }

            using var content = SafeZip.OpenEntry(entry, SafeZip.NewFileBudget());
            ReadContent(content, sink, cancellationToken);
        }
        catch (InvalidDataException)
        {
            return Task.FromResult<ExtractedText?>(null);
        }
        catch (XmlException) when (sink.Length == 0)
        {
            return Task.FromResult<ExtractedText?>(null);
        }
        catch (XmlException)
        {
            // Malformed tail: keep the text before the error.
        }
        catch (ExtractionLimitException)
        {
            // Decompression bomb or oversized part: keep what was read within the limits.
        }

        return Task.FromResult<ExtractedText?>(new ExtractedText(sink.ToString(), kind));
    }

    private static FileKind? GetKind(string path)
    {
        if (ExtractionIO.HasExtension(path, ".odt", ".ott"))
        {
            return FileKind.Document;
        }

        if (ExtractionIO.HasExtension(path, ".ods", ".ots"))
        {
            return FileKind.Spreadsheet;
        }

        if (ExtractionIO.HasExtension(path, ".odp", ".otp"))
        {
            return FileKind.Presentation;
        }

        return null;
    }

    /// <summary>Settings for untrusted XML (see <see cref="BoundedXml.Settings"/>).</summary>
    internal static XmlReaderSettings SafeXmlSettings => BoundedXml.Settings;

    private static void ReadContent(Stream content, TextAccumulator sink, CancellationToken cancellationToken)
    {
        using var reader = XmlReader.Create(content, SafeXmlSettings);
        var cellDepth = 0;      // >0 while inside table:table-cell
        var cellHasText = false;
        var rowHasText = false;
        var nodes = 0;
        var advance = true;
        var buffer = new char[BoundedXml.ChunkSize];

        while (!sink.IsFull && (advance ? reader.Read() : !reader.EOF))
        {
            advance = true;
            if ((++nodes & 0xFFF) == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            switch (reader.NodeType)
            {
                case XmlNodeType.Element:
                {
                    var ns = reader.NamespaceURI;
                    var name = reader.LocalName;
                    if (ns == TextNs)
                    {
                        switch (name)
                        {
                            case "p" or "h":
                                if (cellDepth == 0)
                                {
                                    sink.EnsureLineBreak();
                                }
                                else if (cellHasText)
                                {
                                    sink.Append(' ');
                                }

                                break;
                            case "s":
                                var count = int.TryParse(reader.GetAttribute("c", TextNs), out var c) ? Math.Clamp(c, 1, 64) : 1;
                                sink.Append(new string(' ', count));
                                break;
                            case "tab":
                                sink.Append('\t');
                                break;
                            case "line-break":
                                sink.Append('\n');
                                break;
                            case "tracked-changes" or "sequence-decls" or "variable-decls" or "user-field-decls"
                                or "note-citation":
                                // Metadata / deleted text / footnote numbers: not content. Skip() leaves the
                                // reader on the following node, which must be processed without Read().
                                reader.Skip();
                                advance = false;
                                break;
                        }
                    }
                    else if (ns == TableNs)
                    {
                        switch (name)
                        {
                            case "table":
                                sink.EnsureBlankLine();
                                if (reader.GetAttribute("name", TableNs) is { Length: > 0 } tableName)
                                {
                                    sink.Append(tableName);
                                    sink.Append('\n');
                                }

                                break;
                            case "table-row":
                                rowHasText = false;
                                break;
                            case "table-cell" or "covered-table-cell" when !reader.IsEmptyElement:
                                cellDepth++;
                                cellHasText = false;
                                break;
                        }
                    }
                    else if (ns == DrawNs && name == "page")
                    {
                        sink.EnsureBlankLine();
                    }
                    else if (ns == OfficeNs && name == "annotation")
                    {
                        sink.EnsureLineBreak();
                    }

                    break;
                }

                case XmlNodeType.EndElement:
                {
                    var ns = reader.NamespaceURI;
                    var name = reader.LocalName;
                    if (ns == TextNs && name is "p" or "h" && cellDepth == 0)
                    {
                        sink.EnsureLineBreak();
                    }
                    else if (ns == TableNs)
                    {
                        if (name is "table-cell" or "covered-table-cell" && cellDepth > 0)
                        {
                            cellDepth--;
                        }
                        else if (name == "table-row" && rowHasText)
                        {
                            sink.EnsureLineBreak();
                        }
                    }

                    break;
                }

                case XmlNodeType.Text or XmlNodeType.SignificantWhitespace or XmlNodeType.CDATA:
                {
                    // Read in bounded chunks: a single text node can be hundreds of megabytes in a hostile file.
                    if (cellDepth == 0 || cellHasText)
                    {
                        BoundedXml.AppendValue(reader, sink, buffer);
                        break;
                    }

                    // First text of a table cell: skip leading whitespace; whitespace-only text is not a value.
                    if (!reader.CanReadValueChunk)
                    {
                        AppendCellText(reader.Value.AsSpan(), sink, ref cellHasText, ref rowHasText);
                        break;
                    }

                    int n;
                    while (!sink.IsFull && (n = reader.ReadValueChunk(buffer, 0, buffer.Length)) > 0)
                    {
                        AppendCellText(buffer.AsSpan(0, n), sink, ref cellHasText, ref rowHasText);
                    }

                    break;
                }
            }
        }
    }

    private static void AppendCellText(ReadOnlySpan<char> chunk, TextAccumulator sink, ref bool cellHasText, ref bool rowHasText)
    {
        if (!cellHasText)
        {
            chunk = chunk.TrimStart();
            if (chunk.IsEmpty)
            {
                return;
            }

            if (rowHasText)
            {
                sink.Append('\t');
            }

            cellHasText = true;
            rowHasText = true;
        }

        sink.Append(chunk);
    }
}
