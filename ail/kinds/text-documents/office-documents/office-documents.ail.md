# Office documents, e-books and PDFs

Word, Excel and PowerPoint files, their OpenDocument counterparts, EPUB books, RTF and PDF files are found by their
text, read in the order a reader meets it.

```ail
node OfficeDocuments : flow   level: experience
  why   Letters, reports, spreadsheets, slides and books are where people keep what they will look for later, and
        they rarely remember a file name like "scan_0042.pdf".   <- TextDocuments
  what
    Word (.docx and its macro and template variants): the body with its tables, then footnotes and endnotes;
      deleted tracked changes are left out.
    Excel (.xlsx, .xlsm, .xltx): each sheet's name, then its cell values row by row.
    PowerPoint (.pptx, .pptm, .potx, .ppsx): the text of each slide in slide order, then the speaker notes.
    OpenDocument text, spreadsheets and presentations (.odt, .ods, .odp and their templates): paragraphs and
      headings, table cells row by row, slides and sheets apart.
    EPUB books: the chapters in reading order, without their styles.
    RTF: the text, without fonts, colours, pictures, headers and footers.
    PDF: the text of each page in order, up to 2,000 pages; a scanned PDF without text, or one with a password, is
      found by its name only.
    Scenario: a Word document is found by its text
      Given a Word document "Beekeeping log <code>" that says "The beekeeper harvested honey from twelve hives this autumn."
      When the user searches for "collecting honey from bees"
      Then the document is listed with its passage, code included
    Scenario: an Excel workbook is found by its sheet names and cells
      Given a workbook with a sheet "Telescope budget" whose cells say "Mirror grinding <code>" and "Observatory
        telescope repairs cost 900 kroner"
      When the user searches for "what the telescope repairs cost"
      Then the workbook is listed with its passage
    Scenario: a PowerPoint presentation is found by its slides and speaker notes
      Given slides "Quarterly review" and "Agenda", with the notes "Remind everyone <code> to bring ash masks for the
        volcano field trip"
      When the user searches for "volcano field trip"
      Then the presentation is listed with a passage that ends in its notes
    Scenario: an OpenDocument text is found by its text
      Given an OpenDocument text "Recipe <code>" that says "Sourdough bread with a long cold fermentation."
      When the user searches for "baking bread"
      Then the document is listed with its passage
    Scenario: an EPUB book is found by its chapters
      Given a book whose chapters say "Chapter one: the pirate ship sailed into the harbour at dawn." and "Chapter two
        <code>: the treasure map."
      When the user searches for "pirates looking for treasure"
      Then the book is listed with its passage
    Scenario: an RTF document is found by its text
      Given an RTF document "Pattern <code>" that says "How to knit a wool scarf for the winter."
      When the user searches for "knitting a scarf"
      Then the document is listed with its passage
    Scenario: a PDF is found by its text
      Given a PDF "Workshop <code>" that says "How to change a flat bicycle tyre."
      When the user searches for "fixing a punctured bike wheel"
      Then the PDF is listed with its passage
  how
    decide read the files directly, part by part, with hard limits on their size and count
      because a hostile file (a zip, XML or PDF bomb) must cost little and never stop indexing (PR #1, "Hardened against hostile input")
    decide speaker notes after all the slides, footnotes after the body
      because the main text should fill the passages first when a document is long (OpenXmlExtractor)
    decide the older binary formats (.doc, .xls, .ppt) are not read; see the files found by name only
      because there is no reader for them yet (MetadataOnlyExtractor)
  uses      FormatRules
  sources
    README.md, "What gets indexed"; PR #1, Extraction & chunking
    driven on Linux (Xvfb, openbox) 2026-11-25: one file of each format, made with checks/e2e/kinds/make-file.py, saved and searched
    src/Valafind.Core/Extraction/OpenXmlExtractor.cs, OpenDocumentExtractor.cs, EpubExtractor.cs, RtfExtractor.cs, PdfExtractor.cs
    tests/Valafind.Core.Tests/Extraction/DocumentReaderTests.cs, Docx_…, Pptx_…, Xlsx_…, Pdf_…, Odt_…, Epub_…, Rtf_…
```
