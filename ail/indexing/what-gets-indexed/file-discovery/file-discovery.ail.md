# Finding the files

Which folders are walked, how Phase 1 finds the files used most, and how Phase 2 walks everything.

```ail
node FileDiscovery : fn   level: system
  why   Indexing a large home folder on the processor can take days, so the files the user is likely to look for must
        be found first, and the full walk must cost no more than reading the folders.   <- WhatGetsIndexed
  what
    type DiscoveredFile = { path: text, size: integer, modified: time }
    UserFileDiscovery(settings: Settings) -> UserFileDiscovery — roots from settings.roots, or the home folder when the list is empty (nothing in the Mac App Store sandbox, which allows only folders the user picked)
    UserFileDiscovery.roots: list of text
    UserFileDiscovery.enumerateFrequentFiles() -> list of text — Phase 1, best first, at most MAX_FREQUENT_FILES
    UserFileDiscovery.enumerateAllFilesWithStats() -> list of DiscoveredFile — Phase 2, breadth first over every root, produced as the walk goes
    UserFileDiscovery.enumerateUnder(folder: text) -> list of text — a new or rescanned folder, same rules
    UserFileDiscovery.isExcluded(path: text) -> boolean
    normalizeRoots(roots: list of text, home: text) -> list of text — "~" expanded, made absolute, trailing separators trimmed, duplicates and roots inside other roots dropped, blanks and invalid paths skipped
    score(count: integer, lastUsed: time, now: time) -> number — (1 + ln(1 + count)) × 2 to the power of (−age in days / 21)
    MAX_FREQUENT_FILES: integer = 10000
    RECENT_WINDOW: duration = 90 days
    RECENT_FOLDER_DEPTH: integer = 3
    RECENT_FOLDER_MAX_ENTRIES: integer = 50000
    Phase 1 candidates, each counted once per mention: Windows "Recent" shortcuts; Linux recently-used.xbel (with its
      visit counts) and KDE RecentDocuments; on macOS Spotlight's "last used in the past 90 days" per root (local
      index only, 30 s limit); plus files in Desktop, Documents and Downloads (Linux: the XDG user folders; Windows also
      the OneDrive folders) up to three levels deep, written or read in the last 90 days.
    A candidate that is excluded, missing, empty, a link or outside the roots is dropped. Ties in score go by path, in ordinal order.
    The walk lists each folder completely before going on, files and subfolders in ordinal order; files come with the
      size and modification time from the listing. Links are never followed (on Windows cloud placeholders, which are
      also reparse points, are kept). Unreadable folders are skipped silently.
    examples
      normalizeRoots(["~/Documents", "~/Documents/Work", "/data/", "  ", "~/Documents"], "/home/u") => ["/home/u/Documents", "/data"]
      normalizeRoots([], "/home/u") => []
      score(0, now, now) => 1
      score(0, now − 21 days, now) => 0.5
      score(1, now, now) => 1.6931471805599454
      score(3, now − 42 days, now) => 0.5965735902799727
      score(2, now + 3 days, now) => 2.09861228866811
  how
    decide Phase 1 ranks by frequency and recency together, halving every 21 days
      because "unknown, confirm with the owner: why 21 days, 90 days and 10,000 files"
    decide the walk is breadth first
      because shallow folders are usually more relevant and come first (code comment)
    decide each folder is listed in full before its files are handed on
      because no folder handle stays open while the indexer works (code comment)
    decide sizes and times come from the folder listing
      because unchanged files then need no second look at the disk (PR #20)
    decide online-only cloud files are listed like any other file; listing them downloads nothing
      because the indexer decides how to read them (CloudFiles; code comment)
  sources
    src/Valafind.Core/Indexing/UserFileDiscovery.cs
    src/Valafind.Core/Indexing/RecentFileParsers.cs (xbel, KDE and Windows shortcut parsers)
    GitHub PR #20 "Catch up on changes made while Valafind was not running" (stats from the listing)
    driven on Linux (Xvfb, openbox) 2026-11-25: with no recently-used list in a fresh home the panel showed "0 / 0 files" for Phase 1
```
