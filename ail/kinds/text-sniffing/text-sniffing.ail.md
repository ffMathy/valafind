# Text without a known extension

A file whose extension Valafind does not know (or that has none, like "README" or "CHANGELOG") is read as text when
its first bytes look like text, in whatever encoding it was written. Anything else of no known kind is left out.

```ail
node TextSniffing : flow   level: experience
  why   Plenty of people's notes, logs and configuration files have no extension or an unusual one, and they are as
        worth finding as a .txt; binary files of no known kind are not worth an entry.   <- KindsOfFiles
  what
    A file of no known kind is read as text when its first 8 KB look like text: no NUL bytes (unless it is UTF-16 or
      UTF-32), few control characters, and not mostly bytes above the plain ASCII range unless they form valid UTF-8.
    Every text file, of a known kind or not, is decoded from its byte order mark when it has one, else as UTF-16 when
      every other byte is zero, else as UTF-8 when it is valid UTF-8, else as Windows-1252 (Western European).
    A text file of no known kind shows the plain file icon and the type "FILE" in its details.
    A file of no known kind that does not look like text is not indexed at all: it is not even found by its name.
    Scenario: a text file without an extension is read
      Given a file "<code>-CHANGELOG" without an extension that says "Release <other code>: fixed the crash when the
        printer ran out of paper during a long print job."
      When the user searches for "the printer ran out of paper"
      Then the file is listed with its passage
    Scenario: a UTF-16 text file without a byte order mark is read
      Given a log file written by a Windows tool in UTF-16 without a byte order mark that says "Night <other code>: the
        greenhouse heater switched on because frost was coming."
      When the user searches for "frost in the greenhouse"
      Then the file is listed with its passage
    Scenario: a binary file with an unknown extension is not indexed
      Given a file "sailing-logbook-<code>.bin" of random bytes, and then a text file "sailing-notes-<code>.txt"
      When the user searches for "sailing logbook" until the text file is listed
      Then "sailing-logbook-<code>.bin" is not listed
  how
    decide look at the first 8 KB only
      because the check is meant to be cheap: "cheap heuristics" for every unknown file in a home folder (TextSniffer)
    decide escape (colour codes in logs) and the old end-of-file mark are allowed control characters
      because logs with colours and old DOS text files are text (TextSniffer)
    decide Windows-1252 as the last resort
      because it is a superset of printable Latin-1, so any 8-bit Western text decodes (TextSniffer)
    decide an unknown binary file gets no entry
      because "unknown, confirm with the owner: such files cannot be found even by name; keep this?"
  look
    a text file without a known extension shows the grey plain-file icon, and "FILE" before its size in the details
  children  SniffRules
  sources
    driven on Linux (Xvfb, openbox) 2026-11-25: "CHANGELOG" without an extension, a UTF-16 .log without a byte order
      mark, and a .bin of random bytes saved after which a .txt was saved
    src/Valafind.Core/Extraction/TextSniffer.cs, PlainTextExtractor.cs (CanHandle)
    src/Valafind.Core/Indexing/IndexingCoordinator.cs, Index ("Unknown, unreadable file: not worth a name-only entry")
    tests/Valafind.Core.Tests/Extraction/TextSnifferTests.cs; DocumentReaderTests.cs, UnknownExtension_SniffedText_IsRead, BinaryGarbage_UnknownExtension_ReturnsNull
```
