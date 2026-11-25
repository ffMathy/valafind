# The log

What Valafind writes about itself, where, and how much.

```ail
node AppLog : fn   level: system
  why   When something goes wrong on a user's computer, the log is the only record of what happened; it must always
        be there, never stop the app, and stay small.   <- AppShell
  what
    LEVEL_VARIABLE: text = "VALAFIND_LOG_LEVEL"
    logLevel(value: text or nothing) -> "Trace" | "Debug" | "Information" | "Warning" | "Error" | "Critical" | "None"
      the value trimmed, any case; empty or unknown => "Information"; "None" logs nothing
    where it goes: the console (standard output) and a file in <data folder>/logs, both from the same level
    logFileName(day: text, index: integer) -> text — "valafind-<day>.log", or "valafind-<day>-<index>.log" from 1 on
    a line in the file: the time in UTC as yyyy-MM-ddTHH:mm:ss.fffZ, the level as TRACE, DEBUG, INFO, WARN, ERROR or
      FATAL (padded to five), the source, ": " and the message; an error's details follow on the next lines; driven:
      "2026-11-25T20:34:06.166Z INFO  Valafind.App.Services.Installation: Installing Valafind 9.1.0 to /…/home/.local/bin/valafind"
    a file holds one day and at most 5 MB; after that the next index; today's newest file is continued after a restart
    only the 7 newest log files are kept; older ones are deleted when a new file starts
    the folder and files are owner-only (DataLocations); a failure to write stops file logging for the run, silently
    the installer starts the log only when it has something to say, so a forwarded --toggle does not touch it
    what it records at Information, for example:
      "Valafind 1.0.0.0 starting (Ubuntu 24.04.5 LTS, UI Linux)"
      "Automatic updates are not available: Updates are turned off (VALAFIND_NO_UPDATE)"
      "Core services ready in 2192 ms (model embeddinggemma-2/model_quantized/768 on CPU (…))", which says where the model runs
      "Model not installed; waiting for it to appear";  "Installing Valafind 9.1.0 to …";  "Shutting down"
    Debug adds failed platform calls and the paths of files that could not be opened, which can be private
    exceptions nobody caught are written as Critical before the process ends
    examples
      logLevel("debug") => "Debug";  logLevel(" Warning ") => "Warning";  logLevel("verbose") => "Information";  logLevel(nothing) => "Information"
      logFileName("20261125", 0) => "valafind-20261125.log";  logFileName("20261125", 2) => "valafind-20261125-2.log"
  how
    decide a file the user can find, besides the console
      because users of the windowed app never see a console (AppLogging)
    decide Information by default, Debug only on request
      because Debug messages may contain file paths
    decide a day per file, 5 MB at most, 7 files kept
      because "unknown, confirm with the owner: why these limits"
    decide logging problems are swallowed
      because logging must never take the app down
    decide two processes may append to the same file
      because the installer and the copy it starts both log; their lines can interleave (driven)
  sources
    PR #1 ("File logging"); README.md ("the log's Core services ready line says where it runs")
    src/Valafind.App/Services/AppLogging.cs, FileLogging.cs, Log.cs; src/Valafind.App/Services/Installation.cs, LazyLog
    driven on Linux 2026-11-25: .ail/app/data/logs and the install test's log, where two processes' lines interleaved
```
