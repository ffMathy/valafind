# Installing itself

A downloaded release is one file. Its first start installs it for the user and starts the installed copy; there is
no installer to run.

```ail
node SelfInstall : fn   level: system
  why   Downloading one file and starting it should be all it takes, without administrator rights, and later
        downloads must replace the installation instead of leaving copies around.   <- AppShell
  what
    type StartupResult = { exit: boolean, arguments: list of text, isInstalledCopy: boolean, firstInstall: boolean }
    onStartup(arguments: list of text) -> StartupResult — the first thing Main does; it never fails
    parseInstallerArguments(arguments: list of text) -> { remaining: list of text, replaces: integer or nothing, noInstall: boolean, uninstall: boolean, firstInstall: boolean }
      the options --no-install, --uninstall, --first-install and --replaces <process id>, matched exactly, are taken out
    type InstallLayout = { target: text, executable: text, stateFile: text, dataHome: text }
    installLayout(platform: "Windows" | "MacOS" | "Linux", home: text, localAppData: text, stateFolder: text, dataHome: text or nothing = nothing) -> InstallLayout
    at start, in order:
      --replaces P (from an update) => wait up to 2 min for process P if it started before this one, then end it
      not a single-file build (dotnet run, tests, "dotnet Valafind.dll"), or a store copy => run where it is
      leftovers of earlier file swaps next to the running file are removed
      --uninstall => ask the running copy to quit and wait up to 60 s; give back the cloud files Valafind requested;
        remove the autostart entry, the desktop entry, icon and shortcuts, install.json and the executable; the data
        folder stays; exit 0 (on Windows the folder is removed by a cmd started for it, after this process ends)
      running from the install location => register the desktop entry again and run (firstInstall = --first-install)
      --no-install, or VALAFIND_NO_INSTALL=1 => log "Running from {path} without installing it" and run where it is
      otherwise, one installer at a time per location (a lock waited for up to 3 min):
        installed version newer, or the same version and the same bytes (or another copy just installed it) =>
          log "Version {installed} is already installed; starting it instead of {this}"
        else => log "Installing Valafind {version} to {target}", ask the running copy to quit and wait up to 60 s,
          copy this file under a temporary name, make it executable, drop the download mark, move it into place,
          write install.json and register
        then start the installed copy with the same arguments, or with --show when there were none, adding
        --first-install when nothing was installed before; exit 0
        any failure => log "Could not install Valafind to {target}; running from {path} instead" and run where it is
    where it installs, for the user's home H and Windows' local app data L:
      Windows => L\Programs\Valafind\Valafind.exe, a Start menu shortcut (--show) and an Apps and Control Panel
        entry under HKCU\Software\Microsoft\Windows\CurrentVersion\Uninstall\Valafind that runs "--uninstall"
      macOS => H/Applications/Valafind.app: the running bundle copied, or a bundle made around the file (no Dock
        icon); the quarantine mark is removed
      Linux => H/.local/bin/valafind; the desktop entry app.valafind.Valafind.desktop and its 256 px icon under
        $XDG_DATA_HOME (H/.local/share when unset)
      install.json in the data folder: { "version": "9.1.0", "path": "<the installed file>" }
    examples
      parseInstallerArguments(["--no-install", "--replaces", "42", "-t"]) => { remaining: ["-t"], replaces: 42, noInstall: true, uninstall: false, firstInstall: false }
      installLayout("Linux", "/home/u", "", "/home/u/.local/share/valafind").executable => "/home/u/.local/bin/valafind"
      installLayout("Windows", "C:\Users\u", "C:\Users\u\AppData\Local", "s").executable => "C:\Users\u\AppData\Local\Programs\Valafind\Valafind.exe"
      installLayout("MacOS", "/Users/u", "", "s").executable => "/Users/u/Applications/Valafind.app/Contents/MacOS/Valafind"
      driven, 9.1.0 from ~/Downloads, nothing installed => installed, started with --show --first-install, autostart on, exit 0
      driven, the same file again while it runs => "Version 9.1.0 is already installed; starting it instead of 9.1.0"; its launcher opens
      driven, 9.2.0 => the running 9.1.0 quits, 9.2.0 is installed and starts with --show; then the 9.1.0 file => "Version 9.2.0 is already installed; starting it instead of 9.1.0"
      driven, --uninstall while 9.2.0 runs => it quits; executable, desktop entry, icon, autostart entry and install.json are gone; settings and logs stay
    The installed copy is the executable alone: tools/fetch-model.sh is not copied next to it
  how
    decide install for the current user, in the user's own folders
      because no administrator rights must be needed (PR #6)
    decide an older download starts the installed, newer copy instead of replacing it
      because "unknown, confirm with the owner: PR #6 states the rule, not the reason"
    decide ask the running copy to quit, rather than ending it, and wait up to 60 s
      because quitting saves the index; 15 s was too short and left the old version installed (PR #30)
    decide one installer at a time per location
      because a download opened twice installed twice and one copy kept running from Downloads (PR #36)
    decide only single-file release builds install themselves
      because dotnet run and the tests must stay where they are (RunningApp)
    decide a build without the model installs without its tools folder
      because "unknown, confirm with the owner: keep this? the installed copy then shows a command whose script is missing (ModelSetup)"
  children  StartAtLogin
  sources
    PR #6 "Install on first start and update from GitHub releases"; PR #30; PR #36; README.md, "Installing, updating and uninstalling"
    driven on Linux 2026-11-25: single-file builds 9.1.0 and 9.2.0 published locally without the model, HOME and XDG_*
      in a throwaway folder (.ail/app-install), display 245, VALAFIND_NO_UPDATE=1
    src/Valafind.App/Updates/SelfInstaller.cs, InstallLayout.cs, DesktopIntegration.cs, FileSwap.cs, InstallLock.cs
    src/Valafind.App/Services/Installation.cs
```
