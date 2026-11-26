# macOS change log (FSEvents)

Described from the code only: it runs on macOS, which could not be driven here.

```ail
node FsEventsHistory : fn   level: system
  why   On macOS the system keeps a log of changed paths that survives restarts, so the check after start can ask what
        changed instead of walking every folder.   <- WatchChanges
  what
    FsEventsHistory(timeout: duration) -> FsEventsHistory — fills the ChangeHistory role of StartupCheck
    REPLAY_TIMEOUT: duration = 2 min
    capture(roots) -> text? — "fsevents-v1", the current system event id, then one line per root (sorted) with its
      volume's event-store id and the root, tab-separated; nothing when a root has no event store (network and some
      external volumes) or its path holds a line break or tab, or the system library is missing
    tryReplay(checkpoint, roots, onChange) -> boolean — opens a stream from the checkpoint's event id with file-level
      events and reads until the history is done
    The id is taken before the volume ids, so anything that happens while they are read is replayed next time.
    A file is reported as "changed" and a folder as "upsert", whatever happened to it; the handler checks what is there
      now. A "must scan subfolders" event is a "rescan" of that folder; one above the roots becomes a rescan of every
      root below it.
    Paths the system reports through "/private" (such as "/private/var/…") are mapped back to the roots as configured.
    tryReplay answers false, and the caller walks, when the checkpoint is not in this format, the roots differ from the
      checkpoint's, a volume's event-store id changed (the log was reset or the root moved to another volume), the
      event ids wrapped, or the replay took longer than REPLAY_TIMEOUT. Changes reported before a false answer still count.
    examples
      tryReplay("fsevents-v0\n…", roots, f) => false
      tryReplay(checkpoint taken for ["/Users/u"], ["/Users/u", "/Volumes/Data"], f) => false
  how
    decide the volume's event-store id is part of the checkpoint
      because event ids from a reset store no longer line up with the checkpoint (code comment, PR #20)
    decide a replay that runs over two minutes gives way to a walk
      because "unknown, confirm with the owner: why 2 minutes"
  sources
    GitHub PR #20 (FSEvents replay, UUIDs, 2 min timeout, /private mapping, rescans above a root)
    README.md, "Stays out of your way" ("On macOS it asks the system's change log (FSEvents) what changed since it last ran")
    src/Valafind.Core/Platform/FsEventsHistory.cs
    not driven: macOS only
```
