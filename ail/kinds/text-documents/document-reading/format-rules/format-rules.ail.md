# The text each format gives

Exactly which text each reader takes from a file, in which order and with which separators, traced from Valafind's
own fixtures.

```ail
node FormatRules : fn   level: system
  why   The passages a document is cut into, the excerpt a result shows and the preview all start from this text;
        the same file must give the same text on every platform.   <- DocumentReading
  what
    pdf (.pdf): each page's text in content order, pages a blank line apart; at most 2,000 pages; a page that cannot
      be parsed, or whose streams decode to more than the page budget, is skipped; a password-protected PDF => nothing;
      a PDF holding a decompression bomb => empty text
    officeOpenXml: Word (.docx, .docm, .dotx, .dotm) the body, paragraphs and table cells on their own lines, a tab
      for a tab and a line for a break, deleted text left out, then footnotes and endnotes; PowerPoint (.pptx, .pptm,
      .potx, .ppsx) each slide's text in the order of the slide list, slides a blank line apart, then every slide's
      notes; Excel (.xlsx, .xlsm, .xltx) per sheet in workbook order, a blank line apart: its name, then each row's
      cell values joined by tabs, booleans as TRUE and FALSE; chart sheets are skipped
    officeOpenXml limits: 5,000 rows per sheet, 16,384 cells per row, 256 sheets, 5,000 slides, 1,000,000 shared
      strings, 32,768 characters per cell
    openDocument (.odt, .ott, .ods, .ots, .odp, .otp): content.xml only; paragraphs and headings each on a line, a run
      of n spaces as n spaces, a tab as a tab, table cells joined by tabs one row per line, tables, sheets and slides
      a blank line apart
    epub (.epub): the documents of the reading order (spine), each once, converted like a web page; when the package
      description is missing or broken, every .xhtml, .html and .htm entry in name order; at most 2,000 documents
      and 20,000 manifest items
    mail (.eml, .mbox): "Subject: …", "From: …", "To: …" and "Date: yyyy-MM-dd HH:mm +zz:zz" lines (each only when
      present), a blank line, then the plain-text body trimmed, or else the web-page body as text; .mbox: its messages
      a blank line apart, at most 500
    notebook (.ipynb): the sources of all cells in order, a blank line apart, from nbformat 4 "cells" or nbformat 3
      "worksheets"; outputs are never read
    rtf (.rtf): the text with \par as a line break and \tab as a tab; font, colour and style tables, pictures,
      objects, headers, footers, the info block and field instructions are skipped; \'hh in the document's code page,
      \uN as that character with its fallback skipped
    plainText: text, Markdown, logs, code, web pages, JSON, XML, YAML, TOML, INI, CSV and TSV, and files of no known
      kind that look like text (see sniff rules); a web page or .xhtml is read up to 4 × maxChars of markup, then
      turned into text: script, style, noscript, template, svg, math, canvas, iframe and object content and comments
      dropped, block elements as line breaks, cells a space apart, entities decoded, white space collapsed;
      a NUL character becomes a space; .csv and .tsv are of kind "spreadsheet"
    the fixtures below are Valafind's own (tests/…/Extraction/ExtractionFixtures.cs): report.docx holds "Quarterly report
      on alpine glaciers", "Melt", a tab, "rates", a break, "increased", a table "Cell A" "Cell B", deleted "removed words"
      and "Final words"; deck.pptx two slides ("Welcome slide", "Agenda for the summit") and ("Second slide", "Budget
      figures"), the first with notes "Remember to greet everyone"; budget.xlsx sheets "Budget 2026" (Revenue, Costs,
      "inline note" / 42, TRUE, Glacier tours) and "Notes" ("second sheet value"); hike.eml "Hiking plans" from Alice
      Example to Bob, sent 2026-05-01 10:30 UTC; analysis.ipynb a markdown and a code cell whose output is "SECRET OUTPUT"
    examples
      read("report.docx").text => "Quarterly report on alpine glaciers\nMelt\trates\nincreased\nCell A\nCell B\nFinal words"
      read("deck.pptx").text => "Welcome slide\nAgenda for the summit\n\nSecond slide\nBudget figures\n\nRemember to greet everyone"
      read("budget.xlsx").text => "Budget 2026\nRevenue\tCosts\tinline note\n42\tTRUE\tGlacier tours\n\nNotes\nsecond sheet value"
      read("notes.odt").text => "Field notes\nSnow  depth\tmeasured\nSecond paragraph here\n\nData\nalpha\tbeta"
      read("hike.eml").text => "Subject: Hiking plans\nFrom: Alice Example <alice@example.com>\nTo: Bob <bob@example.com>\nDate: 2026-05-01 10:30 +00:00\n\nLet's meet at the trailhead at 8."
      read("analysis.ipynb").text => "# Glacier analysis\nLoad the data.\n\nimport pandas as pd\ndf = pd.read_csv('melt.csv')"
      read("letter.rtf").text => "Café menu\nBold text with {braces} and a backslash \\.\nUnicode €  euro and 你 done.\nlink text"
      read("outlook.msg") => { text: "", kind: "mail" }
      read("page.html") holds "Alpine trip", "Fish & chips <3 €5" and "Line\nbreak", not its script, style or comment => true
      read("two-pages.pdf").text => "Hello glacier world" and "Second page text", a blank line apart
      read("c.cs") => { text: "class C { }", kind: "code" };  read("t.csv") => { text: "a,b\n1,2", kind: "spreadsheet" }
  sources
    src/Valafind.Core/Extraction/PdfExtractor.cs, PdfStreamGuard.cs, OpenXmlExtractor.cs, OpenDocumentExtractor.cs, EpubExtractor.cs
    src/Valafind.Core/Extraction/MailExtractor.cs, NotebookExtractor.cs, RtfExtractor.cs, PlainTextExtractor.cs, HtmlText.cs
    tests/Valafind.Core.Tests/Extraction/DocumentReaderTests.cs, ExtractionFixtures.cs, HostileInputTests.cs
```
