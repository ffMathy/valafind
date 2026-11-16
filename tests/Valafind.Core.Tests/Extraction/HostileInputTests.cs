using System.Diagnostics;
using System.IO.Compression;
using System.Text;
using Valafind.Core.Abstractions;
using Valafind.Core.Extraction;
using Xunit;
using static Valafind.Core.Tests.Extraction.BombFixtures;

namespace Valafind.Core.Tests.Extraction;

/// <summary>
/// Regression tests for hostile input: every extractor must finish in bounded time and memory, whatever the
/// file claims. Bombs are a few hundred kilobytes on disk and expand to a gigabyte or more.
/// </summary>
public sealed class HostileInputTests : IDisposable
{
    /// <summary>Managed bytes the extraction thread may allocate for one hostile file.</summary>
    private const long MaxAllocatedBytes = 300L * 1024 * 1024;

    /// <summary>Wall-clock bound per hostile file. Bounded files finish in well under a second here; the margin
    /// is for slow shared CI runners. An unbounded decode is caught by the allocation check regardless.</summary>
    private static readonly TimeSpan MaxElapsed = TimeSpan.FromSeconds(15);

    private readonly ExtractionFixtures _fx = new();
    private readonly DocumentReader _reader = new();

    public void Dispose() => _fx.Dispose();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>Reads the file and checks the cost. Extraction runs synchronously on this thread, so the
    /// per-thread allocation counter isolates it from tests running in parallel.</summary>
    private async Task<ExtractedText?> ReadBounded(string path, long maxAllocated = MaxAllocatedBytes)
    {
        var before = GC.GetAllocatedBytesForCurrentThread();
        var watch = Stopwatch.StartNew();
        var result = await _reader.ReadAsync(path, Ct);
        watch.Stop();
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.True(allocated < maxAllocated, $"{Path.GetFileName(path)}: allocated {allocated / (1024 * 1024)} MB");
        Assert.True(watch.Elapsed < MaxElapsed, $"{Path.GetFileName(path)}: took {watch.Elapsed}");
        return result;
    }

    // ------------------------------------------------------------------ fixture self-check

    [Fact]
    public void DeflateRepeated_DecodesToTheDeclaredContent()
    {
        var data = DeflateRepeated("<a>"u8.ToArray(), "xy"u8.ToArray(), 3L << 20, "</a>"u8.ToArray(), out var length);
        using var inflater = new DeflateStream(new MemoryStream(data), CompressionMode.Decompress);
        using var decoded = new MemoryStream();
        inflater.CopyTo(decoded);
        var bytes = decoded.ToArray();
        Assert.Equal(length, bytes.Length);
        Assert.StartsWith("<a>xyxy", Encoding.ASCII.GetString(bytes, 0, 16), StringComparison.Ordinal);
        Assert.EndsWith("xy</a>", Encoding.ASCII.GetString(bytes, bytes.Length - 16, 16), StringComparison.Ordinal);
        Assert.True(data.Length < length / 100, $"compressed {data.Length} of {length}");
    }

    // ------------------------------------------------------------------ xml-text-node-bomb

    [Fact]
    public async Task OdtGigabyteTextNode_IsBounded()
    {
        var path = _fx.WriteBytes("bomb.odt", Zip(
            Entry("mimetype", "application/vnd.oasis.opendocument.text"),
            BombEntry("content.xml", OdtPrefix + "<text:p>", " ", OneGigabyte, "</text:p>" + OdtSuffix)));
        Assert.True(new FileInfo(path).Length < 2 * 1024 * 1024);

        var result = await ReadBounded(path);
        Assert.Equal(new ExtractedText(string.Empty, FileKind.Document), result);
    }

    [Fact]
    public async Task DocxGigabyteTextNode_IsBounded()
    {
        var path = _fx.WriteBytes("bomb.docx", Zip(
            Entry("[Content_Types].xml", ContentTypes),
            Entry("_rels/.rels", RootRels("word/document.xml")),
            BombEntry("word/document.xml", WordPrefix + "<w:p><w:r><w:t>", " ", OneGigabyte, "</w:t></w:r></w:p>" + WordSuffix)));

        var result = await ReadBounded(path);
        Assert.Equal(new ExtractedText(string.Empty, FileKind.Document), result);
    }

