using System.Text;
using Valafind.Core.Abstractions;
using Valafind.Core.Extraction;
using Xunit;

namespace Valafind.Core.Tests.Extraction;

public sealed class DocumentReaderTests : IDisposable
{
    private readonly ExtractionFixtures _fx = new();
    private readonly DocumentReader _reader = new();

    public void Dispose() => _fx.Dispose();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private async Task<ExtractedText> ReadOk(string path)
    {
        var result = await _reader.ReadAsync(path, Ct);
        Assert.NotNull(result);
        return result;
    }

    [Fact]
    public async Task Utf8Text_WithBom()
    {
        var path = _fx.WriteText("notes.txt", "Héllo wörld — ünïcode ✓", new UTF8Encoding(true));
        var result = await ReadOk(path);
        Assert.Equal("Héllo wörld — ünïcode ✓", result.Text);
        Assert.Equal(FileKind.Text, result.Kind);
    }

    [Fact]
    public async Task Utf16LittleAndBigEndian_WithBom()
    {
        var le = await ReadOk(_fx.WriteText("le.txt", "Grüße aus Zürich", new UnicodeEncoding(false, true)));
        var be = await ReadOk(_fx.WriteText("be.md", "Grüße aus Zürich", new UnicodeEncoding(true, true)));
        Assert.Equal("Grüße aus Zürich", le.Text);
        Assert.Equal("Grüße aus Zürich", be.Text);
    }

    [Fact]
    public async Task Utf16_WithoutBom_IsDetected()
    {
        var path = _fx.WriteBytes("nobom.log", Encoding.Unicode.GetBytes("plain ascii log line\nsecond line"));
        var result = await ReadOk(path);
        Assert.Equal("plain ascii log line\nsecond line", result.Text);
    }

    [Fact]
    public async Task Windows1252_FallsBackFromInvalidUtf8()
    {
        // “Café” – €5 in Windows-1252: smart quotes 0x93/0x94, é 0xE9, en dash 0x96, euro 0x80.
        byte[] bytes = [0x93, (byte)'C', (byte)'a', (byte)'f', 0xE9, 0x94, (byte)' ', 0x96, (byte)' ', 0x80, (byte)'5'];
        var result = await ReadOk(_fx.WriteBytes("legacy.txt", bytes));
        Assert.Equal("\u201CCaf\u00E9\u201D \u2013 \u20AC5", result.Text);
    }

    [Fact]
    public async Task Code_And_Csv_AreReadAsText()
    {
        var code = await ReadOk(_fx.WriteText("Program.cs", "class C { }", new UTF8Encoding(false)));
        Assert.Equal(FileKind.Code, code.Kind);
        Assert.Equal("class C { }", code.Text);

        var csv = await ReadOk(_fx.WriteText("data.csv", "a,b\n1,2", new UTF8Encoding(false)));
        Assert.Equal(FileKind.Spreadsheet, csv.Kind);
        Assert.Equal("a,b\n1,2", csv.Text);
    }

