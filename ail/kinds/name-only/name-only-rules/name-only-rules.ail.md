# Name-only rules

Which files are never opened, the kind they are given, and the text that stands for a file that has no content to
read.

```ail
node NameOnlyRules : fn   level: system
  why   A file whose content is not read must still get a kind (for its icon and the filters) and a meaning, and the
        same file must get the same meaning on every platform.   <- NameOnly
  what
    UNREAD_DOCUMENTS: set of text = [".doc", ".dot", ".xls", ".xlt", ".ppt", ".pps", ".pot", ".msg", ".pages", ".key", ".numbers", ".epub"]
      — ".epub" only as a fallback: the e-book reader comes first and reads it
    nameOnly.canHandle(path: text) -> boolean — the kind of path is picture, audio, video or archive, or its
      extension is in UNREAD_DOCUMENTS (ignoring case); the file is never opened
    nameOnly.extract(path: text) -> { text: "", kind: kindOf(path) }
    kindOf(path: text) -> FileKind — the kind table's kind; for an extension the table does not know: ".dot", ".docm",
      ".dotx", ".ott" => "document"; ".xlt", ".xlsm", ".xltx", ".ots" => "spreadsheet"; ".pps", ".pot", ".pptm",
      ".potx", ".ppsx", ".otp" => "presentation"; else "other"
    nameOnlyText(path: text, roots: list of text) -> text — the path relative to the longest indexed folder that holds
      it (compared exactly on Linux, ignoring case elsewhere), or the whole path when none does; its parts joined by " / "
    a file with no chunk of text is stored as one chunk { index: 0, start: 0, length: 0, text: "" } whose vector is
      the embedding of the document with title its file name and text nameOnlyText; a picture, a recording or a video
      with no text adds its content vectors after it (see media inputs)
    a file that gives no text and whose kind is "other" is not stored, and an entry it had is removed
    examples
      kindOf("legacy.doc") => "document";  kindOf("legacy.xls") => "spreadsheet";  kindOf("deck.key") => "presentation"
      kindOf("outlook.msg") => "mail";  kindOf("backup.zip") => "archive";  kindOf("song.mp3") => "audio"
      kindOf("letter.dotx") => "document";  kindOf("sheet.xlsm") => "spreadsheet";  kindOf("blob.bin") => "other"
      nameOnly.canHandle("photo.jpg") => true;  nameOnly.canHandle("notes.txt") => false;  nameOnly.canHandle("blob.bin") => false
      nameOnlyText("/home/u/Taxes/2025/return.zip", ["/home/u"]) => "Taxes / 2025 / return.zip"
      nameOnlyText("/home/u/Music/a.mp3", ["/home/u", "/home/u/Music"]) => "a.mp3"
      nameOnlyText("/mnt/x/y.zip", ["/home/u"]) => "mnt / x / y.zip"
  how
    decide one name vector for every stored file, content or not
      because a picture or recording must stay findable by its name when its content cannot be read, and a decoding
      failure leaves exactly that (PR #9, "each media file also keeps a vector of its name")
    decide the longest matching root wins
      because "unknown, confirm with the owner" (IndexingCoordinator.NameOnlyText picks the longest)
  sources
    src/Valafind.Core/Extraction/MetadataOnlyExtractor.cs (CanHandle, KindOf)
    src/Valafind.Core/Indexing/IndexingCoordinator.cs (Index: "Unknown, unreadable file: not worth a name-only entry", NameOnlyText)
    tests/Valafind.Core.Tests/Extraction/DocumentReaderTests.cs, MediaAndLegacyFormats_AreMetadataOnly, Msg_IsMetadataOnly
```
