# Rebuilding the index

```ail
node RebuildIndex : fn   level: system
  why   Users need one simple way to start over when the index seems wrong, and an index made by another model or
        another size of meanings cannot be searched at all.   <- Indexing
  what
    The index is index.db in the data folder (IndexDatabase). With Valafind closed, deleting index.db (with its -wal and
      -shm files) makes the next start create an empty index and run Phase 1 and Phase 2 from the beginning; the
      launcher shows the panel counting up from "0 / … files" and "0 of 0+".
    The index also starts over, without deleting the file, when the model or the size of its meanings ("Dimensions" in
      Settings) differs from the one the index was built with, and Phase 1 and Phase 2 run again whenever the index
      holds no files at the start.
    Changing the indexed folders or the excludes does not start over: Phase 2 walks again, unchanged files are kept, and
      files now out of scope are removed at the end of the walk.
    Requests for online-only cloud files are kept through a model change, so they are still undone.
    examples
      start with no index.db => Phase 1 and Phase 2 run in full
      start with index.db of the same model and 768, 412 files, both phases complete => no phase runs again; the check after start follows at 90 s
      start with index.db built at 256 when Settings now says 768 => every file and chunk is dropped, both phases run in full
      start after the folders changed in Settings => Phase 2 walks again, unchanged files are not read again
  how
    decide deleting the file is the way to rebuild
      because "unknown, confirm with the owner: there is no Rebuild button"
    decide a different model or size empties the index
      because vectors from another model or size are meaningless for the new one (code comment; README on the move to EmbeddingGemma 2)
  uses      IndexDatabase
  sources
    README.md, "Where your data lives" ("Delete index.db to rebuild the index") and "Installing, updating and uninstalling" (the move to EmbeddingGemma 2 builds the index again)
    src/Valafind.Core/Indexing/IndexingCoordinator.cs (PrepareMeta, ConfigurationFingerprint), src/Valafind.Core/Storage/SqliteIndexStore.cs (EnsureModel)
    driven on Linux (Xvfb, openbox) 2026-11-25: Valafind stopped, index.db, -wal and -shm deleted, Valafind started: "Phase 1: 39 frequently used files" in the log, the panel "11 / 39 files" and "0 of 0+ · queued"
```
