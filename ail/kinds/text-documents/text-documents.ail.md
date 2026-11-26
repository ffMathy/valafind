# Text documents

Text, Markdown, code, web pages and data files are read as text. Office files, e-books, PDFs, e-mail and notebooks
have children of their own. Whatever the format, only the first part of a long document is searched.

```ail
node TextDocuments : flow   level: experience
  why   Most of what people look for is something they wrote or read, so every common document format must be
        found by its words, not only by its name.   <- KindsOfFiles
  what
    Text, Markdown, logs, code, web pages, JSON, XML, YAML, CSV and similar files are read as text.
    A web page is read without its markup: scripts, styles and comments are left out.
    Each document gives up to its first 32,000 characters, cut into passages of about 320 tokens that overlap a
      little; at most 12 passages are kept, so in a long text only roughly the first 15,000 to 20,000 characters are
      found by meaning.
    A file larger than 64 MB, or one whose text takes longer than 20 seconds to read, is found by its name only.
    A damaged document of a known kind is found by its name only.
    The result shows the passage that matched under the file's name, and the details show it again under "Why it
      matched", with a preview of the document's text.
    Scenario: a text file is found by what it says, with the passage that matched
      Given a text file with a name that means nothing, which says "Logbook <code>: the lighthouse keeper repaired the
        foghorn before the winter storm arrived."
      When the user searches for "who looks after the lighthouse"
      Then the file is listed with that passage, code included
    Scenario: Markdown and code are read as text
      Given a Markdown file "# Packing list <code> - tent - sleeping bag - a torch for the camping trip"
      And a Python file whose comment says "Convert a temperature from Fahrenheit to Celsius <code>"
      When the user searches for "what to bring when camping", and then for "temperature conversion"
      Then each search lists its file with its passage
    Scenario: a web page is read without its markup
      Given a web page with a style "color: crimson", a script "trackingPixel" and the heading "Aquarium visit <code>"
        over "The jellyfish tank glows blue in the dark."
      When the user searches for "jellyfish at the aquarium"
      Then the page is listed with its passage
      And neither "crimson" nor "trackingPixel" is shown anywhere
    Scenario: only the first part of a long document is searched
      Given a text file of 40,000 characters of filler, ending "The astronaut floated outside the space station to
        repair a solar panel, mission <code>."
      When the user searches for "an astronaut repairing a solar panel"
      Then the code is not shown anywhere
  how
    decide read about 32,000 characters and embed at most 12 passages of a document
      because that is about 6 s of the processor's time per document; reading more would be parsed but never
      embedded (PR #1, "Known limitations"; ExtractionLimits)
      rejected embedding the whole of a long document because "unknown, confirm with the owner: text beyond the first
      12 passages cannot be found by meaning; is a deeper pass planned?"
    decide files over 64 MB and reading over 20 seconds give the name only
      because a hostile or huge file must never stop indexing, and is not retried endlessly (DocumentReader)
    decide web pages keep 4 × 32,000 characters of markup before it is turned into text
      because markup is typically several times the visible text (PlainTextExtractor)
  uses      TextChunking
  children  OfficeDocuments, MailAndNotebooks, DocumentReading
  sources
    README.md, "What gets indexed"
    PR #1 "Valafind: local semantic file search", Extraction & chunking, and "Known limitations": first ~32k characters (up to 12 chunks)
    driven on Linux (Xvfb, openbox) 2026-11-25: a .txt, .md, .py, .html and a 40,000-character .txt saved into the home folder and searched
    src/Valafind.Core/Extraction/PlainTextExtractor.cs, HtmlText.cs, DocumentReader.cs
    src/Valafind.Core/Abstractions/Contracts.cs, ExtractionLimits; src/Valafind.Core/Extraction/TextChunker.cs, ChunkerOptions
    tests/Valafind.Core.Tests/Extraction/DocumentReaderTests.cs (Html_IsConvertedToText, OversizedFile_IsMetadataOnly, MaxChars_TruncatesStreamingRead)
```
