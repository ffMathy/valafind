# Pacing

Indexing uses the processor and disk the computer is not using, up to the limits in Settings, and only the user (or
the battery) pauses it.

```ail
node Pacing : flow   level: experience
  why   Indexing can take days of processor time; it must never make the computer feel slow, yet must not stop every
        time something else runs, or it would never finish.   <- Indexing
  what
    Scenario: the meters mark the targets of the default limits
      Given the limits in Settings are 30 % processor and 25 % disk busy, as they start
      And the user saved new files, so indexing is under way and the launcher shows the index panel
      When the user points at the CPU meter
      Then a tooltip reads "Target 27%"
      When the user points at the Disk meter
      Then a tooltip reads "Target 22%"
    Scenario: a higher processor limit moves the CPU target
      Given indexing is under way
      When the user opens Settings, moves "Maximum CPU" to 50 % and presses Save
      And points at the CPU meter in the launcher's index panel
      Then a tooltip reads "Target 45%"
    Scenario: pausing from the launcher's menu holds new files back until the user resumes
      Given the user chose "Pause indexing" in the launcher's menu
      Then a banner reads "Indexing paused — … of your files are searchable" with a "Resume" button
      And the index panel's state reads "Paused by you"
      When the user saves "Paused/<id>/held-<id>.txt" about an orchard of apple trees
      Then searching for it does not list "held-<id>.txt", even after half a minute
      When the user presses "Resume"
      Then searching for "apple orchard" lists "held-<id>.txt" once indexing gets to it
    Each limit's target sits a little under it: the limit minus a tenth of it, at most 5 points (27 % for 30 %, 95 % for
      100 %). Indexing speeds up while the whole computer is under the targets and slows down as it nears them.
    Other programs' load slows indexing down, at worst to one file at a time with long rests, but never pauses it.
    Limits run from 5 % to 100 % in steps of 5 and apply from the next measurement, without a restart.
    While the user is searching (the launcher open with something typed), indexing finishes what it is doing and
      waits, for at most two minutes; with nothing typed it keeps going so the panel's counts move.
    "Pause indexing" (launcher menu or tray) is remembered across restarts; "Resume" or "Index Now" undoes it.
    "Index Now" also lets indexing run for the next 30 minutes on battery, and with at least one file at full speed.
    With "Pause indexing on battery power" on (as it starts), unplugging pauses indexing ("Indexing paused while on
      battery") and plugging in resumes it.
    The model running on the graphics card is held to the "Maximum GPU" limit the same way; on the processor it is not.
  how
    decide steer to just under the limits instead of pausing when the computer is busy
      because a fixed pace was far under the limit on many-core machines and over it on small ones (PR #14), and pausing for load meant indexing on a busy machine never finished (PR #16)
    decide the targets sit under the limits
      because the noise of real readings then averages out below the limit instead of around it (code comment)
    decide 30 % processor and 25 % disk by default
      because "unknown, confirm with the owner: why 30 % and 25 %"
    decide indexing waits while the user is searching, but not while the panel is merely open
      because a search must answer quickly, and the panel's counts froze exactly while the user watched them (PR #11)
    decide pause on battery is on by default
      because "unknown, confirm with the owner"
  copy
    menu.pause            "Pause indexing"
    menu.resume           "Resume indexing"
    menu.indexNow         "Index Now"
    banner.pausedByUser   "Indexing paused"
    banner.onBattery      "Indexing paused while on battery"
    banner.searchable     "{0} of your files are searchable"
    banner.resume         "Resume"
    meter.target          "Target {0}"
    settings.section      "Indexing only while the computer is idle"
    settings.cpu          "Maximum CPU"
    settings.disk         "Maximum disk busy"
    settings.gpu          "Maximum GPU"
    settings.gpuHint      "Applies while the model runs on the GPU"
    settings.battery      "Pause indexing on battery power"
  uses      PaceControl, ThrottleGate
  children  PaceControl, ThrottleGate
  sources
    README.md, "Stays out of your way"
    GitHub PRs #14 "Steer indexing speed with a PID controller", #16 "Never pause for load", #11 "Keep indexing while the launcher shows the index panel", #25 and #33 (GPU limit)
    driven on Linux (Xvfb, openbox) 2026-11-25: tooltips "Target 27%" and "Target 22%"; "Maximum CPU" moved to 50 % with the arrow keys, saved, "Target 45%"; "Pause indexing" from the launcher menu showed the banner "Indexing paused — 96% of your files are searchable", "Paused by you", "26 of 27 · paused"; "Resume" resumed it
    src/Valafind.App/Localization/Strings.resx; src/Valafind.App/Views/SettingsWindow.axaml (sliders 5–100, step 5)
    src/Valafind.App/ViewModels/LauncherViewModel.cs (ResumeNow, TogglePause, ResumeNowDuration), src/Valafind.App/Services/AppHost.cs (SetInteractive, SetUserPaused)
```
