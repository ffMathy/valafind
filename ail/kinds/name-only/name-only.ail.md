# Files found by their name only

Archives, older Office and Outlook files, iWork files, and media that no decoder can read are not opened: they are
found by their name and the folders they are in, never by what is inside.

```ail
node NameOnly : flow   level: experience
  why   A file whose content Valafind cannot read is still one the user may look for, and its name often says what it
        is ("contract_apartment.doc").   <- KindsOfFiles
  what
    These are found by their name and folder only: archives (.zip, .7z, .rar, .tar, .gz), older Word, Excel and
      PowerPoint files (.doc, .dot, .xls, .xlt, .ppt, .pps, .pot), Outlook messages (.msg), Pages, Keynote and
      Numbers files, and pictures, recordings and videos that cannot be decoded on this computer.
    So are files of a known kind that are damaged, larger than 64 MB, or too slow to read, and online-only cloud
      files that cannot be downloaded (see cloud files).
    A name-only file is found through its name and the folders under the indexed folder, read as words:
      "holiday-photos-from-crete.zip" is found by "holiday photos from Crete", while "Doc1.doc" or "scan_0003.xls" is
      not found by what it says.
    It never shows an excerpt or a preview of its content.
    Scenario: an archive is found by its name, not by what is in it
      Given an archive "holiday-photos-from-crete-<code>.zip" holding a text file that says "The submarine sonar heard a
        whale song <other code>."
      When the user searches for "holiday photos from Crete"
      Then the archive is listed
      And the other code is not shown anywhere
    Scenario: an old Word file is found by its name only
      Given a file "minutes-of-the-board-meeting-<code>.doc" that holds the text "The elephant parade <other code>
        passed the castle gates."
      When the user searches for "board meeting minutes"
      Then the file is listed
      And the other code is not shown anywhere
    Scenario: a video no decoder can read is still found by its name
      Given a file "sailing-trip-to-norway-<code>.mp4" of random bytes
      When the user searches for "sailing trip to Norway"
      Then the file is listed
  how
    decide older Office files, Outlook messages and Pages, Keynote and Numbers files are name-only
      because they are compound or proprietary containers Valafind has no reader for; reading them would be a
        feature of its own, and saving them as .docx, .xlsx, .pptx or .eml makes them searchable (MetadataOnlyExtractor)
    decide archives are not opened
      because "unknown, confirm with the owner: should files inside archives be searchable?"
    decide a name-only file's meaning is made from its path below the indexed folder
      because "unknown, confirm with the owner: folder names are part of it; is that on purpose?"
  look
    a name-only result row shows its kind's icon (an archive box, a document, the video camera) and no excerpt
  uses      NameOnlyRules, CloudFiles
  children  NameOnlyRules
  sources
    README.md, "What gets indexed": Name only
    PR #1 "Known limitations": legacy .doc/.xls/.ppt/.msg and Apple's Pages, Keynote and Numbers files are name-only
    driven on Linux (Xvfb, openbox) 2026-11-25: a .zip holding a text, a .doc holding plain text, and an .mp4 of random
      bytes; each was found by its name, with no excerpt, and the text inside never appeared
    src/Valafind.Core/Extraction/MetadataOnlyExtractor.cs; src/Valafind.Core/Indexing/IndexingCoordinator.cs (Index, NameOnlyText)
```