    [Fact]
    public async Task DocxRepetitivePartAboveRatioCheck_KeepsLeadingText()
    {
        // 12 MB of empty paragraphs compresses far beyond 100:1. Such a part is read only up to the smaller
        // suspicious-entry cap, but the text before the cap is kept instead of dropping the whole part.
        var path = _fx.WriteBytes("repetitive.docx", Zip(
            Entry("[Content_Types].xml", ContentTypes),
            Entry("_rels/.rels", RootRels("word/document.xml")),
            BombEntry("word/document.xml", WordPrefix + "<w:p><w:r><w:t>Leading words</w:t></w:r></w:p>", "<w:p/>", 12L << 20, WordSuffix)));

        var result = await ReadBounded(path);
        Assert.NotNull(result);
        Assert.Contains("Leading words", result.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task OdtRepetitiveContentAboveRatioCheck_KeepsLeadingText()
    {
        var path = _fx.WriteBytes("repetitive.odt", Zip(
            Entry("mimetype", "application/vnd.oasis.opendocument.text"),
            BombEntry("content.xml", OdtPrefix + "<text:p>Leading words</text:p>", "<text:p/>", 12L << 20, OdtSuffix)));

        var result = await ReadBounded(path);
        Assert.NotNull(result);
        Assert.Contains("Leading words", result.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DocxLargeTextNodeBelowRatioCheck_IsReadInChunks()
    {
        // 7 MB in one w:t: small enough to pass the up-front ratio check, so the streaming reader must stop
        // at the character budget without materializing the node (which alone would be a 14 MB string).
        var path = _fx.WriteBytes("big-node.docx", Zip(
            Entry("[Content_Types].xml", ContentTypes),
            Entry("_rels/.rels", RootRels("word/document.xml")),
            BombEntry("word/document.xml", WordPrefix + "<w:p><w:r><w:t>", "a", 7L << 20, "</w:t></w:r></w:p>" + WordSuffix)));

        var result = await ReadBounded(path, maxAllocated: 10L * 1024 * 1024);
        Assert.NotNull(result);
        Assert.Equal(_reader.Limits.MaxChars, result.Text.Length);
        Assert.All(result.Text, c => Assert.Equal('a', c));
    }

    [Fact]
    public async Task OdtLargeCellTextBelowRatioCheck_IsReadInChunks()
    {
        var path = _fx.WriteBytes("big-cell.ods", Zip(
            Entry("mimetype", "application/vnd.oasis.opendocument.spreadsheet"),
            BombEntry("content.xml",
                OdtPrefix.Replace("<office:text>", "<office:spreadsheet>", StringComparison.Ordinal)
                    .Replace("xmlns:text=", "xmlns:table=\"urn:oasis:names:tc:opendocument:xmlns:table:1.0\" xmlns:text=", StringComparison.Ordinal)
                + "<table:table table:name=\"T\"><table:table-row><table:table-cell><text:p>",
                " ", 7L << 20,
                "x</text:p></table:table-cell><table:table-cell><text:p>y</text:p></table:table-cell></table:table-row></table:table></office:spreadsheet></office:body></office:document-content>")));

        var result = await ReadBounded(path, maxAllocated: 10L * 1024 * 1024);
        Assert.NotNull(result);
        Assert.Equal("T\nx\ty", result.Text);
    }

    // ------------------------------------------------------------------ unbounded-collections-xlsx-epub

    [Fact]
    public async Task XlsxGigabyteOfEmptySharedStrings_IsBounded()
    {
        var path = _fx.WriteBytes("sst-bomb.xlsx", Zip(
            Entry("[Content_Types].xml", ContentTypes),
            Entry("_rels/.rels", RootRels("xl/workbook.xml")),
            Entry("xl/workbook.xml", WorkbookXml()),
            Entry("xl/_rels/workbook.xml.rels", WorkbookRels(sharedStrings: true)),
            Entry("xl/worksheets/sheet1.xml", SheetPrefix + "<row r=\"1\"><c r=\"A1\" t=\"inlineStr\"><is><t>visible</t></is></c></row>" + SheetSuffix),
            BombEntry("xl/sharedStrings.xml", SstPrefix, "<si/>", OneGigabyte, "</sst>")));

        var result = await ReadBounded(path);
        Assert.NotNull(result);
        Assert.Equal("S1\nvisible", result.Text);
    }

    [Fact]
    public async Task XlsxMillionsOfEmptySharedStrings_AreCapped()
    {
        // 1.3 million <si/> (6.5 MB): below the ratio check, so the item cap must stop the list from growing.
        var path = _fx.WriteBytes("sst.xlsx", Zip(
            Entry("[Content_Types].xml", ContentTypes),
            Entry("_rels/.rels", RootRels("xl/workbook.xml")),
            Entry("xl/workbook.xml", WorkbookXml()),
            Entry("xl/_rels/workbook.xml.rels", WorkbookRels(sharedStrings: true)),
            Entry("xl/worksheets/sheet1.xml", SheetPrefix + "<row r=\"1\"><c r=\"A1\" t=\"s\"><v>0</v></c></row>" + SheetSuffix),
            BombEntry("xl/sharedStrings.xml", SstPrefix + "<si><t>first</t></si>", "<si/>", 6_500_000, "</sst>")));

        var result = await ReadBounded(path, maxAllocated: 64L * 1024 * 1024);
        Assert.NotNull(result);
        Assert.Equal("S1\nfirst", result.Text);
    }

    [Fact]
    public async Task XlsxRowWithHundredsOfThousandsOfCells_IsBounded()
    {
        var path = _fx.WriteBytes("row.xlsx", Zip(
            Entry("[Content_Types].xml", ContentTypes),
            Entry("_rels/.rels", RootRels("xl/workbook.xml")),
            Entry("xl/workbook.xml", WorkbookXml()),
            Entry("xl/_rels/workbook.xml.rels", WorkbookRels()),
            BombEntry("xl/worksheets/sheet1.xml", SheetPrefix + "<row r=\"1\">", "<c><v>12345</v></c>", 6_500_000, "</row>" + SheetSuffix)));

        var result = await ReadBounded(path, maxAllocated: 64L * 1024 * 1024);
        Assert.NotNull(result);
        Assert.StartsWith("S1\n12345\t12345", result.Text, StringComparison.Ordinal);
        Assert.True(result.Text.Length <= _reader.Limits.MaxChars);
    }

    [Fact]
    public async Task XlsxRowOfEmptyCells_StopsAtCellCap()
    {
        // 1.6 million <c/> in one row followed by a second row: the per-row cap skips the rest of the row
        // without keeping anything, and the next row is still read.
        var path = _fx.WriteBytes("empty-cells.xlsx", Zip(
            Entry("[Content_Types].xml", ContentTypes),
            Entry("_rels/.rels", RootRels("xl/workbook.xml")),
            Entry("xl/workbook.xml", WorkbookXml()),
            Entry("xl/_rels/workbook.xml.rels", WorkbookRels()),
            BombEntry("xl/worksheets/sheet1.xml", SheetPrefix + "<row r=\"1\">", "<c/>", 6_500_000,
                "</row><row r=\"2\"><c t=\"inlineStr\"><is><t>after</t></is></c></row>" + SheetSuffix)));

        var result = await ReadBounded(path, maxAllocated: 64L * 1024 * 1024);
        Assert.NotNull(result);
        Assert.Equal("S1\nafter", result.Text);
    }

    [Fact]
    public async Task EpubRepeatedSpineItems_AreReadOnce()
    {
        // 300k itemrefs to one empty chapter (6 MB of OPF): each chapter is opened once.
        var path = _fx.WriteBytes("spine.epub", Zip(
            Entry("mimetype", "application/epub+zip"),
            Entry("META-INF/container.xml", EpubContainer),
            BombEntry("OEBPS/content.opf", OpfPrefix, "<itemref idref=\"c1\"/>", 6_300_000, "<itemref idref=\"c2\"/></spine></package>"),
            Entry("OEBPS/c1.xhtml", "<html><body></body></html>"),
            Entry("OEBPS/c2.xhtml", "<html><body><p>Chapter two</p></body></html>")));

        var result = await ReadBounded(path, maxAllocated: 64L * 1024 * 1024);
        Assert.NotNull(result);
        Assert.Equal("Chapter two", result.Text);
    }

    [Fact]
    public async Task EpubGigabyteSpine_IsBounded()
    {
        var path = _fx.WriteBytes("spine-bomb.epub", Zip(
            Entry("mimetype", "application/epub+zip"),
            Entry("META-INF/container.xml", EpubContainer),
            BombEntry("OEBPS/content.opf", OpfPrefix, "<itemref idref=\"c1\"/>", OneGigabyte, "</spine></package>"),
            Entry("OEBPS/c1.xhtml", "<html><body><p>Chapter one</p></body></html>"),
            Entry("OEBPS/c2.xhtml", "<html><body><p>Chapter two</p></body></html>")));

        var result = await ReadBounded(path);
        Assert.NotNull(result);
        Assert.Equal(FileKind.Document, result.Kind);
        Assert.Contains("Chapter one", result.Text, StringComparison.Ordinal); // name-order fallback still reads chapters
    }

    [Fact]
    public async Task EpubGigabyteChapter_IsSkipped()
    {
        var path = _fx.WriteBytes("chapter-bomb.epub", Zip(
            Entry("mimetype", "application/epub+zip"),
            Entry("META-INF/container.xml", EpubContainer),
            Entry("OEBPS/content.opf", OpfPrefix + "<itemref idref=\"c1\"/><itemref idref=\"c2\"/></spine></package>"),
            BombEntry("OEBPS/c1.xhtml", "<html><body><p>", " ", OneGigabyte, "</p></body></html>"),
            Entry("OEBPS/c2.xhtml", "<html><body><p>Chapter two</p></body></html>")));

        var result = await ReadBounded(path);
        Assert.NotNull(result);
        Assert.Equal("Chapter two", result.Text);
    }

    [Fact]
    public async Task ZipWithTensOfThousandsOfEntries_IsNameOnly()
    {
        var items = new List<ZipItem>
        {
            Entry("[Content_Types].xml", ContentTypes),
            Entry("_rels/.rels", RootRels("word/document.xml")),
            Entry("word/document.xml", WordPrefix + "<w:p><w:r><w:t>hidden</w:t></w:r></w:p>" + WordSuffix),
        };
        for (var i = 0; i < SafeZip.MaxEntries; i++)
        {
            items.Add(new ZipItem($"x/{i}", [], 0, 0, Deflated: false));
        }

        var path = _fx.WriteBytes("many.docx", Zip([.. items]));
        var result = await ReadBounded(path, maxAllocated: 64L * 1024 * 1024);
        Assert.Equal(new ExtractedText(string.Empty, FileKind.Document), result);
    }

    [Fact]
    public void CentralDirectoryCount_MatchesZipArchive()
    {
        var bytes = Zip(Entry("a", "1"), Entry("b/c", "2"), Entry("d", "3"));
        using var stream = new MemoryStream(bytes);
        Assert.Equal(3, SafeZip.CountCentralDirectoryEntries(stream, 100));
        Assert.Equal(3, SafeZip.CountCentralDirectoryEntries(stream, 2)); // stops at cap + 1
        using var zip = SafeZip.Open(stream);
        Assert.Equal(3, zip.Entries.Count);
    }

    // ------------------------------------------------------------------ pdf-flate-bomb

    [Fact]
    public async Task PdfGigabyteContentStream_IsNameOnly()
    {
        var path = _fx.WriteBytes("bomb.pdf", Pdf(
            Obj("<< /Type /Catalog /Pages 2 0 R >>"),
            Obj("<< /Type /Pages /Kids [3 0 R] /Count 1 >>"),
            Obj("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Resources << /Font << /F1 4 0 R >> >> /Contents 5 0 R >>"),
            Font(),
            PdfStream("/Filter /FlateDecode", ZlibRepeated("BT /F1 12 Tf 40 750 Td ", (byte)' ', OneGigabyte, "(hi) Tj ET"))));
        Assert.True(new FileInfo(path).Length < 2 * 1024 * 1024);

        var result = await ReadBounded(path);
        Assert.Equal(new ExtractedText(string.Empty, FileKind.Pdf), result);
    }

    [Fact]
    public async Task PdfPageOverDecodedBudget_IsSkipped_OtherPagesKept()
    {
        // Page 2 has three 12 MB content streams: each is under the per-stream limit, together they exceed
        // the per-page limit, so only page 2 is skipped.
        var big = ZlibRepeated("BT /F1 12 Tf 40 700 Td ", (byte)' ', 12L << 20, "(Second page) Tj ET");
        var path = _fx.WriteBytes("heavy-page.pdf", Pdf(
            Obj("<< /Type /Catalog /Pages 2 0 R >>"),
            Obj("<< /Type /Pages /Kids [3 0 R 4 0 R] /Count 2 /Resources << /Font << /F1 5 0 R >> >> >>"),
            Obj("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 6 0 R >>"),
            Obj("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents [7 0 R 8 0 R 9 0 R] >>"),
            Font(),
            PdfStream(string.Empty, TextContent("Hello glacier")),
            PdfStream("/Filter /FlateDecode", big),
            PdfStream("/Filter /FlateDecode", big),
            PdfStream("/Filter /FlateDecode", big)));

        var result = await ReadBounded(path);
        Assert.NotNull(result);
        Assert.Contains("Hello glacier", result.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("Second", result.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PdfBombMislabelledAsImage_UsedAsContents_IsSkipped()
    {
        // The raw pre-scan ignores image streams; the per-page check must still measure a "/Subtype /Image"
        // stream that is used as page contents, because PdfPig decodes contents whatever they claim to be.
        var path = _fx.WriteBytes("image-label.pdf", Pdf(
            Obj("<< /Type /Catalog /Pages 2 0 R >>"),
            Obj("<< /Type /Pages /Kids [3 0 R 4 0 R] /Count 2 /Resources << /Font << /F1 5 0 R >> >> >>"),
            Obj("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 6 0 R >>"),
            Obj("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 7 0 R >>"),
            Font(),
            PdfStream(string.Empty, TextContent("Hello glacier")),
            PdfStream("/Subtype /Image /Width 1 /Height 1 /Filter /FlateDecode",
                ZlibRepeated("BT /F1 12 Tf 40 700 Td ", (byte)' ', OneGigabyte, "(Second page) Tj ET"))));

        var result = await ReadBounded(path);
        Assert.NotNull(result);
        Assert.Contains("Hello glacier", result.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("Second", result.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PdfFormXObjectsOverPageBudget_AreSkipped()
    {
        var form = ZlibRepeated("BT /F1 12 Tf 40 700 Td ", (byte)' ', 20L << 20, "(Form text) Tj ET");
        var path = _fx.WriteBytes("forms.pdf", Pdf(
            Obj("<< /Type /Catalog /Pages 2 0 R >>"),
            Obj("<< /Type /Pages /Kids [3 0 R 4 0 R] /Count 2 >>"),
            Obj("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Resources << /Font << /F1 5 0 R >> >> /Contents 6 0 R >>"),
            Obj("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Resources << /Font << /F1 5 0 R >> /XObject << /Fa 8 0 R /Fb 9 0 R >> >> /Contents 7 0 R >>"),
            Font(),
            PdfStream(string.Empty, TextContent("Hello glacier")),
            PdfStream(string.Empty, Encoding.ASCII.GetBytes("/Fa Do /Fb Do")),
            PdfStream("/Type /XObject /Subtype /Form /BBox [0 0 612 792] /Resources << /Font << /F1 5 0 R >> >> /Filter /FlateDecode", form),
            PdfStream("/Type /XObject /Subtype /Form /BBox [0 0 612 792] /Resources << /Font << /F1 5 0 R >> >> /Filter /FlateDecode", form)));

        var result = await ReadBounded(path);
        Assert.NotNull(result);
        Assert.Contains("Hello glacier", result.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("Form text", result.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PdfWithinLimits_StillExtractsFlateContent()
    {
        var path = _fx.WriteBytes("normal.pdf", Pdf(
            Obj("<< /Type /Catalog /Pages 2 0 R >>"),
            Obj("<< /Type /Pages /Kids [3 0 R] /Count 1 >>"),
            Obj("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Resources << /Font << /F1 4 0 R >> >> /Contents 5 0 R >>"),
            Font(),
            PdfStream("/Filter /FlateDecode", ZlibRepeated("BT /F1 12 Tf 40 750 Td ", (byte)' ', 2L << 20, "(Compressed page) Tj ET"))));

        var result = await ReadBounded(path);
        Assert.NotNull(result);
        Assert.Contains("Compressed page", result.Text, StringComparison.Ordinal);
    }

    // The raw pre-scan is the only guard for object and xref streams (PdfPig decodes them while it opens the
    // file), so it must read the stream dictionary the way PdfPig does, whatever decoys the dictionary holds.

    private static byte[][] OnePageObjects(string text) =>
    [
        Obj("<< /Type /Catalog /Pages 2 0 R >>"),
        Obj("<< /Type /Pages /Kids [3 0 R] /Count 1 >>"),
        Obj("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Resources << /Font << /F1 4 0 R >> >> /Contents 5 0 R >>"),
        Font(),
        PdfStream(string.Empty, TextContent(text)),
    ];

    [Fact]
    public async Task PdfWithXrefStream_WithinLimits_StillExtracts()
    {
        var objects = OnePageObjects("Hello glacier");
        var path = _fx.WriteBytes("xref-stream.pdf", PdfWithXrefStream(
            XrefStreamCore(objects.Length) + " /Filter /FlateDecode /Info (obj) /Pre << /Filter /DCTDecode >>", 0, objects));

        var result = await ReadBounded(path);
        Assert.NotNull(result);
        Assert.Contains("Hello glacier", result.Text, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("{0} /Filter /FlateDecode")]
    [InlineData("{0} /Filter /FlateDecode /Decoy (obj)")] // "obj" in a string used to truncate the dictionary
    [InlineData("{0} /Filter /FlateDecode /Xobj 1")] // ... and in a name
    [InlineData("/Pre << /Filter /DCTDecode >> {0} /Filter /FlateDecode")] // a nested /Filter used to win
    [InlineData("{0} /Subtype /Image /Filter /FlateDecode")] // labelled as an image
    [InlineData("{0} /Filter /FlateDecode /S (9 0 obj << /Subtype /Image %)\n")] // fake header and comment in a string
    [InlineData("{0} /Filter /Fl#61teDecode")] // name escapes
    [InlineData("{0} /Fil#74er /FlateDecode")]
    [InlineData("{0} /Filter /DCTDecode /Filter /FlateDecode")] // duplicate keys: unparseable
    [InlineData("{0} /Filter /FlateDecode % comment\n")] // comment: unparseable
    [InlineData("{0} /Filter 4 0 R")] // indirect filter
    [InlineData("{0} /Filter [/FlateDecode 4 0 R]")]
    public async Task PdfXrefStreamBomb_IsNameOnly(string entries)
    {
        var objects = OnePageObjects("Hello glacier");
        var dictionary = string.Format(System.Globalization.CultureInfo.InvariantCulture, entries, XrefStreamCore(objects.Length));
        var file = PdfWithXrefStream(dictionary, 64L << 20, objects);
        Assert.NotNull(PdfStreamGuard.ScanFile(file, Ct));

        var path = _fx.WriteBytes("xref-bomb.pdf", file);
        var result = await ReadBounded(path);
        Assert.Equal(new ExtractedText(string.Empty, FileKind.Pdf), result);
    }

    [Theory]
    [InlineData("/Type /ObjStm /N 1 /First 4 /Filter /FlateDecode")]
    [InlineData("/Type /ObjStm /N 1 /First 4 /Decoy (obj) /Filter /FlateDecode")]
    [InlineData("/Pre << /Filter /DCTDecode >> /Type /ObjStm /N 1 /First 4 /Filter /FlateDecode")]
    [InlineData("/Type /ObjStm /Subtype /Image /N 1 /First 4 /Filter /FlateDecode")]
    public async Task PdfObjectStreamBomb_IsNameOnly(string entries)
    {
        var file = Pdf([.. OnePageObjects("Hello glacier"), PdfStream(entries, ZlibRepeated("1 0 ", (byte)' ', 64L << 20, "<< >>"))]);
        Assert.NotNull(PdfStreamGuard.ScanFile(file, Ct));

        var path = _fx.WriteBytes("objstm-bomb.pdf", file);
        var result = await ReadBounded(path);
        Assert.Equal(new ExtractedText(string.Empty, FileKind.Pdf), result);
    }

    [Theory]
    [InlineData("/Subtype /Image /Width 8192 /Height 8192 /BitsPerComponent 8 /ColorSpace /DeviceGray /Filter /FlateDecode")]
    [InlineData("/Subtype /Image /Note (obj) /DecodeParms << /Predictor 15 /Columns 8192 >> /Width 8192 /Height 8192 /BitsPerComponent 8 /ColorSpace /DeviceGray /Filter /FlateDecode")]
    public void PdfLargeFlateImage_IsNotMeasured(string entries)
    {
        // Text extraction never decodes image data, so a legitimately large image must not reject the file.
        var file = Pdf([.. OnePageObjects("Hello glacier"), PdfStream(entries, ZlibRepeated(string.Empty, 0x80, 64L << 20, string.Empty))]);
        Assert.Null(PdfStreamGuard.ScanFile(file, Ct));
    }

    [Fact]
    public void InflatedSize_StopsAtCap()
    {
        var data = ZlibRepeated(string.Empty, (byte)' ', OneGigabyte, string.Empty);
        var watch = Stopwatch.StartNew();
        var size = PdfStreamGuard.InflatedSize(data, 0, data.Length, 1L << 20, new byte[64 * 1024]);
        Assert.InRange(size, (1L << 20) + 1, (1L << 20) + (64 * 1024));
        Assert.True(watch.Elapsed < MaxElapsed);
    }

    [Fact]
    public void LzwDecodedSize_CountsWithoutDecoding()
    {
        // 9-bit codes: CLEAR, 'A', 258 ("AA"), 259 ("AAA"), EOD → "A" + "AA" + "AAA" = 6 bytes.
        var bytes = PackCodes(9, 256, 65, 258, 259, 257);
        Assert.Equal(6L, PdfStreamGuard.LzwDecodedSize(bytes, earlyChange: 1, cap: long.MaxValue));
        Assert.Equal((long?)6, PdfStreamGuard.MeasureDecoded(bytes, 0, bytes.Length, ["LZWDecode"], 1, long.MaxValue, new byte[16]));

        static byte[] PackCodes(int width, params int[] codes)
        {
            var bits = new List<bool>();
            foreach (var code in codes)
            {
                for (var i = width - 1; i >= 0; i--)
                {
                    bits.Add(((code >> i) & 1) != 0);
                }
            }

            var result = new byte[(bits.Count + 7) / 8];
            for (var i = 0; i < bits.Count; i++)
            {
                if (bits[i])
                {
                    result[i / 8] |= (byte)(0x80 >> (i % 8));
                }
            }

            return result;
        }
    }

    [Fact]
    public void RunLengthDecodedSize_CountsRuns()
    {
        // Literal run of 3, then 128 copies of 'x', then EOD.
        byte[] data = [2, (byte)'a', (byte)'b', (byte)'c', 129, (byte)'x', 128];
        Assert.Equal(131L, PdfStreamGuard.RunLengthDecodedSize(data, long.MaxValue));
    }

    [Fact]
    public void ChainedExpandingFilters_AreNotMeasurable()
    {
        Assert.Null(PdfStreamGuard.MeasureDecoded([1, 2, 3], 0, 3, ["FlateDecode", "LZWDecode"], 1, long.MaxValue, new byte[16]));
    }

    [Fact]
    public void Ascii85ThenFlate_IsMeasuredThroughTheTextEncoding()
    {
        var flate = ZlibRepeated(string.Empty, (byte)' ', 3L << 20, string.Empty);
        var a85 = Encoding.ASCII.GetBytes(Ascii85(flate) + "~>");
        var size = PdfStreamGuard.MeasureDecoded(a85, 0, a85.Length, ["A85", "Fl"], 1, long.MaxValue, new byte[64 * 1024]);
        Assert.NotNull(size);
        Assert.True(size >= 3L << 20, $"measured {size}");

        static string Ascii85(byte[] data)
        {
            var sb = new StringBuilder();
            var digits = new char[5];
            for (var i = 0; i < data.Length; i += 4)
            {
                var n = Math.Min(4, data.Length - i);
                uint value = 0;
                for (var k = 0; k < 4; k++)
                {
                    value = (value << 8) | (k < n ? data[i + k] : 0u);
                }

                for (var k = 4; k >= 0; k--)
                {
                    digits[k] = (char)('!' + (value % 85));
                    value /= 85;
                }

                sb.Append(digits, 0, n + 1);
            }

            return sb.ToString();
        }
    }

    // ------------------------------------------------------------------ per-file deadline

    [Fact]
    public async Task ExtractorExceedingDeadline_YieldsNameOnlyResult()
    {
        var reader = new DocumentReader([new SpinningExtractor()], perFileTimeout: TimeSpan.FromMilliseconds(200));
        var path = _fx.WriteText("slow.docx", "x", new UTF8Encoding(false));
        var watch = Stopwatch.StartNew();
        var result = await reader.ReadAsync(path, Ct);
        Assert.Equal(new ExtractedText(string.Empty, FileKind.Document), result);
        Assert.True(watch.Elapsed < MaxElapsed, $"took {watch.Elapsed}");
    }

    [Fact]
    public async Task CallerCancellation_StillPropagates_WithDeadline()
    {
        var reader = new DocumentReader([new SpinningExtractor()], perFileTimeout: TimeSpan.FromSeconds(30));
        var path = _fx.WriteText("slow.txt", "x", new UTF8Encoding(false));
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        cts.CancelAfter(TimeSpan.FromMilliseconds(100));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => reader.ReadAsync(path, cts.Token));
    }

    [Fact]
    public async Task ExtractionLimitEscapingAnExtractor_YieldsNameOnlyResult()
    {
        var reader = new DocumentReader([new LimitExtractor()]);
        var path = _fx.WriteText("limited.pdf", "x", new UTF8Encoding(false));
        Assert.Equal(new ExtractedText(string.Empty, FileKind.Pdf), await reader.ReadAsync(path, Ct));
    }

    [Fact]
    public void DefaultDeadline_IsTwentySeconds()
    {
        Assert.Equal(TimeSpan.FromSeconds(20), new DocumentReader().PerFileTimeout);
        Assert.Throws<ArgumentOutOfRangeException>(() => new DocumentReader([], perFileTimeout: TimeSpan.Zero));
    }

    // ------------------------------------------------------------------ bounded streams

    [Fact]
    public void BoundedReadStream_ThrowsPastItsCap()
    {
        using var stream = new BoundedReadStream(new MemoryStream(new byte[10_000]), maxBytes: 4096, budget: null);
        var buffer = new byte[1024];
        for (var i = 0; i < 4; i++)
        {
            Assert.Equal(1024, stream.Read(buffer));
        }

        var ex = Assert.Throws<ExtractionLimitException>(() => stream.Read(buffer));
        Assert.False(ex.FileBudgetExhausted);
    }

    [Fact]
    public void DecompressionBudget_IsSharedAcrossStreams()
    {
        var budget = new DecompressionBudget(3000);
        using var first = new BoundedReadStream(new MemoryStream(new byte[2000]), 10_000, budget);
        using var second = new BoundedReadStream(new MemoryStream(new byte[2000]), 10_000, budget);
        first.CopyTo(Stream.Null);
        var ex = Assert.Throws<ExtractionLimitException>(() => second.CopyTo(Stream.Null));
        Assert.True(ex.FileBudgetExhausted);
    }

    private sealed class SpinningExtractor : ITextExtractor
    {
        public bool CanHandle(string path) => true;

        public Task<ExtractedText?> ExtractAsync(string path, ExtractionLimits limits, CancellationToken cancellationToken)
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                Thread.Sleep(5);
            }
        }
    }

    private sealed class LimitExtractor : ITextExtractor
    {
        public bool CanHandle(string path) => true;

        public Task<ExtractedText?> ExtractAsync(string path, ExtractionLimits limits, CancellationToken cancellationToken) =>
            throw new ExtractionLimitException("test");
    }
}
