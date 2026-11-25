# The index panel

The launcher's view of indexing: Phase 1, Phase 2, the state with the processor and disk meters, and how many files
of each type are indexed. It shows only while indexing is under way, paused or waiting for the model.

```ail
node IndexPanel : view   level: experience
  why   Indexing runs for hours or days on a big home folder; the user needs to see that it progresses, how far it got,
        and that it keeps to their limits, without it cluttering the launcher once it is done.   <- Indexing
  what
    Scenario: the panel shows while new files are indexed
      Given Valafind was up to date and the user saved 25 new notes in the home folder
      When the user opens the launcher with nothing typed
      Then the "Index" panel shows "Phase 1 · Frequently used", "Phase 2 · Home folder" with a percentage, and the
        state "Indexing in the background" with the CPU and Disk meters
      And the row at the bottom counts the indexed files by type, such as "Documents 16", growing as files are done
    Scenario: the panel gives way to "Up to date"
      Given the panel shows while files are indexed
      When the last file is done
      Then the panel is gone and the footer reads "Up to date"
    Scenario: pausing shows in the panel
      When the user chooses "Pause indexing" in the launcher's menu
      Then the state reads "Paused by you" and Phase 2's caption ends "· paused"
    Phase 1 shows a check once done and "done / total files", for example "0 / 0 files" when no file was used recently.
    Phase 2 is named after what is indexed: "Home folder", the folder's name when one folder is listed, or "3 folders";
      a tooltip names them ("Everything in your home folder (~)"). Its caption is "done of total · word", with a "+"
      after the total while the walk is still counting, and the word "indexing", "queued" (waiting for its turn or
      starting), "paused", "stopped" (no model) or "done". Percentages are rounded down.
    After a restart the totals of the last finished walk are shown; a quiet re-walk keeps them until it ends.
    The CPU meter shows the whole computer's processor use with its percentage, the Disk meter the disk's busy time
      with the throughput ("0.7 MB/s"); each has a tick at its target, and a meter over its limit turns orange.
      A GPU meter appears where the system reports graphics card use. Before the first measurement the meters read "—".
    Types are counted in the filter chips' groups, without overlap: Documents (with plain text), PDFs, Spreadsheets,
      Slides, Mail, Code (with notebooks), Images, Media (audio and video) and Other (with archives); empty groups are hidden.
  look
    Under the results, above the filter chips: the title "Index" in semibold 13, then a white card with a hairline
      border and corners of 12, split into three equal columns by hairlines.
    Column one: a green check (once done) and "Phase 1 · Frequently used" in semibold 12.5; a 6-tall green bar; grey "0 / 0 files".
    Column two: the Phase 2 name at the left and the percentage at the right, both semibold 12.5; a 6-tall blue bar; the grey caption.
    Column three: the state in semibold 12.5, green while indexing, amber while paused, red without a model; two
      rows "CPU" and "Disk" in grey 11.5, each a 4-tall blue bar (orange over the limit) with a dark tick at the
      target and the value right-aligned.
    Under the columns, behind a hairline: the type counts in a wrapping row, each a grey label and a dark semibold number.
  copy
    panel.title          "Index"
    phase1               "Phase 1 · Frequently used"
    phase1.files         "{0} files"
    phase2               "Phase 2 · {0}"
    scope.home           "Home folder"
    scope.folders        "{0} folders"
    scope.tooltip.home   "Everything in your home folder ({0})"
    scope.tooltip        "Everything in {0}"
    count                "{0} of {1}"
    phase.indexing       "indexing"
    phase.queued         "queued"
    phase.paused         "paused"
    phase.stopped        "stopped"
    phase.done           "done"
    state.running        "Indexing in the background"
    state.starting       "Starting"
    state.pausedByUser   "Paused by you"
    state.onBattery      "Paused — on battery"
    state.noModel        "Model unavailable"
    meter.cpu            "CPU"
    meter.disk           "Disk"
    meter.gpu            "GPU"
    upToDate             "Up to date"
    upToDate.tooltip     "Index up to date · {0} files searchable"
  uses      IndexStatusText
  children  IndexStatusText
  sources
    GitHub PRs #4 (index progress on every launcher), #5 (panel only while indexing; quiet "up to date"), #12 (counts per type), #16 (target ticks), #27 (GPU meter)
    driven on Linux (Xvfb, openbox) 2026-11-25: 25 notes saved; panel "Phase 1 · Frequently used ✓ 0 / 0 files", "Phase 2 · Home folder 0%", "0 of 0 · queued", "Indexing in the background", "CPU 10%", "Disk 0 MB/s", "Documents 5" then "16"; after a change of folders "0 of 0+ · indexing", then "26 of 27 · indexing 96%"; paused "26 of 27 · paused"; done: "Up to date"
    src/Valafind.App/Views/Common/IndexPanelView.axaml, src/Valafind.App/Views/Linux/LinuxLauncherView.axaml (colours)
    src/Valafind.App/ViewModels/LauncherViewModel.cs (RefreshIndexScope), src/Valafind.App/Localization/Strings.resx
```
