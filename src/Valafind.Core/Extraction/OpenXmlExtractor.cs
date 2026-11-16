using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Xml;
using Valafind.Core.Abstractions;

namespace Valafind.Core.Extraction;

/// <summary>
/// Extracts text from Office Open XML files: Word (.docx/.docm/.dotx: body incl. tables, then footnotes and
/// endnotes), PowerPoint (.pptx/.pptm: slide text in slide order, then speaker notes) and Excel (.xlsx/.xlsm:
/// cell values by sheet with the sheet name as a heading).
/// </summary>
/// <remarks>
/// The package is read directly (zip + relationship parts + streaming <see cref="XmlReader"/>), never through a
/// DOM, so hostile files have bounded cost: at most <see cref="SafeZip.MaxEntries"/> zip entries,
/// <see cref="SafeZip.MaxEntryBytes"/> decompressed bytes per part and <see cref="SafeZip.MaxFileBytes"/> per
/// file; text nodes are read in chunks and stop at the character budget; shared strings, rows, cells per row,
/// sheets, slides and relationships are capped.
/// </remarks>
public sealed class OpenXmlExtractor : ITextExtractor
{
    /// <summary>Maximum rows read per worksheet; later rows are usually repetitive data.</summary>
    internal const int MaxRowsPerSheet = 5_000;

    /// <summary>Maximum cells read per row (Excel's own column limit).</summary>
    internal const int MaxCellsPerRow = 16_384;

    /// <summary>Maximum worksheets read per workbook.</summary>
    internal const int MaxSheets = 256;

    /// <summary>Maximum slides read per presentation.</summary>
    internal const int MaxSlides = 5_000;

    /// <summary>Maximum shared-string items loaded (also bounded by a character budget).</summary>
    internal const int MaxSharedStrings = 1_000_000;

    /// <summary>Maximum characters kept from one cell value or shared string.</summary>
    internal const int MaxCellChars = 32 * 1024;

