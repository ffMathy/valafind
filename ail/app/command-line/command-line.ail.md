# The command line

Starting Valafind again never starts a second copy: the start is handed to the copy that runs. A few options say
what that copy should do, which is how a desktop shortcut on Wayland opens the launcher.

```ail
node CommandLine : flow   level: experience
  why   A second copy would index the same files twice and fight over the shortcut; and where the desktop does not
        let Valafind grab keys, a shortcut bound to a command must be able to open and close the launcher.   <- AppShell
  what
    Scenario: --toggle opens the launcher and closes it again
      Given Valafind runs and its launcher is closed
      When the user runs Valafind with --toggle
      Then the launcher opens, and the command ends at once with exit code 0
      When the user runs Valafind with --toggle again
      Then the launcher closes
    Scenario: --show opens the launcher
      Given Valafind runs and its launcher is closed
      When the user runs Valafind with --show
      Then the launcher opens
    Scenario: starting Valafind again shows the launcher of the copy that runs
      Given Valafind runs and its launcher is closed
      When the user starts Valafind again, with no options
      Then the launcher of the running copy opens, and the new start ends with exit code 0
    Scenario: --background leaves quietly when Valafind already runs
      Given Valafind runs and its launcher is closed
      When the user runs Valafind with --background
      Then nothing opens, and the command ends with exit code 0
    Scenario: --quit ends the copy that runs
      Given Valafind runs in the background
      When the user runs Valafind with --quit
      Then that copy ends within a few seconds, and the command ends with exit code 0
    Scenario: without a display Valafind says so and ends
      Given no graphical session (no DISPLAY and no WAYLAND_DISPLAY) and no copy running
      When the user starts Valafind
      Then it writes "Valafind could not start its user interface (no graphical session?)" with the reason
      And where the log folder is, and ends with exit code 1
      And before those lines the same error is logged, with its technical details, as the log writes to the console too
    The options may be written --toggle, -t or toggle, --show, -s or show, --quit, -q, quit or exit, --background, -b
      or background, in any case; the first one wins. Anything else is ignored, so it starts or shows Valafind.
    --quit when no copy runs does nothing and ends with exit code 0.
    --background when no copy runs starts Valafind in the tray without showing the launcher; it is what the entry for
      starting at login runs.
    --toggle starts Valafind with its launcher open when no copy runs; a launcher opened by --toggle closes again
      when the user clicks elsewhere, like one opened with the shortcut. --show, or starting again, leaves it open.
    While the running copy is quitting to update itself, a start keeps trying for up to 15 seconds, and the new
      version then opens its launcher.
    The installer's own options (--no-install, --uninstall) are described with SelfInstall.
  how
    decide forward the command to the running copy instead of starting another
      because one index and one shortcut per user (PR #1, "Single instance with --toggle/--show/--quit over IPC")
    decide a start at login leaves at once when a copy already runs, without showing anything
      because the user did not ask to see it
    decide a launch made while an update restarts keeps trying for 15 seconds
      because such a launch used to be lost: the user clicked Valafind and nothing appeared (PR #30)
  uses      SingleInstance
  children  SingleInstance
  sources
    PR #1; PR #30 "Keep Valafind visible across updates and installs"; README.md, "Keyboard" (Wayland: bind --toggle)
    driven on Linux 2026-11-25 against the copy on display 204, with its home and data folder: each option, -t,
      --bogus (shows the launcher); the installed single-file 9.2.0 in a throwaway HOME on display 245: --quit, --quit
      with nothing running; without DISPLAY in a fresh home: exit code 1
    src/Valafind.App/Program.cs, Main, ParseCommand, AcquireOrForward; src/Valafind.App/Services/AppHost.cs, OnInstanceCommand
```
