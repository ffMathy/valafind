# Throttle gate

Whether indexing may run at all, the samples that feed the pace, and the hold while the user is searching.

```ail
node ThrottleGate : fn   level: system
  why   Only the user's own choices may stop indexing; everything else only changes its speed, which the gate's
        samples steer.   <- Pacing
  what
    type GateState = "running" | "pausedOnBattery" | "pausedByUser"
    type ThrottleOptions = { maxCpuPercent: number, maxDiskBusyPercent: number, pauseOnBattery: boolean, maxGpuPercent: number, steerGpu: boolean }
    ThrottleGate(monitor: PressureMonitor, options: () -> ThrottleOptions) -> ThrottleGate — options are read on every sample, so changed settings apply at once
    ThrottleGate.setUserPaused(paused: boolean) -> nothing — takes effect immediately
    ThrottleGate.overrideUntil: time? — "Index Now": battery ignored until then, and the pace kept at one full worker at least
    ThrottleGate.setIdle(idle: boolean) -> nothing
    ThrottleGate.waitUntilOpen() -> later nothing
    ThrottleGate.state: GateState
    stateFor(userPaused: boolean, overrideActive: boolean, onBattery: boolean, pauseOnBattery: boolean) -> GateState
    ownCpuPercent(cpuTime: duration, wall: duration, processors: integer) -> number — cpuTime ÷ (wall × processors) × 100, from 0 to 100
    SAMPLE_INTERVAL: duration = 2 s
    IDLE_SAMPLE_INTERVAL: duration = 60 s
    INTERACTIVE_TIMEOUT: duration = 2 min
    INDEX_NOW_DURATION: duration = 30 min
    The gate starts open and samples right away. While the indexer is idle and nobody waits, it samples every 60 s; new
      work or a waiter wakes it for a fresh sample at once. A failing monitor holds the pace where it is.
    Each sample notes Valafind's own processor time and disk bytes since the last one, so the panel and the pace can
      tell its share; disk busy time is scaled by the share of throughput that was not Valafind's when over 64 KB/s.
    The state "paused, system busy" still has its texts but is never entered.
    Interactive hold: while the host says the user is searching, a worker finishes its model batch (2 documents) and
      waits before the next file or batch, without changing the state, until searching stops or INTERACTIVE_TIMEOUT
      has passed since it began.
    examples
      stateFor(true, true, true, true) => "pausedByUser"
      stateFor(false, false, true, true) => "pausedOnBattery"
      stateFor(false, true, true, true) => "running"
      stateFor(false, false, true, false) => "running"
      stateFor(false, false, false, true) => "running"
      ownCpuPercent(2 s, 2 s, 4) => 25
      ownCpuPercent(0 s, 2 s, 4) => 0
      ownCpuPercent(16 s, 2 s, 4) => 100
  how
    decide the gate closes only for the user or the battery rule
      because system load slows indexing instead (PR #16)
    decide the hold while searching is limited to two minutes
      because a launcher left open with a query must not starve indexing (code comment)
    decide sampling slows to once a minute while idle
      because an idle indexer should not wake the computer every two seconds (code comment)
    decide "Index Now" lasts 30 minutes
      because "unknown, confirm with the owner: why 30 minutes"
  sources
    GitHub PRs #1 (pressure monitors, own load excluded), #11 (hold only while searching), #16 (gate only for user and battery)
    src/Valafind.Core/Indexing/ThrottleGate.cs, src/Valafind.Core/Indexing/IndexingCoordinator.cs (SetInteractive, WaitWhileInteractive, CoordinatorTuning)
    src/Valafind.App/ViewModels/LauncherViewModel.cs (ResumeNowDuration), src/Valafind.App/Services/AppHost.cs
```
