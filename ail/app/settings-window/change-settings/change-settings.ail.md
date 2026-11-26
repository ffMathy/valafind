# Changing a setting

What each control in the settings window does, and what Save and Cancel do with it.

```ail
node ChangeSettings : flow   level: experience
  why   A choice must be easy to make and easy to take back: nothing should change by accident while looking around,
        and a shortcut that cannot work must be caught before it replaces one that does.   <- SettingsWindow
  what
    Scenario: Save with a new shortcut moves the launcher to the new keys
      Given the shortcut is Ctrl+Alt+Space
      When the user clicks the shortcut box, presses Ctrl+Alt+K and clicks "Save"
      Then the window closes
      And Ctrl+Alt+K opens the launcher, whose hint now names Ctrl+Alt+K
      And Ctrl+Alt+Space no longer opens it
    Scenario: a shortcut that cannot be used keeps the window open with the reason
      When the user presses Ctrl+Escape in the shortcut box and clicks "Save"
      Then the window stays open and says "Escape cannot be used as a global shortcut key."
      And Ctrl+Alt+Space still opens the launcher
    Scenario: Reset puts the default shortcut back
      Given the user pressed Ctrl+Alt+K in the shortcut box
      When the user clicks "Reset"
      Then the box reads "Ctrl+Alt+Space" again
    Scenario: Esc forgets a changed limit
      When the user moves Maximum CPU two steps up, to 40%, and presses Esc
      Then the window closes
      And when the settings open again, Maximum CPU reads 30%
    Scenario: Save keeps a changed limit
      When the user moves Maximum disk busy two steps up, to 35%, and clicks "Save"
      Then when the settings open again, Maximum disk busy reads 35%
    Scenario: adding a folder replaces the home folder in the list
      Given the folders card shows "Your home folder"
      When the user clicks "Add folder…" and chooses a folder in the home folder
      Then the card lists that folder with "Remove", and "Your home folder" is gone
      When the user clicks "Remove"
      Then the card shows "Your home folder" again
    Scenario: choosing another language says it applies at the next start
      When the user chooses "Deutsch" as the language
      Then "Takes effect the next time Valafind starts." appears under "Language", still in English
    Scenario: choosing other dimensions warns that every file is indexed again
      When the user chooses 256 as the dimensions
      Then "Changing the dimensions re-indexes all files." appears
    Scenario: Start Valafind when I log in adds an entry that starts it at sign-in
      When the user turns on "Start Valafind when I log in" and clicks "Save"
      Then the desktop's autostart folder holds a Valafind entry that starts it in the background
      When the user turns it off and clicks "Save"
      Then the entry is gone
    Scenario: Open log folder opens the folder of the logs
      When the user clicks "Open log folder"
      Then the file manager opens the "logs" folder in Valafind's data folder
    Scenario: the Check for updates button opens the releases page when this copy cannot update itself
      Given this copy cannot update itself, as a copy started from a build folder cannot
      When the user clicks "Check for updates"
      Then the browser opens the project's releases page on GitHub
    Saving new folders or new dimensions starts indexing again; saving while the model is missing or failed
      looks for the model again. Other limits apply to indexing as soon as they are saved.
    A shortcut is registered again only when it changed, or when the last one failed. When the new one cannot be
      registered the window stays open with the reason, and the setting is saved all the same.
    Turning on automatic updates and saving checks for an update at once, in a copy that can update itself.
    Adding a folder that is already listed does nothing.
  how
    decide check the shortcut before anything is saved, and keep the window open on a problem
      because "unknown, confirm with the owner: a shortcut that registers but fails later is saved anyway; keep this?"
    decide the switch for start at login writes or removes the entry itself, on Save, rather than a saved setting
      because the entry, not a setting, is what makes it start (Autostart); "unknown, confirm with the owner"
    decide limits stay between 5% and 100%, in steps of 5
      because "unknown, confirm with the owner: why 5 as the lowest limit"
    decide the language waits for the next start
      because "unknown, confirm with the owner: PR #10 says it applies at the next start, not why not at once"
  uses      SettingsFile, ShortcutRules, StartAtLogin, AutoUpdate, DataLocations
  sources
    PR #10 (language at the next start), PR #13 (switches, sliders), PR #6 (updates switch and button)
    driven on Linux (Xvfb, openbox, xfce4-panel tray) 2026-11-25 on display 204: each scenario; the folder picker is
      the desktop's (GTK) dialog; the autostart entry read in the throwaway home's .config/autostart
    src/Valafind.App/ViewModels/SettingsViewModel.cs, Save, Cancel, AddRoot, RemoveRoot, ResetHotkey, CheckForUpdates
    src/Valafind.App/Services/AppHost.cs, SettingsHost.ApplyAsync; src/Valafind.App/Controls/HotkeyBox.cs
```
