# The app around the search

Everything that is not searching or indexing itself: the tray icon and its menu, the settings window, starting once
per user and the command line, installing and updating itself, the first start without the model, where the data
lives, the log, the three designs and the 21 languages.

```
app-shell.ail.md                                              AppShell (this file)
tray-menu/tray-menu.ail.md                                    TrayMenu: the icon and its menu
settings-window/settings-window.ail.md                        SettingsWindow: the window, its text and layout
settings-window/change-settings/change-settings.ail.md        ChangeSettings: what each control does, Save and Cancel
settings-window/settings-file/settings-file.ail.md            SettingsFile: settings.json, defaults and ranges
settings-window/shortcut-rules/shortcut-rules.ail.md          ShortcutRules: which chords work, and the Wayland help
command-line/command-line.ail.md                              CommandLine: --toggle, --show, --quit, --background
command-line/single-instance/single-instance.ail.md           SingleInstance: one copy per user, commands between copies
self-install/self-install.ail.md                              SelfInstall: install on first start, --no-install, --uninstall
self-install/start-at-login/start-at-login.ail.md             StartAtLogin: the autostart entry per platform
auto-update/auto-update.ail.md                                AutoUpdate: new releases from GitHub
model-setup/model-setup.ail.md                                ModelSetup: a build without the model, noticed without a restart
model-setup/model-install-help/model-install-help.ail.md      ModelInstallHelp: the command that installs the model
data-locations/data-locations.ail.md                          DataLocations: the data folder, private to the user
app-log/app-log.ail.md                                        AppLog: the log files and VALAFIND_LOG_LEVEL
ui-flavours/ui-flavours.ail.md                                UiFlavours: the Windows, Mac and Linux designs
languages/languages.ail.md                                    Languages: 21 languages, right to left for Arabic and Urdu
languages/language-rules/language-rules.ail.md                LanguageRules: the list, the choice and the direction
```

```ail
node AppShell : flow   level: experience
  why   Valafind waits out of sight until it is summoned, so it needs a quiet home around the launcher: a sign that it
        runs, one place for every choice, a way to start with the computer and stay current, and a first start that
        works without any setup.   <- Valafind
  what
    Valafind has no window of its own besides the launcher. It waits in the tray; the tray icon, its menu, the global
      shortcut and the command line reach it.                                     refined-by: TrayMenu, CommandLine
    The settings window holds the shortcut, the indexed folders, the CPU, disk and GPU limits, pause on battery, the
      embedding size, the language, start at login and automatic updates; nothing changes until Save.   refined-by: SettingsWindow
    Only one copy runs per user; starting it again shows the launcher of the copy that runs.   refined-by: CommandLine
    The first start of a downloaded release installs it for the user, without administrator rights, and starts it at
      login.                                                                      refined-by: SelfInstall
    It updates itself from GitHub releases a minute after it starts and every six hours.   refined-by: AutoUpdate
    A build without the model shows the command that installs it and notices the model without a restart.   refined-by: ModelSetup
    The settings, the index, the model and the logs stay in one data folder that only the user can read.   refined-by: DataLocations, AppLog
    Windows, Mac and Linux each get a launcher designed for them, in 21 languages.   refined-by: UiFlavours, Languages
    The very first start, with no settings saved yet, opens the launcher with its welcome; later starts wait in the
      tray until summoned. A start at login (the background option) never shows a window.
    Started where there is no graphical session, it writes "Valafind could not start its user interface (no graphical
      session?)" with the reason, and where the log folder is, and ends with exit code 1.
  how
    decide the tray icon and the shortcut are the way in; there is no main window or taskbar button besides the launcher
      because "unknown, confirm with the owner: PR #1 describes the tray and the launcher, not why there is no window"
    decide the first start shows the launcher once, then saves the defaults so it never shows by itself again
      because a fresh install must visibly do something, and a start at login must not (PR #30)
    decide a start without a display ends with a clear message and exit code 1 instead of a crash
      because an autostart entry can fire before the display is ready, or the app is started over SSH (Program)
  copy
    error.ui        "Valafind could not start its user interface (no graphical session?): {reason}"
    error.crash     "Valafind stopped after an unexpected error: {reason}"
    error.details   "Details are in the log folder: {log folder}"
  children  TrayMenu, SettingsWindow, CommandLine, SelfInstall, AutoUpdate, ModelSetup, DataLocations, AppLog, UiFlavours, Languages
  sources
    README.md, the first part and the sections "Get it", "Languages", "Where your data lives"
    PR #1 "Valafind: local semantic file search" (tray, single instance, first-run screen, owner-only data)
    PR #6 (install and update), PR #10 (languages), PR #13 (settings redesign), PR #30 (visible across installs)
    driven on Linux (Xvfb 1280x800, openbox, xfce4-panel tray) 2026-11-25 on display 204: tray, settings, command line;
      without DISPLAY: exit code 1 and the message above
    src/Valafind.App/Program.cs, Main and RunGuarded; src/Valafind.App/Services/AppHost.cs, Create and Start
```
