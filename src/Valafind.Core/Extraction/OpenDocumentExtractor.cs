using System.IO.Compression;
using System.Xml;
using Valafind.Core.Abstractions;

namespace Valafind.Core.Extraction;

/// <summary>
/// Extracts text from OpenDocument files (.odt/.ods/.odp and their templates) by streaming
/// <c>content.xml</c> out of the zip package. Paragraphs and headings become lines; spreadsheet cells are
/// tab-separated with one line per row; slides/sheets are separated by blank lines.
/// </summary>
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

        try
        {
            using var stream = ExtractionIO.OpenRead(path);
            using var zip = new ZipArchive(stream, ZipArchiveMode.Read);
            var entry = zip.GetEntry("content.xml");
            if (entry is null)
            {
                return Task.FromResult<ExtractedText?>(null);
            }

            using var content = entry.Open();
            var sink = new TextAccumulator(limits.MaxChars);
            ReadContent(content, sink, cancellationToken);
            return Task.FromResult<ExtractedText?>(new ExtractedText(sink.ToString(), kind));
        }
        catch (InvalidDataException)
        {
            return Task.FromResult<ExtractedText?>(null);
        }
        catch (XmlException)
        {
            return Task.FromResult<ExtractedText?>(null);
        }
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

    internal static XmlReaderSettings SafeXmlSettings { get; } = new()
    {
        DtdProcessing = DtdProcessing.Ignore,
        XmlResolver = null,
        IgnoreComments = true,
        IgnoreProcessingInstructions = true,
        CheckCharacters = false,
        CloseInput = false,
    };

    private static void ReadContent(Stream content, TextAccumulator sink, CancellationToken cancellationToken)
    {
        using var reader = XmlReader.Create(content, SafeXmlSettings);
        var cellDepth = 0;      // >0 while inside table:table-cell
        var cellHasText = false;
        var rowHasText = false;
        var nodes = 0;
        var advance = true;

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
                    var value = reader.Value;
                    if (cellDepth > 0)
                    {
                        if (string.IsNullOrWhiteSpace(value))
                        {
                            break;
                        }

                        if (!cellHasText)
                        {
                            if (rowHasText)
                            {
                                sink.Append('\t');
                            }

                            cellHasText = true;
                            rowHasText = true;
                        }
                    }

                    sink.Append(value);
                    break;
                }
            }
        }
    }
}
