using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;
using Valafind.Core.Abstractions;
using P = DocumentFormat.OpenXml.Presentation;

namespace Valafind.Core.Extraction;

/// <summary>
/// Extracts text from Office Open XML files: Word (.docx/.docm/.dotx: body incl. tables, then footnotes),
/// PowerPoint (.pptx/.pptm: slide text in slide order, then speaker notes) and Excel (.xlsx/.xlsm: cell
/// values by sheet with the sheet name as a heading). Parts are read with the streaming
/// <see cref="OpenXmlReader"/> so huge or zip-bomb-like documents stop at the character budget.
/// </summary>
public sealed class OpenXmlExtractor : ITextExtractor
{
    /// <summary>Maximum rows read per worksheet; later rows are usually repetitive data.</summary>
    internal const int MaxRowsPerSheet = 5_000;

    /// <inheritdoc />
    public bool CanHandle(string path) => GetKind(path) is not null;

    /// <inheritdoc />
    public Task<ExtractedText?> ExtractAsync(string path, ExtractionLimits limits, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(limits);
        cancellationToken.ThrowIfCancellationRequested();
        var kind = GetKind(path);
        if (kind is null)
        {
            return Task.FromResult<ExtractedText?>(null);
        }

        using var stream = ExtractionIO.OpenRead(path);
        var sink = new TextAccumulator(limits.MaxChars);
        try
        {
            switch (kind.Value)
            {
                case FileKind.Document:
                    ExtractWord(stream, sink, cancellationToken);
                    break;
                case FileKind.Presentation:
                    ExtractPresentation(stream, sink, cancellationToken);
                    break;
                default:
                    ExtractSpreadsheet(stream, sink, limits.MaxChars, cancellationToken);
                    break;
            }
        }
        catch (OpenXmlPackageException)
        {
            return Task.FromResult<ExtractedText?>(null);
        }
        catch (InvalidDataException)
        {
            return Task.FromResult<ExtractedText?>(null); // not a zip / damaged archive
        }

        return Task.FromResult<ExtractedText?>(new ExtractedText(sink.ToString(), kind.Value));
    }

    private static FileKind? GetKind(string path)
    {
        if (ExtractionIO.HasExtension(path, ".docx", ".docm", ".dotx", ".dotm"))
        {
            return FileKind.Document;
        }

        if (ExtractionIO.HasExtension(path, ".pptx", ".pptm", ".potx", ".ppsx"))
        {
            return FileKind.Presentation;
        }

        if (ExtractionIO.HasExtension(path, ".xlsx", ".xlsm", ".xltx"))
        {
            return FileKind.Spreadsheet;
        }

        return null;
    }

    // ------------------------------------------------------------------ Word

    private static void ExtractWord(Stream stream, TextAccumulator sink, CancellationToken cancellationToken)
    {
        using var document = WordprocessingDocument.Open(stream, false);
        var main = document.MainDocumentPart;
        if (main is null)
        {
            return;
        }

        AppendWordprocessingPart(main, sink, cancellationToken);

        if (main.FootnotesPart is { } footnotes && !sink.IsFull)
        {
            sink.EnsureBlankLine();
            AppendWordprocessingPart(footnotes, sink, cancellationToken);
        }

        if (main.EndnotesPart is { } endnotes && !sink.IsFull)
        {
            sink.EnsureBlankLine();
            AppendWordprocessingPart(endnotes, sink, cancellationToken);
        }
    }

