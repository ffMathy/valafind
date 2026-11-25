# Pace control

Two feedback controllers, one on whole-computer processor use and one on disk busy time (a third on the graphics card
while the model runs there), decide how hard indexing works.

```ail
node PaceControl : fn   level: system
  why   The same limit must hold on 2 cores and on 32, whether one indexing worker keeps one core busy or three, and
        whatever else the computer is doing.   <- Pacing
  what
    type PaceLimit = "cpu" | "disk" | "gpu"
    type PidGains = { kp: number, ki: number, kd: number, outputMin: number, outputMax: number, derivativeSmoothing: number }
    type PaceReading = { maxCpu: number, machineCpu: number, ownCpu: number?, maxDisk: number, diskBusy: number?, ownDisk: number?, maxGpu: number, gpu: number?, ownGpu: number? }
    PidController(gains: PidGains, initialOutput: number) -> PidController
    PidController.update(setpoint: number, measurement: number, dt: duration, allowIncrease: boolean = true) -> number
    PidController.trackDown(output: number) -> nothing
    IndexingPace(maxConcurrency: integer, processorCount: integer, initialEffort: number = 1) -> IndexingPace
    IndexingPace.update(reading: PaceReading, dt: duration) -> nothing
    IndexingPace.effort: number — cores' worth of indexing work, from MIN_EFFORT to maxConcurrency
    IndexingPace.limitedBy: PaceLimit
    targetFor(limitPercent: number) -> number — the limit clamped to 1…100, minus the smaller of 5 and a tenth of it
    concurrencyFor(effort: number, max: integer) -> integer — effort rounded up, from 1 to max
    dutyCycleFor(effort: number, max: integer) -> number — effort ÷ concurrency, from MIN_EFFORT to 1
    restFor(busy: duration, dutyCycle: number) -> duration — busy × (1 − d) ÷ d, at most MAX_REST; none at a duty of 0.999 or more
    defaultMaxConcurrency(processorCount: integer) -> integer — the processor count, from 1 to 8
    MIN_EFFORT: number = 0.2
    MAX_REST: duration = 5 s
    SAMPLE_INTERVAL: duration = 2 s
    CPU and disk gains: kp 0.25, ki 0.15, kd 0.1, derivative smoothing 0.3; GPU gains: kp 0.12, ki 0.06, kd 0, with the GPU reading averaged (weight 0.2 per sample).
    Each controller's error is turned into units of effort by what one unit of effort was seen to cost (cores, disk
      busy points, GPU points), learnt from Valafind's own use while every allowed worker was busy (smoothing 0.3;
      cores 0.25…4, disk and GPU 5…100 points, starting at 1 core, 10 and 60 points).
    The lowest controller output is applied; the others are pulled down to it. A controller may not raise its output
      unless every allowed worker was busy during the last interval. Readings taken while paused or idle are not fed in.
    A worker rests after each processor-heavy step (extracting, an embedding batch, decoding media) for restFor(that step, dutyCycle).
    examples
      targetFor(30) => 27
      targetFor(25) => 22.5
      targetFor(50) => 45
      targetFor(100) => 95
      targetFor(5) => 4.5
      targetFor(0) => 0.9
      concurrencyFor(2.5, 8) => 3
      dutyCycleFor(2.5, 8) => 0.8333333333333334
      concurrencyFor(0.4, 8) => 1
      dutyCycleFor(0.4, 8) => 0.4
      concurrencyFor(12, 8) => 8
      dutyCycleFor(12, 8) => 1
      restFor(1 s, 0.4) => 1.5 s
      restFor(10 s, 0.2) => 5 s
      restFor(1 s, 1) => 0 s
      defaultMaxConcurrency(4) => 4
      defaultMaxConcurrency(16) => 8
  how
    decide PID controllers with conditional integration and tracking anti-windup
      because a long stretch at a limit (nothing to do, or hardware that cannot go faster) must not leave a backlog that overshoots later (code comment, PR #14)
    decide the derivative acts on the measurement, not the error
      because changing a limit must not kick the output (PR #14)
    decide the error is divided by the learnt cost of one unit of effort
      because with fixed gains the loop oscillated in simulation at 3 cores per worker (PR #14)
    decide effort never falls below 0.2
      because system load slows indexing down, it never stops it (PR #16)
    decide the lowest of the controllers wins
      because whichever resource is closer to its limit decides (PR #16)
    decide at most 8 files at once
      because each concurrent model run holds its own working memory (code comment)
  sources
    GitHub PR #14 (4-core VM: 120 files took 118 s at a 100 % limit, 372 s at 30 % with mean CPU 27.4 %), PR #16 (disk loop, never pause), PR #33 (GPU limit steadied)
    src/Valafind.Core/Indexing/PidController.cs, src/Valafind.Core/Indexing/IndexingPace.cs, src/Valafind.Core/Indexing/IndexingWorkers.cs
```
