# Index status texts

How the indexer's status becomes the panel's texts, fractions and meters.

```ail
node IndexStatusText : fn   level: system
  why   The panel, the Linux banner, the tray tooltip and the Mac footer must all say the same thing about the same
        status, in every language.   <- IndexPanel
  what
    type IndexState = "starting" | "running" | "pausedSystemBusy" | "pausedOnBattery" | "pausedByUser" | "upToDate" | "modelUnavailable"
    type IndexStatus = { state: IndexState, phase: Phase, frequentDone: integer, frequentTotal: integer, allDone: integer, allTotal: integer, discoveryComplete: boolean, sample: PressureSample?, indexedByKind: map of FileKind to integer }
    type PressureSample = { cpuPercent: number, diskBusyPercent: number?, diskBytesPerSecond: number?, gpuPercent: number? }
    type StatusTexts = { shortState: text, frequentFiles: text, frequentShort: text, allPercent: text, allCaption: text, banner: text, cpuText: text, cpuHigh: boolean, cpuTarget: text, diskText: text, diskTarget: text?, upToDateTooltip: text }
    statusTexts(status: IndexStatus, maxCpu: number, maxDisk: number, maxGpu: number = 100) -> StatusTexts
    percent(fraction: number) -> text — whole percent, rounded toward zero
    throughput(bytesPerSecond: number) -> text — MB/s with one decimal under 10, whole from 10
    The panel shows unless the state is "upToDate"; the paused banner shows for the three paused states.
    allFraction is allDone ÷ allTotal (0 without a total), and 1 when up to date; searchable is (frequentDone + allDone)
      ÷ (frequentTotal + allTotal), and 1 when up to date.
    Phase 1 counts as complete when frequentDone reaches frequentTotal, or, with no total, once the phase is past Phase 1.
    The CPU text is the whole computer's use, rounded; the disk text is the throughput when known, else the busy
      percent; the disk meter fills by busy time when known, else by throughput against 150 MB/s, and its tick shows
      only with busy time. A meter is "high" over its limit.
    examples
      percent(0.963) => "96%"
      percent(0.225) => "22%"
      percent(1.2) => "100%"
      throughput(734003) => "0.7 MB/s"
      throughput(15728640) => "15 MB/s"
      throughput(0) => "0 MB/s"
      statusTexts({ state: "running", phase: "all", frequentDone: 0, frequentTotal: 0, allDone: 26, allTotal: 27, discoveryComplete: true, … }, 30, 25).allCaption => "26 of 27 · indexing"
      statusTexts({ state: "running", phase: "all", allDone: 0, allTotal: 0, discoveryComplete: false, … }, 30, 25).allCaption => "0 of 0+ · indexing"
      statusTexts({ state: "running", phase: "incremental", allDone: 0, allTotal: 0, discoveryComplete: true, … }, 30, 25).allCaption => "0 of 0 · queued"
      statusTexts({ state: "pausedByUser", allDone: 26, allTotal: 27, … }, 30, 25).shortState => "Paused by you"
      statusTexts({ state: "pausedByUser", allDone: 26, allTotal: 27, … }, 30, 25).banner => "Indexing paused — 96% of your files are searchable"
      statusTexts({ state: "running", frequentDone: 0, frequentTotal: 0, phase: "all", … }, 30, 25).frequentFiles => "0 / 0 files"
      statusTexts({ state: "running", frequentDone: 52, frequentTotal: 100, phase: "frequent", … }, 30, 25).frequentShort => "Frequent 52%"
      statusTexts({ state: "running", sample: { cpuPercent: 38.4, diskBusyPercent: 3, diskBytesPerSecond: 734003 }, … }, 50, 25).cpuTarget => "Target 45%"
      statusTexts({ state: "running", sample: { cpuPercent: 38.4, diskBusyPercent: 3, diskBytesPerSecond: 734003 }, … }, 30, 25).cpuHigh => true
      statusTexts({ state: "running", sample: { cpuPercent: 38.4, diskBusyPercent: 3, diskBytesPerSecond: 734003 }, … }, 30, 25).diskText => "0.7 MB/s"
      statusTexts({ state: "running", sample: { cpuPercent: 10, diskBusyPercent: nothing, diskBytesPerSecond: 1000 }, … }, 30, 25).diskTarget => nothing
      statusTexts({ state: "upToDate", allDone: 412, … }, 30, 25).upToDateTooltip => "Index up to date · 412 files searchable"
      statusTexts({ state: "running", sample: nothing, … }, 30, 25).cpuText => "—"
  how
    decide percentages round toward zero
      because "unknown, confirm with the owner: so that 100% is shown only when all is done?"
    decide the "user paused" captions follow the saved setting, not the indexer
      because the tray and launcher toggles must agree with their captions while the indexer is starting or has no model (code comment)
  sources
    src/Valafind.App/ViewModels/IndexStatusViewModel.cs, src/Valafind.App/ViewModels/DisplayFormat.cs
    src/Valafind.Core/Abstractions/Contracts.cs (IndexingState, IndexPhase)
    GitHub PR #12 (counts per type), PR #16 (target ticks, disk tick only with busy time)
```
