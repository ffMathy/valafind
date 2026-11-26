# The index database

index.db in the data folder: one SQLite database (WAL) with every indexed file, its chunks with their text and
vectors, the indexer's bookkeeping, and the cloud files it asked to download.

```ail
node IndexDatabase : data   level: system
  why   The index must survive restarts, so a file is only read and embedded again when it changes; it holds copies of
        the user's text, so only the user may read it.   <- ModelAndIndex
  what
    SCHEMA_VERSION: integer = 2
    tables, as created:
      meta(key text primary key, value text)
      files(id integer primary key, path text, path_key text unique, size integer, mtime_utc integer, kind integer,
        phase integer, indexed_utc integer, content_pending integer default 0)
      chunks(file_id integer, idx integer, start integer, length integer, text text, vector blob), unique on (file_id, idx)
      cloud_downloads(path_key text primary key, path text, size integer, phase integer, state integer,
        requested_utc integer, file_id integer default 0)
    path is the full path; path_key is its key (PathKeys); mtime_utc and indexed_utc are UTC ticks (100 ns since
      0001-01-01); kind: 0 other, 1 document, 2 PDF, 3 spreadsheet, 4 presentation, 5 text, 6 code, 7 mail,
      8 notebook, 9 image, 10 audio, 11 video, 12 archive; phase: 1 frequent, 2 all, 3 incremental
    a chunk row: its index in the file, its start and length in the extracted text (for a clip of sound, in whole
      seconds), its collapsed text (empty for a picture, video or clip), and its vector: dimensions float32 values,
      little-endian, normalised to length 1 when written
    meta keys: model_id, dimensions, schema_version; and the indexer's phase1_complete, phase1_count, phase2_complete,
      phase2_count, phase2_last_full_scan_utc, indexing_model_id, indexing_config and change_history_checkpoint (the
      checkpoint of the platform's change log, macOS FSEvents, from which the start-up check replays what changed)
    IndexStore.open(modelId: text, dimensions: integer) -> nothing — creates or upgrades the tables; when the stored
      model_id or dimensions differ, deletes every chunk and file (meta and cloud_downloads stay) and stores the new
      ones; then loads every vector into memory (VectorSearch)
    IndexStore.upsertFile(file: FileEntry, chunks: list of ChunkRecord) -> nothing — one transaction: the file's row
      is inserted or updated, its old chunks deleted and the new ones written
    IndexStore.removeFile(path: text) -> nothing;  IndexStore.removeUnder(directory: text) -> integer — whole path
      components only; returns how many files were removed
    IndexStore.fingerprint(path: text) -> { size: integer, modifiedUtc: time } or nothing
    IndexStore.stats() -> { files: integer, chunks: integer, frequent: integer, all: integer } — kept in memory, cheap
    IndexStore.getMeta(key: text) -> text or nothing;  IndexStore.setMeta(key: text, value: text) -> nothing
    type FileEntry = { path: text, size: integer, modifiedUtc: time, kind: integer, phase: integer, contentPending: boolean }
    type ChunkRecord = { index: integer, start: integer, length: integer, text: text, vector: list of number }
    examples
      upsertFile({ path: "/home/a/notes.txt", size: 1234, modifiedUtc: 2024-01-02 03:04:05.678 +02:00 }, one chunk "hello world") then fingerprint("/home/a/notes.txt") => { size: 1234, modifiedUtc: 2024-01-02 01:04:05.678 UTC }
      upsertFile(a.txt with 2 chunks) then upsertFile(a.txt, size 7, 1 chunk "new") => stats { files: 1, chunks: 1 }
      upsertFile("/x/empty.bin", []) => fingerprint known, stats { files: 1, chunks: 0, frequent: 0, all: 1 }, never found
      upsertFile with a vector of the wrong size, or two chunks with the same index => refused, nothing written
      removeUnder("/home/a/docs/") over docs/x.txt, docs/sub/y.txt, docs, docs2/x.txt, docs.txt, doc/x.txt, Docs/x.txt, /home/b/docs/x.txt => 3
      removeUnder("/") => every file;  removeUnder("/c") with nothing there => 0
      open("other-model", 8) on an index of "test-model" with 8 => stats all 0, meta model_id "other-model", a meta key "keep" kept
      driven: a text file of 121 bytes saved in the home folder => files row (kind 5, phase 3), one chunk of start 0, length 121, its collapsed text, a 3072-byte vector
    the database is opened with journal_mode WAL, synchronous NORMAL, temp_store MEMORY and a busy timeout of 10 s;
      searches read through their own read-only connections, at most 8 kept
    on Unix the folder is 0700 and index.db, -wal, -shm and -journal 0600 whatever the umask; existing ones are tightened
    deleting index.db makes the next start build the index again
  how
    decide store float32 vectors and the chunk text in the database, and keep an int8 copy in memory
      because the copy in memory is fast to scan, the float vectors make the final scores exact, and the text is the
      excerpt a result shows (VectorSearch)
    decide a new model or size starts the index over
      because vectors from another model or size are meaningless for the new one (README, PR #9)
    decide create the files private before SQLite opens them
      because SQLite gives -wal and -shm the mode of the database file, and the index holds the user's text
    decide add columns in place (schema 1 to 2: content_pending, cloud_downloads.file_id) without a rebuild
      because the files already indexed stay valid
  children  PathKeys
  sources
    README.md, "Where your data lives"; PR #1; PR #18 and #26 (cloud files, content_pending)
    src/Valafind.Core/Storage/SqliteIndexStore.cs; src/Valafind.Core/Abstractions/Contracts.cs, FileKind.cs
    src/Valafind.Core/Indexing/IndexingCoordinator.cs (the meta keys)
    tests/Valafind.Core.Tests/Storage/SqliteIndexStoreTests.cs, SqliteIndexStoreReviewFixTests.cs, ContentPendingStoreTests.cs
    driven on Linux (Xvfb, openbox) 2026-11-25: .ail/model/data/index.db read with SQLite after saving kqzlighthouse.txt
```
