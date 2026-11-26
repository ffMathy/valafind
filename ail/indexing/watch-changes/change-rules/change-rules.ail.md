# Change rules

From a watcher event to an index update: which watchers, debouncing, de-duplication, the order changes are handled
in, and what each kind of change does.

```ail
node ChangeRules : fn   level: system
  why   File events arrive in bursts, twice, out of order, or not at all; the index must still end up matching the disk,
        with as little work as possible.   <- WatchChanges
  what
    type ChangeKind = "delete" | "upsert" | "rescan" | "changed"
    PendingChanges(debounce: duration, overflowTarget: (text) -> text?, capacity: integer = 50000) -> PendingChanges
    PendingChanges.add(path: text, kind: ChangeKind) -> nothing
    PendingChanges.takeDue() -> list of { path: text, kind: ChangeKind } — deletes first, then upserts and changes, then rescans; by path in ordinal order within each
    PendingChanges.nextDueIn() -> duration?
    DEBOUNCE: duration = 2 s
    ROOT_RESCAN_MIN_INTERVAL: duration = 10 min
    Watchers: on Linux one inotify watch per indexed folder (skipped folders get none); on Windows and macOS one
      recursive watcher per root (64 KB buffer) whose events for skipped paths are dropped by name before queueing.
      A rename is a delete of the old path and an upsert of the new one. Created is upsert, written is changed.
    Lost events (a watcher overflow or error) schedule a rescan of the whole root, at most once per root every 10 minutes.
      Reaching the system's watch limit is logged once and not rescanned: the daily walk covers it.
    Handling, after the debounce: delete removes the path and everything under it, unless the path is a root or its
      root folder is gone; rescan walks the folder (documents first, then media in kind order) and removes entries
      whose files vanished; upsert of a folder walks it; changed on a folder does nothing; a file that exists is
      indexed (or removed when it is now skipped); a path that no longer exists is removed unless its root is gone.
    Indexing a file first compares size and modification time with the index: unchanged files are not read again;
      an empty file is removed from the index; a rename that changes only letter case is indexed again.
    Before a batch of changes is handled, the files already being indexed finish, so a delete never races an update of the same path.
    in the examples, t is the time in seconds; add(p, k) at t
    examples
      add("/h/a.txt", "upsert") at 0, takeDue() at 1.9 => []
      add("/h/a.txt", "upsert") at 0, takeDue() at 2 => [{ path: "/h/a.txt", kind: "upsert" }]
      add("/h/a.txt", "upsert") at 0, add("/h/a.txt", "changed") at 1, takeDue() at 3 => [{ path: "/h/a.txt", kind: "upsert" }]
      add("/h/a.txt", "changed") at 0, add("/h/a.txt", "delete") at 1, takeDue() at 2.5 => []
      add("/h/a.txt", "changed") at 0, add("/h/a.txt", "delete") at 1, takeDue() at 3 => [{ path: "/h/a.txt", kind: "delete" }]
      add("/h/b.txt", "upsert") at 0, add("/h/a.txt", "delete") at 0, takeDue() at 2 => [{ path: "/h/a.txt", kind: "delete" }, { path: "/h/b.txt", kind: "upsert" }]
      add("/h/d", "rescan") at 0, add("/h/d/x.txt", "upsert") at 1, takeDue() at 2 => [{ path: "/h/d", kind: "rescan" }]
      add("/h/d/x.txt", "upsert") at 0, add("/h/d", "rescan") at 1, takeDue() at 3 => [{ path: "/h/d", kind: "rescan" }]
      capacity 2, add("/h/a", "upsert"), add("/h/b", "upsert"), add("/h/c", "upsert") with overflowTarget giving "/h" => only a rescan of "/h" is pending
  how
    decide the last event for a path wins, except that "changed" never downgrades a pending "upsert"
      because a created or moved-in folder must still be walked (code comment)
    decide more than 50,000 pending paths collapse into rescans of their roots
      because memory must stay bounded however many events arrive while indexing is paused (code comment)
    decide Linux uses its own inotify tree watcher instead of the platform's recursive watcher
      because a recursive watcher puts a watch on every subfolder, node_modules, .git and ~/.cache included, and can exhaust the per-user limit shared with editors and sync clients (code comment, PR #1)
    decide a deleted root is not removed from the index
      because a root "deleted" is usually a drive going away (code comment)
    decide 2 seconds of quiet before a change is handled
      because "unknown, confirm with the owner: why 2 seconds"
  sources
    src/Valafind.Core/Indexing/PendingChanges.cs
    src/Valafind.Core/Indexing/IndexingCoordinator.cs (StartWatchers, StartInotify, StartFileSystemWatcher, OnWatcherEvent, ScheduleRootRescan, DrainPending, HandleChange, RescanDirectory, Prepare, CoordinatorTuning.Default)
    src/Valafind.Core/Platform/InotifyTreeWatcher.cs
```
