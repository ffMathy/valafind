# Reading a document

Which reader takes a file, within which limits, and what a file that cannot be read becomes.

```ail
node DocumentReading : fn   level: system
  why   Every file in a home folder is untrusted input of any size; reading one must never stop indexing, never take
        unbounded memory or time, and must say whether the file is still worth an entry.   <- TextDocuments
  what
    type ExtractionLimits = { maxFileBytes: integer = 64 MB, maxChars: integer = 32000 }
    type ExtractedText = { text: text, kind: FileKind } — text may be empty: the file is then indexed by its name
    type TextReader = { canHandle(path: text) -> boolean, extract(path: text, limits: ExtractionLimits) -> later ExtractedText or nothing }
    DEFAULT_PER_FILE_TIMEOUT: duration = 20 s
    READERS: list of TextReader = [pdf, officeOpenXml, openDocument, epub, mail, notebook, rtf, plainText, nameOnly]
      — tried in this order; the first whose canHandle is true reads the file
    DocumentReading.read(path: text) -> later ExtractedText or nothing
      1. a blank path, a missing file or a folder => nothing
      2. no reader can handle the file => nothing
      3. the file is larger than maxFileBytes => { text: "", kind: kindOf(path) }, without opening it
      4. the reader runs with a deadline of DEFAULT_PER_FILE_TIMEOUT; running out of time, or a hard limit (a zip with
         more than 20,000 entries, a decompression bomb, a part over its byte budget) => { text: "", kind: kindOf(path) }
      5. any other failure (locked, unreadable, damaged) => nothing; only the caller's own cancellation passes through
    then indexing: nothing with a kind of "other" => no entry (an entry it had is removed); nothing with any other kind,
      or empty text => an entry with one vector made of the file's name and folder (see name-only rules)
    every reader stops appending at maxChars and never splits a surrogate pair; plainText stops reading there
    zip-based formats: at most 20,000 entries, 16 MB decompressed per part and 64 MB per file; an entry larger than
      8 MB that is compressed more than 100:1 is read only up to 8 MB, keeping the text before the cut
    examples
      read("long.log" holding "0123456789" 100,000 times) with maxChars 1000 => its first 1000 characters
      read("emoji.txt" holding "abcdef😀xyz") with maxChars 7 => { text: "abcdef", kind: "text" }
      read("big.txt" of 500 bytes) with maxFileBytes 100 => { text: "", kind: "text" };  likewise "big.pdf" => { text: "", kind: "pdf" }
      read("corrupt.docx" of random bytes) => nothing;  likewise .pdf, .xlsx, .pptx, .odt, .epub, .ipynb, .rtf
      read("blob.bin" of random bytes) => nothing;  read("noextension" of random bytes) => nothing
      read("does-not-exist.txt") => nothing;  read("") => nothing;  read("folder.txt" that is a folder) => nothing
      read("note.txt" held open for writing by another program) => its text
      read("note.txt" locked for exclusive use by another program, on Windows) => nothing
      read("photo.jpg") => { text: "", kind: "image" };  read("backup.zip") => { text: "", kind: "archive" }
  how
    decide failures give nothing, limits and timeouts give an empty text
      because a damaged file is skipped, while a file that is merely too big or too slow stays findable by name and
      is not retried endlessly until it changes (DocumentReader)
    decide files are opened sharing read, write and delete with other programs
      because editors, mail clients and sync tools keep files open while the user works (ExtractionIO.OpenRead)
    decide a damaged file of a known kind still gets a name-only entry, an unknown one none
      because a .docx that cannot be read is still the user's document; an unreadable file of no known kind is not
      worth an entry (IndexingCoordinator.Index)
  children  FormatRules
  sources
    src/Valafind.Core/Extraction/DocumentReader.cs, ExtractionIO.cs, BoundedInput.cs (SafeZip)
    src/Valafind.Core/Abstractions/Contracts.cs, ExtractionLimits, ExtractedText, ITextExtractor
    src/Valafind.Core/Indexing/IndexingCoordinator.cs, Index (the "Unknown, unreadable file" rule)
    tests/Valafind.Core.Tests/Extraction/DocumentReaderTests.cs, HostileInputTests.cs
    PR #1, "Hardened against hostile input: zip, XML and Flate bombs, guards on PDF object and xref streams, and a time budget for each file"
```
