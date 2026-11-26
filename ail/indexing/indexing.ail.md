# Indexing

Valafind reads the user's files in the background so that a search can find them by meaning. It covers the home
folder (or the folders chosen in Settings), starts with the files the user uses most, keeps up with every change,
and stays out of the way of whatever else the computer is doing.

```ail
node Indexing : flow   level: experience
  why   A search can only find what has been read, so every file the user cares about must get into the index, and
        stay current, without the user ever having to start, stop or babysit it.   <- Valafind
  what
    The index covers the home folder unless the user picked other folders in Settings, and skips folders that hold
      no files of the user's own (hidden and app-data folders, installed packages, version control, build output,
      caches and the Trash).
    Files used often are read first (Phase 1), then everything else (Phase 2); within that, documents always come
      before pictures, pictures before audio, and audio before video.
    While Valafind runs, a file that is added, changed, moved or deleted is updated within seconds; changes made while
      it was not running are caught up about 90 seconds after it starts, and every folder is checked again once a day.
    Indexing uses the computer's spare capacity up to the limits in Settings (30 % processor and 25 % disk by
      default). Load from other programs slows it down but never stops it; only the user, or running on battery when
      "Pause indexing on battery power" is on, pauses it.
    While the user is searching, indexing holds back so the search answers quickly, for at most two minutes.
    The launcher shows the index panel while indexing is under way or paused, and a small "Up to date" otherwise.
    Online-only cloud files are downloaded, read and made online-only again (on Windows and on a Mac).
    The index is one database file in the data folder; deleting it while Valafind is closed makes Valafind build the
      index again from scratch.
  children  WhatGetsIndexed, IndexOrder, WatchChanges, Pacing, IndexPanel, CloudFiles, RebuildIndex
  sources
    README.md, "Stays out of your way", "What gets indexed", "Where your data lives"
    GitHub PR #1 "Valafind: local semantic file search" (Phase 1 / Phase 2, watchers, daily re-check, roots and excludes)
    GitHub PRs #14 and #16 (PID pacing, never pausing for load), #11 (keep indexing while the panel shows), #20 (catch-up after start), #18, #26, #34 and #36 (cloud files)
    driven on Linux (Xvfb, openbox) 2026-11-25: files saved in the home folder, the index panel, Settings, the launcher menu
    src/Valafind.Core/Indexing/IndexingCoordinator.cs
```