    private static void AppendWordprocessingPart(OpenXmlPart part, TextAccumulator sink, CancellationToken cancellationToken)
    {
        using var reader = OpenXmlReader.Create(part);
        var count = 0;
        while (!sink.IsFull && reader.Read())
        {
            if ((++count & 0xFFF) == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            switch (reader.LocalName)
            {
                case "t" when reader.IsStartElement:
                    sink.Append(reader.GetText());
                    break;
                case "tab" when reader.IsStartElement && IsInRun(reader):
                    sink.Append('\t');
                    break;
                case "br" or "cr" when reader.IsStartElement:
                    sink.Append('\n');
                    break;
                case "p" when reader.IsEndElement:
                    sink.EnsureLineBreak();
                    break;
            }
        }
    }

    // w:tab also appears inside w:tabs (paragraph tab-stop definitions) where it is not content.
    private static bool IsInRun(OpenXmlReader reader) => reader.ElementType != typeof(DocumentFormat.OpenXml.Wordprocessing.TabStop);

    // ------------------------------------------------------------------ PowerPoint

    private static void ExtractPresentation(Stream stream, TextAccumulator sink, CancellationToken cancellationToken)
    {
        using var document = PresentationDocument.Open(stream, false);
        var presentationPart = document.PresentationPart;
        var slideIds = presentationPart?.Presentation?.SlideIdList?.Elements<P.SlideId>();
        if (presentationPart is null || slideIds is null)
        {
            return;
        }

        var notes = new List<NotesSlidePart>();
        foreach (var slideId in slideIds)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (sink.IsFull)
            {
                return;
            }

            if (slideId.RelationshipId?.Value is not { } relId
                || !presentationPart.TryGetPartById(relId, out var part)
                || part is not SlidePart slidePart)
            {
                continue;
            }

            sink.EnsureBlankLine();
            AppendDrawingText(slidePart, sink, cancellationToken);
            if (slidePart.NotesSlidePart is { } notesPart)
            {
                notes.Add(notesPart);
            }
        }

        foreach (var notesPart in notes)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (sink.IsFull)
            {
                return;
            }

            sink.EnsureBlankLine();
            AppendDrawingText(notesPart, sink, cancellationToken);
        }
    }

    /// <summary>Text from DrawingML paragraphs (a:p / a:t / a:br), as used by slides, notes and charts.</summary>
    private static void AppendDrawingText(OpenXmlPart part, TextAccumulator sink, CancellationToken cancellationToken)
    {
        using var reader = OpenXmlReader.Create(part);
        var count = 0;
        var hasNode = reader.Read();
        while (!sink.IsFull && hasNode)
        {
            if ((++count & 0xFFF) == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            // Slide-number / date fields in notes are just noise. Skip() already positions the reader on
            // the next sibling, so do not Read() again.
            if (reader.LocalName == "fld" && reader.IsStartElement)
            {
                reader.Skip();
                hasNode = !reader.EOF;
                continue;
            }

            switch (reader.LocalName)
            {
                case "t" when reader.IsStartElement:
                    sink.Append(reader.GetText());
                    break;
                case "br" when reader.IsStartElement:
                    sink.Append('\n');
                    break;
                case "p" when reader.IsEndElement:
                    sink.EnsureLineBreak();
                    break;
            }

            hasNode = reader.Read();
        }
    }

    // ------------------------------------------------------------------ Excel

    private static void ExtractSpreadsheet(Stream stream, TextAccumulator sink, int maxChars, CancellationToken cancellationToken)
    {
        using var document = SpreadsheetDocument.Open(stream, false);
        var workbookPart = document.WorkbookPart;
        var sheets = workbookPart?.Workbook?.Sheets?.Elements<Sheet>();
        if (workbookPart is null || sheets is null)
        {
            return;
        }

        var sharedStrings = LoadSharedStrings(workbookPart.SharedStringTablePart, maxChars, cancellationToken);
        var row = new List<string>();
        foreach (var sheet in sheets)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (sink.IsFull)
            {
                return;
            }

            if (sheet.Id?.Value is not { } relId
                || !workbookPart.TryGetPartById(relId, out var part)
                || part is not WorksheetPart worksheetPart)
            {
                continue; // chart sheets, dialog sheets
            }

            sink.EnsureBlankLine();
            sink.Append(sheet.Name?.Value ?? "Sheet");
            sink.Append('\n');

            using var reader = OpenXmlReader.Create(worksheetPart);
            var rows = 0;
            var hasNode = reader.Read();
            while (!sink.IsFull && hasNode)
            {
                if (reader.ElementType == typeof(Row))
                {
                    if (reader.IsStartElement)
                    {
                        row.Clear();
                    }
                    else if (reader.IsEndElement)
                    {
                        AppendRow(row, sink);
                        if (++rows >= MaxRowsPerSheet)
                        {
                            break;
                        }

                        if ((rows & 0xFF) == 0)
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                        }
                    }
                }
                else if (reader.ElementType == typeof(Cell) && reader.IsStartElement)
                {
                    // LoadCurrentElement leaves the reader on the following node: do not Read() again.
                    var cell = (Cell)reader.LoadCurrentElement()!;
                    var value = GetCellText(cell, sharedStrings);
                    if (!string.IsNullOrWhiteSpace(value))
                    {
                        row.Add(value.Trim());
                    }

                    hasNode = !reader.EOF;
                    continue;
                }

                hasNode = reader.Read();
            }

        }
    }

    private static void AppendRow(List<string> row, TextAccumulator sink)
    {
        if (row.Count == 0)
        {
            return;
        }

        for (var i = 0; i < row.Count; i++)
        {
            if (i > 0)
            {
                sink.Append('\t');
            }

            sink.Append(row[i]);
        }

        sink.Append('\n');
    }

    private static string? GetCellText(Cell cell, List<string> sharedStrings)
    {
        var type = cell.DataType?.Value;
        if (type == CellValues.InlineString)
        {
            return cell.InlineString?.InnerText;
        }

        var raw = cell.CellValue?.Text;
        if (string.IsNullOrEmpty(raw))
        {
            return null;
        }

        if (type == CellValues.SharedString)
        {
            return int.TryParse(raw, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var index)
                   && index >= 0 && index < sharedStrings.Count
                ? sharedStrings[index]
                : null;
        }

        if (type == CellValues.Boolean)
        {
            return raw == "1" ? "TRUE" : "FALSE";
        }

        if (type == CellValues.Error)
        {
            return null;
        }

        return raw; // numbers, dates (as serials) and formula string results
    }

    private static List<string> LoadSharedStrings(SharedStringTablePart? part, int maxChars, CancellationToken cancellationToken)
    {
        var strings = new List<string>();
        if (part is null)
        {
            return strings;
        }

        // Shared strings are referenced by index, so keep reading past the text budget a little; but never
        // without bound (a hostile file could hold gigabytes of strings).
        var budget = (long)Math.Max(maxChars, 1) * 4;
        long total = 0;
        using var reader = OpenXmlReader.Create(part);
        var hasNode = reader.Read();
        while (hasNode)
        {
            if (reader.ElementType != typeof(SharedStringItem) || !reader.IsStartElement)
            {
                hasNode = reader.Read();
                continue;
            }

            // LoadCurrentElement leaves the reader on the following node.
            var item = reader.LoadCurrentElement();
            hasNode = !reader.EOF;
            var text = item?.InnerText ?? string.Empty;
            strings.Add(text);
            total += text.Length;
            if (total > budget)
            {
                break;
            }

            if ((strings.Count & 0xFFF) == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }
        }

        return strings;
    }
}
