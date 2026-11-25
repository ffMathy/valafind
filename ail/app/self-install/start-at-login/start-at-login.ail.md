# Starting at login

The entry that starts Valafind in the background when the user signs in, one per platform.

```ail
node StartAtLogin : fn   level: system
  why   Indexing runs while the computer is idle, and the shortcut only works while Valafind runs, so it should
        already be running, out of sight, when the user signs in.   <- SelfInstall
  what
    BACKGROUND_ARGUMENT: text = "--background"
    isAutostartEnabled() -> boolean — whether the entry exists (and, on Linux, is not hidden or disabled)
    setAutostartEnabled(enabled: boolean) -> nothing — writes the entry for the running file, or removes it
    launchCommand() -> list of text — [the running executable, "--background"], or [dotnet, Valafind.dll,
      "--background"] when started as "dotnet Valafind.dll"; inside a Mac bundle, the bundle's main executable
    per platform:
      Windows => the value "Valafind" under HKCU\Software\Microsoft\Windows\CurrentVersion\Run: each part in double
        quotes, but --background bare, joined by spaces
      macOS => ~/Library/LaunchAgents/app.valafind.plist: Label app.valafind, ProgramArguments the command,
        RunAtLoad true, ProcessType Interactive, LimitLoadToSessionType Aqua
      macOS App Store copy (macOS 13 and later) => a login item of the app, through the system
      Microsoft Store copy => the package's startup task; Windows switches it, and the settings say where
      Linux => $XDG_CONFIG_HOME/autostart/valafind.desktop (H/.config when unset or not absolute), written through a
        temporary file and a move, holding the lines "[Desktop Entry]", "Type=Application", "Name=Valafind",
        "Comment=Semantic local file search", "Exec=<the command, quoted>", "Terminal=false", "NoDisplay=true",
        "X-GNOME-Autostart-enabled=true", "X-KDE-autostart-after=panel"
    quoteExecArgument(argument: text) -> text — Desktop Entry quoting: in double quotes with \ " ` $ escaped by a
      backslash, then every % doubled and every backslash doubled again
    it is switched on:
      by the installer's first start of a fresh install (--first-install)
      on every start of the installed copy whose entry already exists, so the entry points at the installed file
      by Save in the settings, when "Start Valafind when I log in" differs from the entry
      and removed by --uninstall
    examples
      quoteExecArgument("/opt/My App/run%1") => "\"/opt/My App/run%%1\""
      quoteExecArgument("a$b") => "\"a\\\\$b\""
      driven, Linux, installed copy => Exec="/…/home/.local/bin/valafind" "--background"
      driven, Linux, the switch on and Save in a copy run as dotnet => Exec="/root/.dotnet/dotnet" "/…/Valafind.dll" "--background"
      driven, the switch off and Save => the file is gone
      a Linux entry holding "Hidden=true" or "X-GNOME-Autostart-enabled=false" in its [Desktop Entry] => isAutostartEnabled() is false
  how
    decide each platform's own per-user mechanism, needing no administrator rights
      because nothing Valafind does may need elevation (PR #1, Autostart)
    decide a fresh install turns it on
      because "unknown, confirm with the owner: PR #6 says so, not why"
    decide start with --background
      because a start at login must not show the launcher; and when a copy already runs it leaves quietly (CommandLine)
  sources
    PR #1; PR #6 ("A fresh install turns on start at login; later starts point an existing autostart entry at the installed copy")
    driven on Linux 2026-11-25: the installer's entry (.ail/app-install), the settings switch on display 204 and 245
    src/Valafind.App/Platform/Autostart.cs; src/Valafind.App/Services/AppHost.cs, ConfigureInstalledCopy
```
