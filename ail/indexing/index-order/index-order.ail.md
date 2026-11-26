# Index order

Phase 1 (files used often), Phase 2 (everything else), and within each pass documents, then pictures, then audio,
then video.

```ail
node IndexOrder : fn   level: system
  why   On the processor a large home folder takes days to index, so what the user writes and reads must be
        searchable before the photo library, and the slow video work must come last.   <- Indexing
  what
    type Phase = "frequent" | "all" | "incremental"
    DOCUMENTS: integer = 0
    PICTURES: integer = 1
    AUDIO: integer = 2
    VIDEO: integer = 3
    rank(path: text) -> integer — by the file's kind: picture 1, audio 2, video 3, everything else (text, office files, PDFs, mail, code, archives, unknown) 0
    examples
      rank("/home/u/notes.txt") => 0
      rank("/home/u/lease.pdf") => 0
      rank("/home/u/backup.zip") => 0
      rank("/home/u/photo.jpg") => 1
      rank("/home/u/memo.mp3") => 2
      rank("/home/u/holiday.mp4") => 3
    Start: the index is reset (both phases marked not done) when the model differs from the one stored or the index
      holds no files. Phase 1 runs unless it was completed before; it indexes only the frequent documents and keeps the
      frequent pictures, audio and video for Phase 2.
    Phase 2 runs unless it was completed before and the roots and excludes are unchanged. Its walk hands files over in
      batches of 256 (or whatever it has after 1 s); unchanged files (same size, and a modification time equal or within
      2 s when one side has whole seconds) are counted done after one lookup for the whole batch. Documents are indexed
      as they are found; pictures, audio and video are kept and indexed afterwards, in that order, frequent ones first
      within their kind. The next kind starts only when every file of the previous kind is done.
    A change reported by the watchers during a pass waits until the pass reaches its kind.
    Up to as many files as the pace allows are indexed at once (at most the number of processors, at most 8); audio
      and video are decoded one file at a time.
    A pass is recorded complete, with its file count and the time, only when its walk finished and the online-only files
      it found are done too; then entries whose files are gone, excluded or outside the roots are removed (entries under
      a root that is missing or empty, like an unplugged drive, are kept).
    Each pass's progress (done and total, and whether the walk is still counting) feeds the index panel.
  how
    decide Phase 1 holds only documents; frequent media go first within their kind in Phase 2
      because no media is embedded before every document is (code comment)
    decide documents, pictures, audio, video
      because "everything you write and read is searchable before it starts on your photo library, and the slow video work comes last" (README)
    decide one kind finishes before the next starts, also for watcher changes
      because a new photo must not jump ahead of the documents (code comment)
    decide entries on an unavailable root are kept
      because the files are most likely still there and would all be embedded again when the drive comes back (code comment)
    decide a restart before the walk is recorded complete walks again
      because no file found by the walk may be forgotten (PR #26)
  sources
    README.md, "Stays out of your way"
    GitHub PRs #1 (phases), #14 (parallel workers, kind order kept), #20 (batches of 256), #26 (walk complete only with the cloud files)
    src/Valafind.Core/Indexing/IndexOrder.cs, src/Valafind.Core/Indexing/IndexingCoordinator.cs (RunPhase1, RunPhase2, RunFullScan, FindUnchanged, SweepStale, IsUnchanged)
    src/Valafind.Core/Indexing/IndexingWorkers.cs, src/Valafind.Core/Indexing/IndexingPace.cs (DefaultMaxConcurrency)
```