    [Fact]
    public async Task Html_IsConvertedToText()
    {
        var html = """
            <!DOCTYPE html><html><head><title>Trip</title><script>var x = "<p>not text</p>";</script>
            <style>p { color: red }</style></head>
            <body><!-- comment --><h1>Alpine&nbsp;trip</h1><p>Fish &amp; chips &lt;3 &#8364;5</p>
            <p>Line<br/>break</p><table><tr><td>a</td><td>b</td></tr></table><p>1 < 2</p></body></html>
            """;
        var result = await ReadOk(_fx.WriteText("page.html", html, new UTF8Encoding(false)));
        Assert.DoesNotContain("not text", result.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("color", result.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("comment", result.Text, StringComparison.Ordinal);
        Assert.Contains("Trip", result.Text, StringComparison.Ordinal);
        Assert.Contains("Alpine trip", result.Text, StringComparison.Ordinal);
        Assert.Contains("Fish & chips <3 \u20AC5", result.Text, StringComparison.Ordinal);
        Assert.Contains("Line\nbreak", result.Text, StringComparison.Ordinal);
        Assert.Contains("a b", result.Text, StringComparison.Ordinal);
        Assert.Contains("1 < 2", result.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task UnknownExtension_SniffedText_IsRead()
    {
        var result = await ReadOk(_fx.WriteText("README", "Just a readme\nwith lines", new UTF8Encoding(false)));
        Assert.Equal(FileKind.Other, result.Kind);
        Assert.Equal("Just a readme\nwith lines", result.Text);
    }

    [Fact]
    public async Task BinaryGarbage_UnknownExtension_ReturnsNull()
    {
        var bytes = new byte[4096];
        new Random(42).NextBytes(bytes);
        bytes[10] = 0;
        Assert.Null(await _reader.ReadAsync(_fx.WriteBytes("blob.bin", bytes), Ct));
        Assert.Null(await _reader.ReadAsync(_fx.WriteBytes("noextension", bytes), Ct));
    }

    [Fact]
    public async Task MissingFile_And_Directory_ReturnNull()
    {
        Assert.Null(await _reader.ReadAsync(_fx.PathOf("does-not-exist.txt"), Ct));
        Directory.CreateDirectory(_fx.PathOf("folder.txt"));
        Assert.Null(await _reader.ReadAsync(_fx.PathOf("folder.txt"), Ct));
        Assert.Null(await _reader.ReadAsync("", Ct));
    }

    [Fact]
    public async Task OversizedFile_IsMetadataOnly()
    {
        var reader = new DocumentReader(DocumentReader.CreateDefaultExtractors(), new ExtractionLimits(MaxFileBytes: 100));
        var path = _fx.WriteText("big.txt", new string('x', 500), new UTF8Encoding(false));
        var result = await reader.ReadAsync(path, Ct);
        Assert.NotNull(result);
        Assert.Equal(string.Empty, result.Text);
        Assert.Equal(FileKind.Text, result.Kind);

        var pdf = await reader.ReadAsync(_fx.WritePdf("big.pdf", "page text"), Ct);
        Assert.NotNull(pdf);
        Assert.Equal(new ExtractedText(string.Empty, FileKind.Pdf), pdf);
    }

    [Fact]
    public async Task MaxChars_TruncatesStreamingRead()
    {
        var reader = new DocumentReader(DocumentReader.CreateDefaultExtractors(), new ExtractionLimits(MaxChars: 1000));
        var content = string.Concat(Enumerable.Repeat("0123456789", 100_000)); // 1M chars
        var result = await reader.ReadAsync(_fx.WriteText("long.log", content, new UTF8Encoding(false)), Ct);
        Assert.NotNull(result);
        Assert.Equal(1000, result.Text.Length);
        Assert.Equal(content[..1000], result.Text);
    }

    [Fact]
    public async Task MaxChars_NeverSplitsSurrogatePair()
    {
        var reader = new DocumentReader(DocumentReader.CreateDefaultExtractors(), new ExtractionLimits(MaxChars: 7));
        var result = await reader.ReadAsync(_fx.WriteText("emoji.txt", "abcdef\U0001F600xyz", new UTF8Encoding(false)), Ct);
        Assert.NotNull(result);
        Assert.Equal("abcdef", result.Text);
    }

    [Fact]
    public async Task FileOpenForWritingByAnotherHandle_IsStillReadable()
    {
        var path = _fx.WriteText("inuse.txt", "being edited", new UTF8Encoding(false));
        using var writer = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete);
        var result = await ReadOk(path);
        Assert.Equal("being edited", result.Text);
    }

    [Fact]
    public async Task ExclusivelyLockedFile_ReturnsNull()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Skip("Mandatory file sharing locks only exist on Windows.");
        }

        var path = _fx.WriteText("locked.txt", "secret", new UTF8Encoding(false));
        using var locker = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        Assert.Null(await _reader.ReadAsync(path, Ct));
    }

    [Fact]
    public async Task Cancellation_Propagates()
    {
        var path = _fx.WriteText("c.txt", "text", new UTF8Encoding(false));
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => _reader.ReadAsync(path, cts.Token));
    }

    [Fact]
    public async Task ExtractorThrowing_IsCaught_ButCancellationIsNot()
    {
        var reader = new DocumentReader([new ThrowingExtractor(new InvalidOperationException("boom"))]);
        var path = _fx.WriteText("x.txt", "text", new UTF8Encoding(false));
        Assert.Null(await reader.ReadAsync(path, Ct));

        // An OperationCanceledException not caused by the caller's token is treated as a failure.
        var stray = new DocumentReader([new ThrowingExtractor(new OperationCanceledException())]);
        Assert.Null(await stray.ReadAsync(path, Ct));
    }

