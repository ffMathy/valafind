using System.IO.Compression;
using System.Text;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using MimeKit;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Fonts.Standard14Fonts;
using UglyToad.PdfPig.Writer;
using A = DocumentFormat.OpenXml.Drawing;
using P = DocumentFormat.OpenXml.Presentation;
using S = DocumentFormat.OpenXml.Spreadsheet;
using W = DocumentFormat.OpenXml.Wordprocessing;

namespace Valafind.Core.Tests.Extraction;

/// <summary>A temporary directory with programmatic fixture writers for every supported format.</summary>
internal sealed class ExtractionFixtures : IDisposable
{
    public ExtractionFixtures()
    {
        Root = Path.Combine(Path.GetTempPath(), "valafind-extraction-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Root);
    }

    public string Root { get; }

    public string PathOf(string name) => Path.Combine(Root, name);

    public void Dispose()
    {
        try
        {
            Directory.Delete(Root, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    public string WriteText(string name, string content, Encoding encoding)
    {
        var path = PathOf(name);
        File.WriteAllText(path, content, encoding);
        return path;
    }

    public string WriteBytes(string name, byte[] bytes)
    {
        var path = PathOf(name);
        File.WriteAllBytes(path, bytes);
        return path;
    }

    public string WriteDocx(string name)
    {
        var path = PathOf(name);
        using var doc = WordprocessingDocument.Create(path, WordprocessingDocumentType.Document);
        var main = doc.AddMainDocumentPart();
        main.Document = new W.Document(new W.Body(
            new W.Paragraph(
                new W.ParagraphProperties(new W.Tabs(new W.TabStop { Val = W.TabStopValues.Left, Position = 720 })),
                new W.Run(new W.Text("Quarterly report on alpine glaciers"))),
            new W.Paragraph(
                new W.Run(new W.Text("Melt")),
                new W.Run(new W.TabChar()),
                new W.Run(new W.Text("rates") { Space = SpaceProcessingModeValues.Preserve }),
                new W.Run(new W.Break()),
                new W.Run(new W.Text("increased"))),
            new W.Table(new W.TableRow(
                new W.TableCell(new W.Paragraph(new W.Run(new W.Text("Cell A")))),
                new W.TableCell(new W.Paragraph(new W.Run(new W.Text("Cell B")))))),
            new W.Paragraph(new W.Run(new W.DeletedText("removed words")), new W.Run(new W.Text("Final words")))));
        return path;
    }

    public string WritePptx(string name)
    {
        var path = PathOf(name);
        using var doc = PresentationDocument.Create(path, PresentationDocumentType.Presentation);
        var presentationPart = doc.AddPresentationPart();
        var slideIds = new P.SlideIdList();
        presentationPart.Presentation = new P.Presentation(slideIds);

        uint id = 256;
        foreach (var (title, body, notes) in new[]
                 {
                     ("Welcome slide", "Agenda for the summit", "Remember to greet everyone"),
                     ("Second slide", "Budget figures", (string?)null),
                 })
        {
            var slidePart = presentationPart.AddNewPart<SlidePart>();
            slidePart.Slide = new P.Slide(new P.CommonSlideData(new P.ShapeTree(
                Shape(title),
                Shape(body))));
            if (notes is not null)
            {
                var notesPart = slidePart.AddNewPart<NotesSlidePart>();
                notesPart.NotesSlide = new P.NotesSlide(new P.CommonSlideData(new P.ShapeTree(Shape(notes))));
            }

            slideIds.Append(new P.SlideId { Id = id++, RelationshipId = presentationPart.GetIdOfPart(slidePart) });
        }

        return path;

        static P.Shape Shape(string text) => new(
            new P.TextBody(new A.BodyProperties(), new A.Paragraph(new A.Run(new A.Text(text)))));
    }

    public string WriteXlsx(string name)
    {
        var path = PathOf(name);
        using var doc = SpreadsheetDocument.Create(path, SpreadsheetDocumentType.Workbook);
        var workbookPart = doc.AddWorkbookPart();
        workbookPart.Workbook = new S.Workbook();
        var sst = workbookPart.AddNewPart<SharedStringTablePart>();
        sst.SharedStringTable = new S.SharedStringTable(
            new S.SharedStringItem(new S.Text("Revenue")),
            new S.SharedStringItem(new S.Text("Costs")),
            new S.SharedStringItem(new S.Text("Glacier tours")));

        var sheet1 = workbookPart.AddNewPart<WorksheetPart>();
        sheet1.Worksheet = new S.Worksheet(new S.SheetData(
            new S.Row(
                new S.Cell { CellReference = "A1", DataType = S.CellValues.SharedString, CellValue = new S.CellValue("0") },
                new S.Cell { CellReference = "B1", DataType = S.CellValues.SharedString, CellValue = new S.CellValue("1") },
                new S.Cell { CellReference = "C1", DataType = S.CellValues.InlineString, InlineString = new S.InlineString(new S.Text("inline note")) }),
            new S.Row(
                new S.Cell { CellReference = "A2", CellValue = new S.CellValue("42") },
                new S.Cell { CellReference = "B2", DataType = S.CellValues.Boolean, CellValue = new S.CellValue("1") },
                new S.Cell { CellReference = "C2", DataType = S.CellValues.SharedString, CellValue = new S.CellValue("2") })));

        var sheet2 = workbookPart.AddNewPart<WorksheetPart>();
        sheet2.Worksheet = new S.Worksheet(new S.SheetData(
            new S.Row(new S.Cell { CellReference = "A1", DataType = S.CellValues.String, CellValue = new S.CellValue("second sheet value") })));

        workbookPart.Workbook.AppendChild(new S.Sheets(
            new S.Sheet { Name = "Budget 2026", SheetId = 1, Id = workbookPart.GetIdOfPart(sheet1) },
            new S.Sheet { Name = "Notes", SheetId = 2, Id = workbookPart.GetIdOfPart(sheet2) }));
        return path;
    }

    public string WritePdf(string name, params string[] pages)
    {
        var builder = new PdfDocumentBuilder();
        var font = builder.AddStandard14Font(Standard14Font.Helvetica);
        foreach (var text in pages)
        {
            var page = builder.AddPage(PageSize.A4);
            var y = 750.0;
            foreach (var line in text.Split('\n'))
            {
                page.AddText(line, 12, new PdfPoint(40, y), font);
                y -= 16;
            }
        }

        return WriteBytes(name, builder.Build());
    }

    public static MimeMessage CreateMessage(string subject, string body, bool html = false)
    {
        var message = new MimeMessage();
        message.From.Add(new MailboxAddress("Alice Example", "alice@example.com"));
        message.To.Add(new MailboxAddress("Bob", "bob@example.com"));
        message.Subject = subject;
        message.Date = new DateTimeOffset(2026, 5, 1, 10, 30, 0, TimeSpan.Zero);
        var builder = new BodyBuilder();
        if (html)
        {
            builder.HtmlBody = body;
        }
        else
        {
            builder.TextBody = body;
        }

        message.Body = builder.ToMessageBody();
        return message;
    }

    public string WriteEml(string name, MimeMessage message)
    {
        var path = PathOf(name);
        message.WriteTo(path);
        return path;
    }

    public string WriteMbox(string name, params MimeMessage[] messages)
    {
        var path = PathOf(name);
        using var stream = File.Create(path);
        foreach (var message in messages)
        {
            var marker = Encoding.ASCII.GetBytes("From alice@example.com Fri May  1 10:30:00 2026\n");
            stream.Write(marker);
            message.WriteTo(stream);
            stream.Write("\n"u8);
        }

        return path;
    }

    public string WriteZip(string name, params (string Entry, string Content)[] entries)
    {
        var path = PathOf(name);
        using var stream = File.Create(path);
        using var zip = new ZipArchive(stream, ZipArchiveMode.Create);
        foreach (var (entryName, content) in entries)
        {
            var entry = zip.CreateEntry(entryName, entryName == "mimetype" ? CompressionLevel.NoCompression : CompressionLevel.Optimal);
            using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false));
            writer.Write(content);
        }

        return path;
    }

    public string WriteOdt(string name) => WriteZip(name,
        ("mimetype", "application/vnd.oasis.opendocument.text"),
        ("content.xml", """
            <?xml version="1.0" encoding="UTF-8"?>
            <office:document-content xmlns:office="urn:oasis:names:tc:opendocument:xmlns:office:1.0"
                xmlns:text="urn:oasis:names:tc:opendocument:xmlns:text:1.0"
                xmlns:table="urn:oasis:names:tc:opendocument:xmlns:table:1.0">
            <office:body><office:text><text:sequence-decls><text:sequence-decl text:name="Illustration"/></text:sequence-decls><text:h text:outline-level="1">Field notes</text:h><text:p>Snow<text:s text:c="2"/>depth<text:tab/>measured</text:p><text:p>Second <text:span>paragraph</text:span> here</text:p><table:table table:name="Data"><table:table-row><table:table-cell><text:p>alpha</text:p></table:table-cell><table:table-cell/><table:table-cell><text:p>beta</text:p></table:table-cell></table:table-row></table:table></office:text></office:body>
            </office:document-content>
            """));

    public string WriteEpub(string name) => WriteZip(name,
        ("mimetype", "application/epub+zip"),
        ("META-INF/container.xml", """
            <?xml version="1.0"?>
            <container version="1.0" xmlns="urn:oasis:names:tc:opendocument:xmlns:container">
              <rootfiles><rootfile full-path="OEBPS/content.opf" media-type="application/oebps-package+xml"/></rootfiles>
            </container>
            """),
        ("OEBPS/content.opf", """
            <?xml version="1.0"?>
            <package xmlns="http://www.idpf.org/2007/opf" version="3.0">
              <manifest>
                <item id="c1" href="text/z-first.xhtml" media-type="application/xhtml+xml"/>
                <item id="c2" href="text/a-second.xhtml" media-type="application/xhtml+xml"/>
                <item id="css" href="style.css" media-type="text/css"/>
              </manifest>
              <spine><itemref idref="c1"/><itemref idref="c2"/></spine>
            </package>
            """),
        ("OEBPS/text/z-first.xhtml", "<html xmlns=\"http://www.w3.org/1999/xhtml\"><head><title>One</title><style>p{}</style></head><body><h1>Chapter one</h1><p>It was a cold &amp; snowy night.</p></body></html>"),
        ("OEBPS/text/a-second.xhtml", "<html xmlns=\"http://www.w3.org/1999/xhtml\"><body><h1>Chapter two</h1><p>Morning came.</p></body></html>"));

    public string WriteNotebook(string name) => WriteText(name, """
        {
          "cells": [
            { "cell_type": "markdown", "metadata": {}, "source": ["# Glacier analysis\n", "Load the data."] },
            { "cell_type": "code", "execution_count": 1, "metadata": {},
              "outputs": [ { "output_type": "stream", "name": "stdout", "text": ["SECRET OUTPUT\n"] } ],
              "source": "import pandas as pd\ndf = pd.read_csv('melt.csv')" }
          ],
          "metadata": {}, "nbformat": 4, "nbformat_minor": 5
        }
        """, new UTF8Encoding(false));

    public string WriteRtf(string name) => WriteText(name,
        "{\\rtf1\\ansi\\ansicpg1252\\deff0{\\fonttbl{\\f0\\fswiss Helvetica;}}{\\colortbl;\\red255\\green0\\blue0;}" +
        "{\\*\\generator Test 1.0;}{\\info{\\title Hidden title}}" +
        "\\pard\\f0\\fs24 Caf\\'e9 menu\\par " +
        "{\\b Bold} text with \\{braces\\} and a backslash \\\\.\\par\r\n" +
        "Unicode \\u8364?  euro and \\uc2\\u20320\\'c4\\'e3 done.\\par" +
        "{\\field{\\*\\fldinst HYPERLINK \"http://example.com\"}{\\fldrslt link text}}" +
        "}", Encoding.ASCII);
}