    private const int MaxRelationships = 10_000;

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
            using var zip = SafeZip.Open(stream);
            var package = new Package(SafeZip.Index(zip), cancellationToken);
            switch (kind.Value)
            {
                case FileKind.Document:
                    ExtractWord(package, sink);
                    break;
                case FileKind.Presentation:
                    ExtractPresentation(package, sink);
                    break;
                default:
                    ExtractSpreadsheet(package, sink, limits.MaxChars);
                    break;
            }
        }
        catch (InvalidDataException)
        {
            return Task.FromResult<ExtractedText?>(null); // not a zip / damaged archive
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
            // Per-file decompression budget used up: keep what was read within it.
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

    private static void ExtractWord(Package package, TextAccumulator sink)
    {
        var main = package.MainPart("word/document.xml");
        if (main is null)
        {
            return;
        }

        package.ReadPart(main, reader => AppendWordprocessingText(reader, sink, package.CancellationToken));
        var relationships = package.Relationships(main);
        foreach (var type in (ReadOnlySpan<string>)["/footnotes", "/endnotes"])
        {
            if (sink.IsFull)
            {
                return;
            }

            if (Package.FirstOfType(relationships, type) is { } part)
            {
                sink.EnsureBlankLine();
                package.ReadPart(part, reader => AppendWordprocessingText(reader, sink, package.CancellationToken));
            }
        }
    }

    private static void AppendWordprocessingText(XmlReader reader, TextAccumulator sink, CancellationToken cancellationToken)
    {
        var buffer = new char[BoundedXml.ChunkSize];
        var textDepth = -1; // depth of the open w:t / a:t / m:t element
        var tabsDepth = -1; // inside w:tabs / a:tabLst: tab-stop definitions, not content
        var nodes = 0;
        while (!sink.IsFull && reader.Read())
        {
            if ((++nodes & 0xFFF) == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            switch (reader.NodeType)
            {
                case XmlNodeType.Element:
                    switch (reader.LocalName)
                    {
                        case "t" when !reader.IsEmptyElement:
                            textDepth = reader.Depth;
                            break;
                        case "tabs" or "tabLst" when !reader.IsEmptyElement:
                            tabsDepth = reader.Depth;
                            break;
                        case "tab" when tabsDepth < 0:
                            sink.Append('\t');
                            break;
                        case "br" or "cr":
                            sink.Append('\n');
                            break;
                        case "p" when reader.IsEmptyElement:
                            sink.EnsureLineBreak();
                            break;
                    }

                    break;
                case XmlNodeType.EndElement:
                    if (reader.Depth == textDepth)
                    {
                        textDepth = -1;
                    }
                    else if (reader.Depth == tabsDepth)
                    {
                        tabsDepth = -1;
                    }
                    else if (reader.LocalName == "p")
                    {
                        sink.EnsureLineBreak();
                    }

                    break;
                default:
                    if (textDepth >= 0 && BoundedXml.IsText(reader.NodeType))
                    {
                        BoundedXml.AppendValue(reader, sink, buffer);
                    }

                    break;
            }
        }
    }

    // ------------------------------------------------------------------ PowerPoint

    private static void ExtractPresentation(Package package, TextAccumulator sink)
    {
        var presentation = package.MainPart("ppt/presentation.xml");
        if (presentation is null)
        {
            return;
        }

        // Slide order is the order of p:sldIdLst/p:sldId; each points at a slide through a relationship id.
        var slideRelIds = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        package.ReadPart(presentation, reader =>
        {
            while (reader.Read() && slideRelIds.Count < MaxSlides)
            {
                if (reader.NodeType == XmlNodeType.Element && reader.LocalName == "sldId"
                    && Package.GetRelationshipIdAttribute(reader) is { } id && seen.Add(id))
                {
                    slideRelIds.Add(id);
                }
            }
        });

        var relationships = package.Relationships(presentation);
        var notes = new List<string>();
        foreach (var id in slideRelIds)
        {
            package.CancellationToken.ThrowIfCancellationRequested();
            if (sink.IsFull)
            {
                return;
            }

            if (!relationships.ById.TryGetValue(id, out var slideRel))
            {
                continue;
            }

            var slide = slideRel.Target;

            sink.EnsureBlankLine();
            package.ReadPart(slide, reader => AppendDrawingText(reader, sink, package.CancellationToken));
            if (Package.FirstOfType(package.Relationships(slide), "/notesSlide") is { } notesPart)
            {
                notes.Add(notesPart);
            }
        }

        foreach (var notesPart in notes)
        {
            package.CancellationToken.ThrowIfCancellationRequested();
            if (sink.IsFull)
            {
                return;
            }

            sink.EnsureBlankLine();
            package.ReadPart(notesPart, reader => AppendDrawingText(reader, sink, package.CancellationToken));
        }
    }

    /// <summary>Text from DrawingML paragraphs (a:p / a:t / a:br), as used by slides, notes and charts.</summary>
    private static void AppendDrawingText(XmlReader reader, TextAccumulator sink, CancellationToken cancellationToken)
    {
        var buffer = new char[BoundedXml.ChunkSize];
        var textDepth = -1;
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
                    switch (reader.LocalName)
                    {
                        case "fld":
                            // Slide-number / date fields in notes are just noise. Skip() leaves the reader on
                            // the next node, which must be processed without Read().
                            reader.Skip();
                            advance = false;
                            break;
                        case "t" when !reader.IsEmptyElement:
                            textDepth = reader.Depth;
                            break;
                        case "br":
                            sink.Append('\n');
                            break;
                        case "p" when reader.IsEmptyElement:
                            sink.EnsureLineBreak();
                            break;
                    }

                    break;
                case XmlNodeType.EndElement:
                    if (reader.Depth == textDepth)
                    {
                        textDepth = -1;
                    }
                    else if (reader.LocalName == "p")
                    {
                        sink.EnsureLineBreak();
                    }

                    break;
                default:
                    if (textDepth >= 0 && BoundedXml.IsText(reader.NodeType))
                    {
                        BoundedXml.AppendValue(reader, sink, buffer);
                    }

                    break;
            }
        }
    }

    // ------------------------------------------------------------------ Excel

    private static void ExtractSpreadsheet(Package package, TextAccumulator sink, int maxChars)
    {
        var workbook = package.MainPart("xl/workbook.xml");
        if (workbook is null)
        {
            return;
        }

        var sheets = new List<(string Name, string RelId)>();
        package.ReadPart(workbook, reader =>
        {
            while (reader.Read() && sheets.Count < MaxSheets)
            {
                if (reader.NodeType == XmlNodeType.Element && reader.LocalName == "sheet"
                    && Package.GetRelationshipIdAttribute(reader) is { } id)
                {
                    sheets.Add((BoundedXml.GetAttribute(reader, "name", 256) ?? "Sheet", id));
                }
            }
        });

        var relationships = package.Relationships(workbook);
        var sharedStrings = Package.FirstOfType(relationships, "/sharedStrings") is { } sstPart
            ? LoadSharedStrings(package, sstPart, maxChars)
            : [];

        foreach (var (name, relId) in sheets)
        {
            package.CancellationToken.ThrowIfCancellationRequested();
            if (sink.IsFull)
            {
                return;
            }

            if (!relationships.ById.TryGetValue(relId, out var sheetRel)
                || !sheetRel.Type.EndsWith("/worksheet", StringComparison.Ordinal))
            {
                continue; // chart sheets, dialog sheets
            }

            sink.EnsureBlankLine();
            sink.Append(name);
            sink.Append('\n');
            package.ReadPart(sheetRel.Target, reader => AppendWorksheet(reader, sink, sharedStrings, package.CancellationToken));
        }
    }

    private static void AppendWorksheet(XmlReader reader, TextAccumulator sink, List<string> sharedStrings, CancellationToken cancellationToken)
    {
        var buffer = new char[BoundedXml.ChunkSize];
        var value = new StringBuilder();   // <v> of the current cell
        var inline = new StringBuilder();  // <is> text of the current cell
        string? cellType = null;
        var cellDepth = -1;
        var collect = 0;                   // 1 = collecting <v>, 2 = collecting <is>/<t>
        var collectDepth = -1;
        var phoneticDepth = -1;
        var rowHasText = false;
        var cellsInRow = 0;
        var rows = 0;
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
                    var name = reader.LocalName;
                    if (cellDepth < 0)
                    {
                        if (name == "row")
                        {
                            rowHasText = false;
                            cellsInRow = 0;
                            if (reader.IsEmptyElement && ++rows >= MaxRowsPerSheet)
                            {
                                return;
                            }
                        }
                        else if (name == "c")
                        {
                            if (++cellsInRow > MaxCellsPerRow)
                            {
                                reader.Skip();
                                advance = false;
                            }
                            else if (!reader.IsEmptyElement)
                            {
                                cellDepth = reader.Depth;
                                cellType = BoundedXml.GetAttribute(reader, "t", 16);
                                value.Clear();
                                inline.Clear();
                            }
                        }

                        break;
                    }

                    if (reader.IsEmptyElement)
                    {
                        break;
                    }

                    if (name == "rPh")
                    {
                        phoneticDepth = reader.Depth; // phonetic reading guide, not cell text
                    }
                    else if (name == "v" && collect == 0)
                    {
                        collect = 1;
                        collectDepth = reader.Depth;
                    }
                    else if (name == "t" && collect == 0 && phoneticDepth < 0)
                    {
                        collect = 2;
                        collectDepth = reader.Depth;
                    }

                    break;
                }

                case XmlNodeType.EndElement:
                    if (reader.Depth == collectDepth)
                    {
                        collect = 0;
                        collectDepth = -1;
                    }
                    else if (reader.Depth == phoneticDepth)
                    {
                        phoneticDepth = -1;
                    }
                    else if (reader.Depth == cellDepth)
                    {
                        cellDepth = -1;
                        var text = GetCellText(cellType, value, inline, sharedStrings);
                        if (!string.IsNullOrWhiteSpace(text))
                        {
                            if (rowHasText)
                            {
                                sink.Append('\t');
                            }

                            sink.Append(text.AsSpan().Trim());
                            rowHasText = true;
                        }
                    }
                    else if (cellDepth < 0 && reader.LocalName == "row")
                    {
                        if (rowHasText)
                        {
                            sink.Append('\n');
                        }

                        if (++rows >= MaxRowsPerSheet)
                        {
                            return;
                        }
                    }

                    break;

                default:
                    if (collect != 0 && BoundedXml.IsText(reader.NodeType))
                    {
                        BoundedXml.AppendValue(reader, collect == 1 ? value : inline, MaxCellChars, buffer);
                    }

                    break;
            }
        }
    }

    private static string? GetCellText(string? type, StringBuilder value, StringBuilder inline, List<string> sharedStrings)
    {
        switch (type)
        {
            case "inlineStr":
                return inline.ToString();
            case "s":
                return int.TryParse(value.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var index)
                       && index >= 0 && index < sharedStrings.Count
                    ? sharedStrings[index]
                    : null;
            case "b":
                return value.Length == 0 ? null : value.ToString().Trim() == "1" ? "TRUE" : "FALSE";
            case "e":
                return null;
            default:
                return value.ToString(); // numbers, dates (as serials) and formula string results
        }
    }

    private static List<string> LoadSharedStrings(Package package, string part, int maxChars)
    {
        // Shared strings are referenced by index, so keep reading past the text budget a little; but never
        // without bound. Each item costs at least one unit so millions of empty <si/> cannot grow the list.
        var strings = new List<string>();
        var budget = (long)Math.Max(maxChars, 1) * 4;
        long total = 0;
        package.ReadPart(part, reader =>
        {
            var buffer = new char[BoundedXml.ChunkSize];
            var item = new StringBuilder();
            var itemDepth = -1;
            var textDepth = -1;
            var phoneticDepth = -1;
            while (reader.Read())
            {
                switch (reader.NodeType)
                {
                    case XmlNodeType.Element:
                        var name = reader.LocalName;
                        if (name == "si")
                        {
                            if (reader.IsEmptyElement)
                            {
                                if (!Add(string.Empty))
                                {
                                    return;
                                }
                            }
                            else
                            {
                                itemDepth = reader.Depth;
                                item.Clear();
                            }
                        }
                        else if (itemDepth >= 0 && !reader.IsEmptyElement)
                        {
                            if (name == "rPh")
                            {
                                phoneticDepth = reader.Depth;
                            }
                            else if (name == "t" && phoneticDepth < 0)
                            {
                                textDepth = reader.Depth;
                            }
                        }

                        break;
                    case XmlNodeType.EndElement:
                        if (reader.Depth == textDepth)
                        {
                            textDepth = -1;
                        }
                        else if (reader.Depth == phoneticDepth)
                        {
                            phoneticDepth = -1;
                        }
                        else if (reader.Depth == itemDepth)
                        {
                            itemDepth = -1;
                            if (!Add(item.ToString()))
                            {
                                return;
                            }
                        }

                        break;
                    default:
                        if (textDepth >= 0 && BoundedXml.IsText(reader.NodeType))
                        {
                            BoundedXml.AppendValue(reader, item, MaxCellChars, buffer);
                        }

                        break;
                }
            }
        });

        return strings;

        bool Add(string text)
        {
            strings.Add(text);
            total += text.Length + 1;
            if ((strings.Count & 0xFFF) == 0)
            {
                package.CancellationToken.ThrowIfCancellationRequested();
            }

            return total <= budget && strings.Count < MaxSharedStrings;
        }
    }

    // ------------------------------------------------------------------ Package (OPC) access

    private sealed record Relationship(string Id, string Type, string Target);

    private sealed class RelationshipSet
    {
        internal List<Relationship> All { get; } = [];

        internal Dictionary<string, Relationship> ById { get; } = new(StringComparer.Ordinal);

        internal void Add(Relationship relationship)
        {
            All.Add(relationship);
            ById.TryAdd(relationship.Id, relationship);
        }
    }

    /// <summary>Minimal, bounded Open Packaging Conventions reader over a zip index.</summary>
    private sealed class Package(Dictionary<string, ZipArchiveEntry> entries, CancellationToken cancellationToken)
    {
        private readonly DecompressionBudget _budget = SafeZip.NewFileBudget();
        private readonly Dictionary<string, RelationshipSet> _relationships = new(StringComparer.OrdinalIgnoreCase);

        internal CancellationToken CancellationToken { get; } = cancellationToken;

        /// <summary>The package's main part (officeDocument relationship), or <paramref name="fallback"/> if present.</summary>
        internal string? MainPart(string fallback)
        {
            var root = ReadRelationships("_rels/.rels", string.Empty);
            if (FirstOfType(root, "/officeDocument") is { } main && entries.ContainsKey(main))
            {
                return main;
            }

            return entries.ContainsKey(fallback) ? fallback : null;
        }

        internal RelationshipSet Relationships(string part)
        {
            if (_relationships.TryGetValue(part, out var cached))
            {
                return cached;
            }

            var slash = part.LastIndexOf('/');
            var directory = slash >= 0 ? part[..(slash + 1)] : string.Empty;
            var relsPath = directory + "_rels/" + part[(slash + 1)..] + ".rels";
            var result = ReadRelationships(relsPath, directory);
            _relationships[part] = result;
            return result;
        }

        internal static string? FirstOfType(RelationshipSet relationships, string typeSuffix) =>
            relationships.All.FirstOrDefault(r => r.Type.EndsWith(typeSuffix, StringComparison.Ordinal))?.Target;

        /// <summary>The r:id attribute (any relationships namespace, transitional or strict).</summary>
        internal static string? GetRelationshipIdAttribute(XmlReader reader)
        {
            if (!reader.MoveToFirstAttribute())
            {
                return null;
            }

            try
            {
                do
                {
                    if (reader.LocalName == "id"
                        && reader.NamespaceURI.EndsWith("/relationships", StringComparison.Ordinal)
                        && reader.Value.Length <= 256)
                    {
                        return reader.Value;
                    }
                }
                while (reader.MoveToNextAttribute());

                return null;
            }
            finally
            {
                reader.MoveToElement();
            }
        }

        /// <summary>
        /// Streams one part through <paramref name="read"/>. A part that is missing, looks like a decompression
        /// bomb or exceeds the per-part limit is skipped (text read before the limit is kept); exhausting the
        /// per-file budget propagates so the whole extraction stops.
        /// </summary>
        internal void ReadPart(string part, Action<XmlReader> read)
        {
            CancellationToken.ThrowIfCancellationRequested();
            if (!entries.TryGetValue(part, out var entry))
            {
                return;
            }

            try
            {
                using var stream = SafeZip.OpenEntry(entry, _budget);
                using var reader = XmlReader.Create(stream, BoundedXml.Settings);
                read(reader);
            }
            catch (ExtractionLimitException ex) when (!ex.FileBudgetExhausted)
            {
            }
        }

        private RelationshipSet ReadRelationships(string relsPath, string baseDirectory)
        {
            var result = new RelationshipSet();
            try
            {
                ReadPart(relsPath, reader =>
                {
                    while (reader.Read() && result.All.Count < MaxRelationships)
                    {
                        if (reader.NodeType != XmlNodeType.Element || reader.LocalName != "Relationship"
                            || string.Equals(reader.GetAttribute("TargetMode"), "External", StringComparison.OrdinalIgnoreCase)
                            || BoundedXml.GetAttribute(reader, "Id", 256) is not { } id
                            || BoundedXml.GetAttribute(reader, "Type", 1024) is not { } type
                            || BoundedXml.GetAttribute(reader, "Target", 2048) is not { } target)
                        {
                            continue;
                        }

                        result.Add(new Relationship(id, type, Resolve(baseDirectory, target)));
                    }
                });
            }
            catch (XmlException)
            {
                // Broken relationships: use what was parsed (callers fall back to default part names).
            }

            return result;
        }

        private static string Resolve(string baseDirectory, string target)
        {
            var hash = target.IndexOf('#', StringComparison.Ordinal);
            if (hash >= 0)
            {
                target = target[..hash];
            }

            target = Uri.UnescapeDataString(target).Replace('\\', '/');
            return SafeZip.NormalizePath(target.StartsWith('/') ? target : baseDirectory + target);
        }
    }
}
