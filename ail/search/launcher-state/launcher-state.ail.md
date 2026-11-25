# The launcher's state

The rules behind the launcher, without its look: when a search runs, which answer is shown, what the main area shows,
and which result is chosen. Shared by the Windows, macOS and Linux designs.

```ail
node LauncherState : fn   level: system
  why   Every design must behave the same while the user types quickly, and a late answer must never replace a newer one.   <- SearchFiles
  what
    type LauncherContent = "hint" | "results" | "noResults" | "error" | "modelLoading" | "modelMissing" | "modelFailed"
    type ModelState = "loading" | "ready" | "missing" | "failed"
    DEBOUNCE_DELAY: duration = 150 ms
    MAX_RESULTS: integer = 30
    LauncherViewModel.query: text — every change schedules a search after DEBOUNCE_DELAY; a newer change cancels the older one
    LauncherViewModel.selectFilter(chip: FilterChip) -> nothing — searches again at once; the chosen chip again => nothing happens
    LauncherViewModel.selectTimeRange(range: TimeRange) -> nothing — likewise
    LauncherViewModel.buildFilter() -> { kinds: set of FileKind or nothing, modifiedAfter: time or nothing } — modifiedAfter is now minus the range's age
    LauncherViewModel.moveUp() -> nothing;  LauncherViewModel.moveDown() -> nothing
    LauncherViewModel.onShown() -> nothing — keeps the query, the results and the choice; with results and none chosen, chooses the first
    LauncherViewModel.isSearchActive: boolean — the launcher is on screen and the query is not blank; indexing waits while it is true
    type FilterChip = { label: text, kinds: set of FileKind or nothing }
    type TimeRange = { label: text, maxAge: duration or nothing }
    content(model: ModelState, error: text or nothing, hasQuery: boolean, resultCount: integer, isSearching: boolean, previous: LauncherContent) -> LauncherContent
    examples
      content("missing", nothing, true, 3, false, "results") => "modelMissing"
      content("loading", nothing, false, 0, false, "hint") => "modelLoading"
      content("ready", "boom", true, 0, false, "results") => "error"
      content("ready", nothing, true, 3, false, "hint") => "results"
      content("ready", nothing, true, 0, false, "results") => "noResults"
      content("ready", nothing, true, 0, true, "results") => "results"
      content("ready", nothing, true, 0, true, "hint") => "hint"
      content("ready", nothing, false, 0, false, "results") => "hint"
      resultSummary(1, 38 ms) => "1 result · 38 ms";  resultSummary(6, 0.2 ms) => "6 results · 1 ms"
      moveDown() with 3 results, 2 chosen => 2 chosen;  moveUp() with 0 chosen => 0 chosen;  moveUp() with no results => nothing chosen
      buildFilter() with "All" and "Any time" => { kinds: nothing, modifiedAfter: nothing }
      buildFilter() with "Code" and "Past week" at 2026-11-25 12:00:00 => { kinds: { code, notebook }, modifiedAfter: 2026-11-18 12:00:00 }
  how
    decide a trimmed, blank query clears the results at once and searches nothing
      because there is nothing to embed (SearchService returns nothing for blank text)
    decide each search gets a generation number; results, explanations and errors of an older generation are dropped
      because a slow answer to an older query must never replace a newer one
    decide new results replace the list, choose the first, load the pictures' thumbnails a few at a time from the top, and start the explanation pass with the same cancellation
      because the next keystroke cancels the explanation along with the search (LauncherViewModel, RunExplainAsync)
    decide explanations patch the shown rows by path, in place, only when their count and paths match the shown results
      because the list is not rebuilt and the choice stays (LauncherViewModel, ApplyExplanations)
    decide the chosen row's preview loads the first time it is chosen while the pane is shown; choosing another cancels it
      because "unknown, confirm with the owner"
    decide the time shown is the search alone, rounded, at least 1 ms; the 150 ms wait before it is not counted
      because "unknown, confirm with the owner"
    decide while the model loads, is missing or failed, the search is detached; results in flight are dropped
      because no search may reach services that are being disposed (LauncherViewModel, SetModelLoading)
    decide with the model ready again, a query already typed is searched at once
      because "unknown, confirm with the owner"
  verify
    property: whatever the order the answers arrive in, the results shown belong to the last query typed
    property: the summary's count equals the number of rows shown
  sources
    src/Valafind.App/ViewModels/LauncherViewModel.cs (UpdateContent, ScheduleSearch, RunSearchAsync, RunExplainAsync,
      ApplyResults, ApplyExplanations, BuildFilter, MoveUp, MoveDown, OnShown); FilterChipViewModel.cs
    PR #11 (indexing waits only while a query is shown); PR #22 (thumbnails and previews)
    content examples traced by hand from UpdateContent; summary and move examples from ApplyResults, MoveUp, MoveDown
```