    [Fact]
    public async Task Docx_ExtractsParagraphsTablesAndBreaks()
    {
        var result = await ReadOk(_fx.WriteDocx("report.docx"));
        Assert.Equal(FileKind.Document, result.Kind);
        Assert.Equal("Quarterly report on alpine glaciers\nMelt\trates\nincreased\nCell A\nCell B\nFinal words", result.Text);
    }

    [Fact]
    public async Task Pptx_ExtractsSlidesInOrderThenNotes()
    {
        var result = await ReadOk(_fx.WritePptx("deck.pptx"));
        Assert.Equal(FileKind.Presentation, result.Kind);
        Assert.Equal("Welcome slide\nAgenda for the summit\n\nSecond slide\nBudget figures\n\nRemember to greet everyone", result.Text);
    }

    [Fact]
    public async Task Xlsx_ExtractsSheetsWithNamesAndCellValues()
    {
        var result = await ReadOk(_fx.WriteXlsx("budget.xlsx"));
        Assert.Equal(FileKind.Spreadsheet, result.Kind);
        Assert.Equal("Budget 2026\nRevenue\tCosts\tinline note\n42\tTRUE\tGlacier tours\n\nNotes\nsecond sheet value", result.Text);
    }

    [Fact]
    public async Task Pdf_ExtractsPagesSeparatedByBlankLines()
    {
        var result = await ReadOk(_fx.WritePdf("doc.pdf", "Hello glacier world", "Second page text"));
        Assert.Equal(FileKind.Pdf, result.Kind);
        var pages = result.Text.Split("\n\n");
        Assert.Equal(2, pages.Length);
        Assert.Contains("Hello glacier world", pages[0], StringComparison.Ordinal);
        Assert.Contains("Second page text", pages[1], StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("corrupt.pdf")]
    [InlineData("corrupt.docx")]
    [InlineData("corrupt.xlsx")]
    [InlineData("corrupt.pptx")]
    [InlineData("corrupt.odt")]
    [InlineData("corrupt.epub")]
    [InlineData("corrupt.ipynb")]
    [InlineData("corrupt.rtf")]
    public async Task CorruptFiles_ReturnNull(string name)
    {
        var bytes = new byte[2048];
        new Random(name.Length).NextBytes(bytes);
        Assert.Null(await _reader.ReadAsync(_fx.WriteBytes(name, bytes), Ct));
    }

    [Fact]
    public async Task TruncatedPdf_ReturnsNullOrPartialText_WithoutThrowing()
    {
        var full = await File.ReadAllBytesAsync(_fx.WritePdf("full.pdf", "Alpha page", "Beta page"), Ct);
        var path = _fx.WriteBytes("truncated.pdf", full[..(full.Length / 2)]);
        var result = await _reader.ReadAsync(path, Ct); // must not throw
        Assert.True(result is null || result.Kind == FileKind.Pdf);
    }

    [Fact]
    public async Task Eml_HeadersAndPlainBody()
    {
        var path = _fx.WriteEml("hike.eml", ExtractionFixtures.CreateMessage("Hiking plans", "Let's meet at the trailhead at 8."));
        var result = await ReadOk(path);
        Assert.Equal(FileKind.Mail, result.Kind);
        Assert.Equal(
            "Subject: Hiking plans\nFrom: Alice Example <alice@example.com>\nTo: Bob <bob@example.com>\n" +
            "Date: 2026-05-01 10:30 +00:00\n\nLet's meet at the trailhead at 8.",
            result.Text);
    }

    [Fact]
    public async Task Eml_HtmlOnlyBody_IsConverted()
    {
        var message = ExtractionFixtures.CreateMessage("Newsletter", "<p>Hello <b>world</b></p><script>tracking()</script>", html: true);
        var result = await ReadOk(_fx.WriteEml("news.eml", message));
        Assert.EndsWith("Hello world", result.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("tracking", result.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Mbox_ConcatenatesMessages()
    {
        var path = _fx.WriteMbox("archive.mbox",
            ExtractionFixtures.CreateMessage("First", "Body one"),
            ExtractionFixtures.CreateMessage("Second", "Body two"),
            ExtractionFixtures.CreateMessage("Third", "Body three"));
        var result = await ReadOk(path);
        Assert.Equal(FileKind.Mail, result.Kind);
        Assert.Contains("Subject: First", result.Text, StringComparison.Ordinal);
        Assert.Contains("Body two", result.Text, StringComparison.Ordinal);
        Assert.Contains("Subject: Third", result.Text, StringComparison.Ordinal);
        Assert.True(result.Text.IndexOf("Body one", StringComparison.Ordinal) < result.Text.IndexOf("Body three", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Msg_IsMetadataOnly()
    {
        var result = await ReadOk(_fx.WriteBytes("outlook.msg", [0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1]));
        Assert.Equal(new ExtractedText(string.Empty, FileKind.Mail), result);
    }

    [Fact]
    public async Task Notebook_SourcesWithoutOutputs()
    {
        var result = await ReadOk(_fx.WriteNotebook("analysis.ipynb"));
        Assert.Equal(FileKind.Notebook, result.Kind);
        Assert.Equal("# Glacier analysis\nLoad the data.\n\nimport pandas as pd\ndf = pd.read_csv('melt.csv')", result.Text);
        Assert.DoesNotContain("SECRET", result.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Odt_ParagraphsSpacesTabsAndTables()
    {
        var result = await ReadOk(_fx.WriteOdt("notes.odt"));
        Assert.Equal(FileKind.Document, result.Kind);
        Assert.Equal("Field notes\nSnow  depth\tmeasured\nSecond paragraph here\n\nData\nalpha\tbeta", result.Text);
    }

    [Fact]
    public async Task Epub_FollowsSpineOrder()
    {
        var result = await ReadOk(_fx.WriteEpub("book.epub"));
        Assert.Equal(FileKind.Document, result.Kind);
        Assert.DoesNotContain("p{}", result.Text, StringComparison.Ordinal);
        var one = result.Text.IndexOf("Chapter one", StringComparison.Ordinal);
        var two = result.Text.IndexOf("Chapter two", StringComparison.Ordinal);
        Assert.True(one >= 0 && two > one, result.Text);
        Assert.Contains("It was a cold & snowy night.", result.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Rtf_StripsControlWordsAndDecodesEscapes()
    {
        var result = await ReadOk(_fx.WriteRtf("letter.rtf"));
        Assert.Equal(FileKind.Document, result.Kind);
        Assert.Equal(
            "Caf\u00E9 menu\nBold text with {braces} and a backslash \\.\nUnicode \u20AC  euro and \u4F60 done.\nlink text",
            result.Text);
    }

    [Theory]
    [InlineData("photo.jpg", FileKind.Image)]
    [InlineData("song.mp3", FileKind.Audio)]
    [InlineData("clip.mp4", FileKind.Video)]
    [InlineData("backup.zip", FileKind.Archive)]
    [InlineData("legacy.doc", FileKind.Document)]
    [InlineData("legacy.xls", FileKind.Spreadsheet)]
    [InlineData("deck.key", FileKind.Presentation)]
    public async Task MediaAndLegacyFormats_AreMetadataOnly(string name, FileKind kind)
    {
        var bytes = new byte[256];
        new Random(1).NextBytes(bytes);
        var result = await ReadOk(_fx.WriteBytes(name, bytes));
        Assert.Equal(new ExtractedText(string.Empty, kind), result);
    }

    [Fact]
    public async Task CustomExtractorOrder_FirstMatchWins()
    {
        var reader = new DocumentReader([new MetadataOnlyExtractor(), new PlainTextExtractor()]);
        var path = _fx.WriteText("a.txt", "hello", new UTF8Encoding(false));
        var result = await reader.ReadAsync(path, Ct);
        Assert.NotNull(result);
        Assert.Equal("hello", result.Text); // MetadataOnly does not claim .txt

        var none = new DocumentReader([]);
        Assert.Null(await none.ReadAsync(path, Ct));
    }

    private sealed class ThrowingExtractor(Exception exception) : ITextExtractor
    {
        public bool CanHandle(string path) => true;

        public Task<ExtractedText?> ExtractAsync(string path, ExtractionLimits limits, CancellationToken cancellationToken) =>
            throw exception;
    }
}
