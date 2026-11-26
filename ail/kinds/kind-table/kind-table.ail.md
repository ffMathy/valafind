# The kind of each extension

Every file gets a kind from its extension alone. The kind picks its reader, its icon, its filter and its place in
the index order.

```ail
node KindTable : data   level: system
  why   One table decides how every file is read, shown, filtered and queued, so these never disagree about a
        file.   <- KindsOfFiles
  what
    type FileKind = "other" | "document" | "pdf" | "spreadsheet" | "presentation" | "text" | "code" | "mail" | "notebook" | "image" | "audio" | "video" | "archive"
    classify(path: text) -> FileKind — by the extension after the last dot of the file name, ignoring case; "other"
      when there is none or the table does not have it
    the table:
      pdf: .pdf
      document: .doc .docx .odt .rtf .pages .epub
      spreadsheet: .xls .xlsx .ods .csv .tsv .numbers
      presentation: .ppt .pptx .odp .key
      text: .txt .md .markdown .rst .org .tex .log .html .htm .xml .json .yaml .yml .toml .ini
      mail: .eml .msg .mbox
      notebook: .ipynb
      code: .cs .fs .vb .py .js .mjs .ts .tsx .jsx .java .kt .go .rs .c .h .cpp .hpp .swift .rb .php .sh .ps1 .sql
        .css .scss .axaml .xaml .lua .r .scala .dart
      image: .png .jpg .jpeg .gif .heic .webp .bmp .tif .tiff .svg .raw .heif .avif
      audio: .mp3 .wav .flac .m4a .ogg .aac .opus .wma .aif .aiff
      video: .mp4 .mov .mkv .avi .webm .m4v .wmv .3gp
      archive: .zip .7z .rar .tar .gz
    a reader may give a more exact kind than the table: .docm, .dotx, .xlsm, .pptm, .ott and the other templates are
      "other" here but "document", "spreadsheet" or "presentation" once read (see name-only rules)
    examples
      classify("/home/u/Lease.PDF") => "pdf"
      classify("/home/u/notes.txt") => "text";  classify("/home/u/page.html") => "text";  classify("/home/u/data.csv") => "spreadsheet"
      classify("/home/u/photo.HEIC") => "image";  classify("/home/u/drawing.svg") => "image"
      classify("/home/u/memo.m4a") => "audio";  classify("/home/u/clip.webm") => "video"
      classify("/home/u/backup.tar.gz") => "archive"
      classify("/home/u/README") => "other";  classify("/home/u/blob.bin") => "other";  classify("/home/u/report.docm") => "other"
  how
    decide by extension only, never by content (the one exception: text sniffing for "other")
      because "unknown, confirm with the owner"
    decide web pages, JSON, XML and YAML count as text, not code
      because "unknown, confirm with the owner"
  uses      IndexOrder
  sources
    src/Valafind.Core/Abstractions/FileKind.cs (FileKind, FileKinds.Classify)
    src/Valafind.Core/Indexing/IndexOrder.cs (Rank by kind); src/Valafind.Core/Extraction/MetadataOnlyExtractor.cs (KindOf)
```
