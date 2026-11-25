# What gets indexed, by kind of file

Valafind reads each file for what it holds, in the way that suits its kind: the text of a document, what a picture
shows, what is said or heard in a recording, and both the pictures and the sound of a video. A file it cannot read is
still found by its name.

```
kinds.ail.md                                                   KindsOfFiles (this file)
text-documents/text-documents.ail.md                           TextDocuments: text, Markdown, code, web pages; the limit
text-documents/office-documents/office-documents.ail.md        OfficeDocuments: Word, Excel, PowerPoint, OpenDocument, EPUB, RTF, PDF
text-documents/mail-and-notebooks/mail-and-notebooks.ail.md    MailAndNotebooks: .eml, .mbox, Jupyter notebooks
text-documents/document-reading/document-reading.ail.md        DocumentReading: which reader, limits, failures
text-documents/document-reading/format-rules/format-rules.ail.md   FormatRules: the text each format gives
pictures/pictures.ail.md                                       Pictures: found by what they show
pictures/picture-decoding/picture-decoding.ail.md              PictureDecoding: formats, size, orientation
audio/audio.ail.md                                             Audio: found by what is said or heard
audio/audio-clips/audio-clips.ail.md                           AudioClips: which 30-second clips are taken
video/video.ail.md                                             Video: found by its pictures and its sound
video/video-frames/video-frames.ail.md                         VideoFrames: which frames are taken, and their size
media-decoders/media-decoders.ail.md                           MediaDecoders: the decoders of each platform
name-only/name-only.ail.md                                     NameOnly: files found by their name alone
name-only/name-only-rules/name-only-rules.ail.md               NameOnlyRules: which files, and the text of a name
text-sniffing/text-sniffing.ail.md                             TextSniffing: text files without a known extension
text-sniffing/sniff-rules/sniff-rules.ail.md                   SniffRules: what counts as text, and its encoding
kind-table/kind-table.ail.md                                   KindTable: the kind of each extension
```

```ail
node KindsOfFiles : flow   level: experience
  why   People look for a file by what was in it: what a letter said, what a photo showed, what was said in a
        meeting. Valafind is only useful if every kind of file is read for that, and the rest can still be found
        by name.   <- Valafind
  what
    Documents are found by their text: each contributes its first part, up to about 32,000 characters.
    Pictures are found by what they show, including the words written in them.
    Audio is found by what is said or heard in it, from up to four 30-second clips.
    A video is found by eight of its pictures and up to two 30-second clips of its sound.
    Every file Valafind keeps is also found by its name and folder; a file it cannot read is found by those alone.
    A file of no known kind is read only when it looks like text; otherwise it is left out of the index.
    Indexing goes by kind: documents first, then pictures, then audio, then video (see the index order).
    Scenario: a question finds files of every kind by what they hold
      Given a recording in which a weather forecast is read out
      And a picture of a red circle
      And a note that says "Remember to water the tomato plants every morning before work."
      And their names say nothing about what they hold
      When the user searches for "looking after the vegetable garden"
      Then the note is listed above the recording and the picture
      When the user searches for "a red circle"
      Then the picture is listed above the recording and the note
      When the user searches for "a weather forecast"
      Then the recording is listed above the picture and the note
  how
    decide pictures, audio and video are read by what they show and say, in the same space as text
      because a question in words should find a photo or a voice memo as easily as a letter (PR #9, README)
      rejected finding pictures and media by their name only because that was the first version's limit (PR #1, "Known limitations")
    decide every kept file also has a meaning made of its name and folder
      because a file whose content cannot be read, or reads as nothing, must still be findable (PR #9, "each media file also keeps a vector of its name")
    decide documents are read for their text only, not for the pictures in them
      because "unknown, confirm with the owner: should pictures inside documents (a scanned PDF) be read?"
    decide documents are indexed before pictures, audio and video
      because media cost far more to read and are searched for less often, so documents are always searchable first (PR #9, README)
  uses      KindTable, IndexOrder
  children  TextDocuments, Pictures, Audio, Video, NameOnly, TextSniffing, MediaDecoders, KindTable
  sources
    README.md, "What gets indexed" and "Stays out of your way"
    PR #1 "Valafind: local semantic file search", formats and "Known limitations"
    PR #9 "Switch to EmbeddingGemma 2 and search pictures, audio and video by content"
    driven on Linux (Xvfb, openbox) 2026-11-25: a speech recording, a red circle and notes saved into the home folder, searched by meaning
    src/Valafind.Core/Indexing/IndexingCoordinator.cs, Index and EmbedMedia
```
