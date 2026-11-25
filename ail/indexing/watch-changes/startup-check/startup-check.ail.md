# The check after start, and the daily walk

```ail
node StartupCheck : fn   level: system
  why   Files edited, added or deleted while Valafind was closed stayed stale for up to 24 hours; catching up must not
        compete with everything else that starts at login.   <- WatchChanges
  what
    STARTUP_CHECK_DELAY: duration = 90 s
    FULL_RESCAN_INTERVAL: duration = 24 h
    CHECKPOINT_SETTLE: duration = 10 s
    IDLE_POLL: duration = 1 min
    type ChangeHistory = { capture(roots: list of text) -> text?, tryReplay(checkpoint: text, roots: list of text, onChange: (text, ChangeKind) -> nothing) -> boolean }
    After Phase 1 and Phase 2: when this session already walked every root, no check is scheduled; otherwise the check
      is due STARTUP_CHECK_DELAY after the indexer started.
    The check: with a change history (macOS), a saved checkpoint, and no daily walk due, it replays the history since
      the checkpoint, queueing each change like a watcher event; when the replay cannot answer, or there is no history
      (Windows, Linux), or the daily walk is due, it walks every root quietly.
    A quiet walk keeps the panel's last Phase 2 totals until it ends, then shows its own; unchanged files cost a
      listing and one index lookup per batch of 256.
    Daily walk: once no check is pending, after any handled change or at least every IDLE_POLL, a quiet walk runs when
      the last completed walk is FULL_RESCAN_INTERVAL old or older (or never recorded).
    Checkpoints (macOS only): only after the check has run; captured when nothing is pending, and saved
      CHECKPOINT_SETTLE later if no watcher event arrived meanwhile. While no new event arrived since the last saved
      checkpoint nothing is written. When the history has no checkpoint for these roots (a network volume), none is saved
      and the next check walks.
    in the examples, the indexer starts at 0 s; "walked" means this session's Phase 2 walk ran to the end
    examples
      fresh index, Phase 2 walked => no check is scheduled
      Phase 2 already complete from an earlier run, Linux => a quiet walk of every root at 90 s
      Phase 2 complete, macOS, checkpoint saved, last walk 3 h ago => the history since the checkpoint is replayed at 90 s, no walk
      Phase 2 complete, macOS, checkpoint saved, last walk 25 h ago => a quiet walk at 90 s
      Phase 2 complete, macOS, no checkpoint saved => a quiet walk at 90 s
      last walk 23 h 59 min ago, idle => nothing until the next idle poll after it turns 24 h
  how
    decide 90 seconds after start
      because it stays out of the way of everything else starting at login (PR #20, README)
      because "unknown, confirm with the owner: why 90 s exactly"
    decide the daily walk waits for the check too, rather than starting at once
      because a due daily walk at start would compete with login like the check (PR #20)
    decide a checkpoint is saved only after a quiet spell with nothing pending
      because every change before it must already be in the index (PR #20)
    decide no writes while idle
      because an idle machine should not write to the disk (PR #20)
  sources
    GitHub PR #20 "Catch up on changes made while Valafind was not running"
    src/Valafind.Core/Indexing/IndexingCoordinator.cs (RunWorker, RunIncremental, RunStartupCheck, IsFullScanDue, UpdateCheckpoint, CoordinatorTuning)
    src/Valafind.Core/Indexing/ChangeHistory.cs
    driven on Linux (Xvfb, openbox) 2026-11-25: ready 20:36:01.615, "Phase 2: scanning 1 root(s)" 20:37:31.652; ready 20:41:50.967, walk 20:43:20.985
```
