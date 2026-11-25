# The tray icon and its menu

The one part of Valafind that is always on screen: an icon in the system tray (the menu bar on a Mac), with a menu.

```ail
node TrayMenu : flow   level: experience
  why   With no window of its own, Valafind needs a visible sign that it runs, a way to open it with the mouse, and a
        place to pause indexing, reach the settings and quit.   <- AppShell
  what
    Scenario: clicking the tray icon opens the launcher
      Given Valafind runs and its icon is in the tray
      When the user clicks the icon
      Then the launcher opens
    Scenario: Open Valafind opens the launcher
      When the user chooses "Open Valafind" in the tray menu
      Then the launcher opens
    Scenario: Pause indexing pauses until Resume indexing
      When the user chooses "Pause indexing" in the tray menu
      Then the launcher says "Indexing paused" and "Paused by you", with a "Resume" button
      And the tray menu now offers "Resume indexing" in its place
      When the user chooses "Resume indexing"
      Then the launcher no longer says "Indexing paused"
    Scenario: Settings… opens the settings, and only once
      When the user chooses "Settings…" in the tray menu
      Then the settings window opens
      When the user chooses "Settings…" again
      Then the same window comes forward and no second one opens
    Scenario: Check for updates… opens the releases page when this copy cannot update itself
      Given this copy cannot update itself, as a copy started from a build folder cannot
      When the user chooses "Check for updates…"
      Then the browser opens the project's releases page on GitHub
      And the settings window does not open
    A copy that can update itself checks at once on "Check for updates…" and opens the settings window, whose
      "Install new versions automatically" row shows how the check goes.
    Once an update is downloaded, "Check for updates…" reads "Restart to update" and restarts into it at once,
      with the launcher open.
    "Quit Valafind" stops indexing, removes the icon and ends Valafind until it is started again.
    Pointing at the icon shows "Valafind — " and the index state, for example "Valafind — Up to date".
    The pause is kept: after a restart indexing stays paused until the user resumes it. The launcher's own pause and
      resume and the tray item are the same switch.
    Opening the settings hides the launcher.
  how
    decide a left click opens the launcher, not the settings
      because "unknown, confirm with the owner: PR #1 says only that the tray has Open, Pause, Settings and Quit"
    decide pausing is the user's choice only and is remembered across restarts
      because indexing never pauses for load; only the user (or running on battery) pauses it (PR #16, README)
    decide "Check for updates…" in a copy that cannot update itself opens the releases page instead
      because such a copy (a build from source, a store copy) must be updated the way it was installed (PR #6)
    decide the menu names the shortcut next to "Open Valafind" where the platform's menus show shortcuts
      because the shortcut is how Valafind is meant to be opened (TrayIconController.SetHotkey); the Linux panel
        menu driven here does not show it
  copy
    tray.open        "Open Valafind"            "Open Valafind (Ctrl+Alt+Space)" where the shortcut cannot be shown as a key
    tray.pause       "Pause indexing"           "Resume indexing" while paused
    tray.settings    "Settings…"
    tray.updates     "Check for updates…"       "Restart to update" once an update is downloaded
    tray.quit        "Quit Valafind"
    tray.tooltip     "Valafind — {index state}"
  look
    menu        the platform's own menu, in this order: Open Valafind, a separator, Pause indexing, Settings…, Check for
                updates…, a separator, Quit Valafind; both separators are thin, light grey lines across the menu; it is not
                mirrored in Arabic or Urdu
    icon        Valafind's magnifier in a rounded blue square; on a Mac a template image that follows the menu bar
  uses      ChangeSettings, AutoUpdate
  sources
    PR #1 (tray menu), PR #6 ("Check for updates…"), PR #16 (never pause for load), PR #30 ("Restart to update")
    driven on Linux (Xvfb, openbox, xfce4-panel tray) 2026-11-25 on display 204: the menu (screenshot), each item, a left
      click; the releases page recorded by the stand-in for xdg-open; the Arabic menu on display 244
    src/Valafind.App/Services/TrayIconController.cs; src/Valafind.App/Services/AppHost.cs, Start, TogglePause, CheckForUpdates, Quit
    src/Valafind.App/Localization/Strings.resx, OpenValafind, PauseIndexing, ResumeIndexing, SettingsMenu, CheckForUpdatesMenu, QuitValafind
```
