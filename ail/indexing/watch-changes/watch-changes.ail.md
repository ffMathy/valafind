# Keeping up with changes

While Valafind runs it watches the indexed folders: a file added, changed, moved or deleted is updated a few
seconds later. Changes made while it was not running are caught up shortly after it starts.

```ail
node WatchChanges : flow   level: experience
  why   A search that misses yesterday's file, or still offers a file that was deleted, cannot be trusted; and the
        user should never have to ask for the index to be refreshed.   <- Indexing
  what
    Scenario: a new file is found within seconds
      Given Valafind is up to date
      When the user saves "Inbox/<id>/added-<id>.txt" in the home folder, about a harbour full of sailing boats
      Then, a few seconds later on an idle computer and later on a busy one, searching for "harbour sailing boats" lists "added-<id>.txt"
    Scenario: an edited file is found by its new text
      Given "Inbox/<id>/edited-<id>.txt" says "notes about a chess tournament" and is listed when searching for it
      When the user replaces its text with "a recipe for mushroom risotto with parmesan"
      Then searching for "mushroom risotto" lists "edited-<id>.txt" with "mushroom risotto" in its excerpt
    Scenario: a renamed file is found under its new name only
      Given "Inbox/<id>/before-<id>.txt" about a volcano eruption is listed when searching for it
      When the user renames it to "after-<id>.txt"
      Then searching for "volcano eruption" lists "after-<id>.txt" and no longer "before-<id>.txt"
    Scenario: a moved folder takes its files along
      Given a folder "Inbox/<id>/Old" holding "moved-<id>.txt" about a desert caravan, listed under "~/Inbox/<id>/Old"
      When the user moves the folder to "Inbox/<id>/New"
      Then searching for "desert caravan" lists "moved-<id>.txt" under "~/Inbox/<id>/New"
    Scenario: a deleted file disappears from the results
      Given "Inbox/<id>/deleted-<id>.txt" about a polar bear on the ice is listed when searching for it
      When the user deletes it
      Then searching for "polar bear ice" soon no longer lists "deleted-<id>.txt"
    Scenario: a file emptied by the user disappears from the results
      Given "Inbox/<id>/emptied-<id>.txt" about a jazz trumpet solo is listed when searching for it
      When the user empties the file, keeping it
      Then searching for "jazz trumpet solo" no longer lists "emptied-<id>.txt"
    A change is handled about two seconds after the last change to the same file, so a file saved in several steps is
      read once; reading it then takes seconds, longer while other programs keep the computer busy (Pacing). Deletions are handled first, then documents, pictures, audio and video, as in a pass.
    A file added to a skipped folder (a dot folder, "node_modules", …) is ignored; a file that becomes skipped is removed.
    Changes made while Valafind was not running are found by a quiet walk of every folder about 90 seconds after it
      starts (on a Mac, from the system's own change log instead), so it stays out of the way of everything else
      starting at login. Unchanged files cost a look at the folder listing and nothing more.
    Every folder is walked again once a day, quietly: the panel keeps the last totals until the walk ends.
    A folder whose drive is unplugged or not mounted keeps its files in the index until it is back.
  how
    decide watch while running, and catch up 90 seconds after start
      because files edited, added or deleted while Valafind was closed stayed stale for up to 24 hours (PR #20); the delay keeps it out of the way of everything starting at login (README)
    decide a daily walk on top of the watchers
      because watchers can lose events or hit system limits; the daily walk picks up what they missed (PR #1, code comment)
    decide an emptied file is removed from the index
      because "unknown, confirm with the owner: keep this? an empty file can no longer be found even by its name"
    decide on Linux only indexed folders are watched
      because watching every folder (node_modules, .git, ~/.cache) can exhaust the per-user limit shared with editors and sync clients (code comment)
  uses      ChangeRules
  children  ChangeRules, StartupCheck, FsEventsHistory
  sources
    README.md, "Stays out of your way" (watching, 90 seconds, FSEvents, daily re-check)
    GitHub PR #1 (inotify on indexed folders only, daily re-check, removal of deleted files), PR #20 "Catch up on changes made while Valafind was not running"
    driven on Linux (Xvfb, openbox) 2026-11-25: files added, edited, renamed, moved and deleted in the home folder; Valafind
      stopped, a file saved, Valafind started: ready at 20:41:50.97, the log's "Phase 2: scanning 1 root(s)" at 20:43:20.98, the file listed at 20:43:29
    src/Valafind.Core/Indexing/IndexingCoordinator.cs (StartWatchers, OnWatcherEvent, DrainPending, HandleChange, RunIncremental, RunStartupCheck)
```
