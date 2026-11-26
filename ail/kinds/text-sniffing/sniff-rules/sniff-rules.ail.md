# Sniffing text and its encoding

The exact tests that decide whether bytes are text, and which encoding text is read in.

```ail
node SniffRules : fn   level: system
  why   A wrong "yes" fills the index with binary noise, a wrong "no" loses a user's notes, and a wrong encoding turns
        every accented letter into garbage that no question matches.   <- TextSniffing
  what
    HEAD_SIZE: integer = 8192 — the bytes looked at to decide; reading text uses the first 65,536 bytes to pick its encoding
    suspicious(b: integer) -> boolean — b is 127, or below 32 and not tab (9), line feed (10), vertical tab (11), form
      feed (12), carriage return (13), substitute (26) or escape (27)
    looksLikeText(head: bytes) -> boolean
      1. empty => false
      2. a UTF-8 byte order mark: true when no NUL follows and at most 2 % of the bytes after it are suspicious
      3. a UTF-16 or UTF-32 byte order mark: the rest decoded (whole code units only); true when it has no NUL and at
         most 2 % suspicious characters below 128
      4. byte-order-mark-less UTF-16 (below): decoded and checked the same way
      5. any NUL byte => false; more than 2 % suspicious bytes => false
      6. valid UTF-8, allowing a sequence cut off at the end => true
      7. otherwise true when at most 30 % of the bytes are 128 or above
    guessBomlessUtf16(head: bytes) -> "utf-16le" | "utf-16be" | nothing — at least 2 byte pairs; little-endian when at
      least 60 % of the pairs have a zero second byte and at most 5 % a zero first byte; big-endian the other way round
    detectEncoding(head: bytes, headIsWholeFile: boolean) -> { encoding: text, bomLength: integer }
      — the byte order mark (UTF-8 3 bytes, UTF-32 LE FF FE 00 00 and BE 00 00 FE FF 4 bytes, UTF-16 LE FF FE and BE
      FE FF 2 bytes; UTF-32 LE is checked before UTF-16 LE); else guessBomlessUtf16; else UTF-8 when valid (a cut-off
      sequence at the end allowed only when the head is not the whole file); else Windows-1252. Text is read after the mark.
    plainText.canHandle(path) for a path of kind "other" (and not .csv or .tsv): looksLikeText of its first HEAD_SIZE
      bytes; a file that cannot be opened => false
    examples
      looksLikeText([]) => false
      looksLikeText(utf8("plain ascii text\r\nwith lines\tand tabs")) => true
      looksLikeText(utf8("\u001b[32mcolored log\u001b[0m")) => true
      looksLikeText(utf16le("Windows tool output, UTF-16 without BOM")) => true;  likewise utf16be
      looksLikeText(latin1("Café crème brûlée, naïve façade")) => true
      looksLikeText(4096 random bytes) => false
      looksLikeText([0x7F, "E", "L", "F", 2, 1, 1, 0, 0, 0, 0, 0]) => false;  the start of a PNG => false
      looksLikeText(1000 bytes, every tenth 0x01, the rest "a") => false
      looksLikeText(4096 random bytes from 0x80 to 0xFF) => false
      read("README" holding "Just a readme\nwith lines") => { text: "Just a readme\nwith lines", kind: "other" }
      read("nobom.log" in UTF-16 LE without a mark holding "plain ascii log line\nsecond line") => that text
      read("legacy.txt" holding 93 43 61 66 E9 94 20 96 20 80 35) => { text: "“Café” – €5", kind: "text" }
      read("le.txt" and "be.md" with UTF-16 LE and BE marks holding "Grüße aus Zürich") => "Grüße aus Zürich" for both
  sources
    src/Valafind.Core/Extraction/TextSniffer.cs (LooksLikeText, DetectBom, GuessBomlessUtf16, DetectEncoding, IsValidUtf8)
    src/Valafind.Core/Extraction/PlainTextExtractor.cs (CanHandle, SniffBytes 64 KB)
    tests/Valafind.Core.Tests/Extraction/TextSnifferTests.cs; DocumentReaderTests.cs (Utf8Text_WithBom, Utf16LittleAndBigEndian_WithBom, Utf16_WithoutBom_IsDetected, Windows1252_FallsBackFromInvalidUtf8, UnknownExtension_SniffedText_IsRead)
```
