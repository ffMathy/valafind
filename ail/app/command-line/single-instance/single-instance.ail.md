# One copy per user

How a copy knows another one runs, and how a command reaches it.

```ail
node SingleInstance : fn   level: system
  why   Two copies would index the same files into the same database and fight over the shortcut; a later start
        must find the running copy and hand it the user's request.   <- CommandLine
  what
    type Command = "toggle" | "show" | "quit"
    parseCommand(arguments: list of text) -> Command | "background" | nothing
      each argument trimmed, leading "-" and "/" removed, lower case; the first that names one wins:
      "toggle" or "t" => "toggle"; "show" or "s" => "show"; "quit", "q" or "exit" => "quit"; "background" or "b" => "background"
    tryAcquire() -> Lease or nothing — nothing while another copy of this user holds it
    type Lease = { commandReceived: (Command) -> nothing, release() -> nothing }
    signalExisting(command: text) -> later boolean — false when the command is not a Command or nothing answered in 2 s
    what Main does, after the installer (SelfInstall) and before the window system starts:
      a Command given => send it to the running copy; delivered => exit 0
      "quit" and nothing delivered => exit 0 (nothing to quit)
      otherwise try to take the lease; taken => run
      not taken => with "background", exit 0; else send "show"; delivered => exit 0; else wait 250 ms and try
        again, for up to 15 s, then exit 0
    the lease: on Windows a named mutex "Local\Valafind-<user key>"; elsewhere an exclusive lock on instance.lock in
      the data folder, created readable only by the user and never deleted (the system releases the lock when the
      process dies); a data folder that cannot be used means running without a lock
    the command channel: a named pipe "valafind-<user key>"; on Linux, when XDG_RUNTIME_DIR exists, the socket
      "<XDG_RUNTIME_DIR>/valafind-<user key>.sock" (if that path is under 100 bytes); only the same user may connect
    a message is the command and a new line, at most 64 bytes of UTF-8, read within 2 s; anything else is dropped;
      up to 16 commands that arrive before the app listens are kept and delivered in order
    userKey() -> text — the first 8 bytes, as 16 lower-case hex digits, of the SHA-256 of "<domain>\<user>" in lower
      case, plus "|<home folder>" except on Windows
    examples
      parseCommand(["--toggle"]) => "toggle";  parseCommand(["-T"]) => "toggle";  parseCommand(["/show"]) => "show"
      parseCommand(["exit"]) => "quit";  parseCommand(["-b"]) => "background";  parseCommand(["--bogus"]) => nothing
      parseCommand(["--show", "--quit"]) => "show"
      signalExisting("restart") => false (not a Command, nothing is sent)
      driven: a copy runs with HOME A; a start with HOME B => a second copy runs (the key includes the home folder)
  how
    decide a lock file on macOS and Linux, a named mutex on Windows
      because the system drops a lock when the process dies, so a stale file never blocks a start (SingleInstance)
    decide the key includes the home folder on macOS and Linux
      because "unknown, confirm with the owner: two homes of one account get two copies; keep this?"
    decide the command socket lives in XDG_RUNTIME_DIR when there is one
      because that folder is private to the user, so no other user can take the name in the shared /tmp
    decide only toggle, show and quit travel between copies
      because the channel is a way in from outside; it accepts nothing else
  sources
    PR #1 ("Single instance with --toggle/--show/--quit over IPC"); PR #30 (the 15 s hand-over)
    src/Valafind.App/Platform/SingleInstance.cs; src/Valafind.App/Program.cs
    driven on Linux 2026-11-25: copies with different HOME on one display ran side by side (displays 204 and 241–246)
```
