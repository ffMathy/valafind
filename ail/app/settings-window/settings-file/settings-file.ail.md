# The saved settings

The record behind the settings window, its defaults and limits, and how it is kept on disk.

```ail
node SettingsFile : data   level: system
  why   Every setting needs one default, one valid range and one place on disk, and a broken or hand-edited file
        must never stop Valafind from starting.   <- SettingsWindow
  what
    type Settings = { Hotkey: text or nothing, Roots: list of text, RootBookmarks: map of text to text, Excludes: list of text, MaxCpuPercent: number, MaxDiskBusyPercent: number, MaxGpuPercent: number, PauseOnBattery: boolean, UserPaused: boolean, EmbeddingDimensions: integer, UseGpu: boolean, Language: text or nothing, AutomaticUpdates: boolean }
      Hotkey starts at nothing, which means DEFAULT_HOTKEY
      Roots starts empty, which means the home folder (nothing at all in the Mac App Store sandbox)
      RootBookmarks starts empty: Mac App Store only, a bookmark per folder in Roots, keyed by its path
      Excludes starts empty: extra absolute paths or folder names to skip, on top of the built-in ones; only settings.json sets them
      MaxCpuPercent starts at 30, MaxDiskBusyPercent at 25, MaxGpuPercent at 30
      PauseOnBattery starts at true; UserPaused at false (the tray's and the launcher's pause)
      EmbeddingDimensions starts at 768, one of 768, 512, 256 or 128
      UseGpu starts at true; only settings.json sets it, and it takes effect at the next start (ComputeDevice)
      Language starts at nothing, which means the system's language; else a code such as "de" or "zh-Hans"
      AutomaticUpdates starts at true
    DEFAULT_HOTKEY: text = "Ctrl+Alt+Space"
    effectiveHotkey(settings: Settings) -> text — Hotkey, or DEFAULT_HOTKEY when it is nothing or blank
    loadSettings(path: text = settingsFilePath()) -> Settings — defaults when the file is missing, unreadable or not JSON
    saveSettings(settings: Settings, path: text = settingsFilePath()) -> nothing
      makes the folder private (DataLocations), writes indented JSON to "settings.json.tmp" readable only by the
      user, then moves it over settings.json; a failure is an error the caller shows
    Save in the settings window, in this order:
      the shortcut is checked first (ShortcutRules); a problem keeps the window open and saves nothing
      a shortcut equal to DEFAULT_HOTKEY, ignoring case, or blank is saved as nothing
      the three limits are clamped to 5 through 100
      a language of "System default" is saved as nothing
      the file is written; on failure every value is put back and "Could not save settings: {reason}" is shown
      start at login is switched when it differs from the entry (StartAtLogin)
      automatic updates turned on start a check now, when the copy can update itself and is idle
      then the change is applied: new Roots, Excludes or EmbeddingDimensions restart indexing; a new Hotkey is registered
    The first start, when settings.json does not exist, saves the defaults right after the window system is up.
    examples
      effectiveHotkey(loadSettings("<missing file>")) => "Ctrl+Alt+Space"
      loadSettings("<file holding { not json>").MaxCpuPercent => 30
      loadSettings("<file holding {\"MaxCpuPercent\": 50}>").MaxDiskBusyPercent => 25
      Save with the CPU slider at 3 => MaxCpuPercent 5;  Save with the shortcut "ctrl+alt+space" => Hotkey nothing
    settings.json as the first start writes it, driven on Linux: every field above, in that order, named as above,
      indented by two spaces; Hotkey and Language as JSON's empty value, the rest at their starting values
  how
    decide a broken settings file falls back to the defaults and is overwritten by the next save
      because a hand-edited or damaged file must never stop the app (ValafindSettings.Load)
    decide write a temporary file and move it over the old one
      because a crash in the middle of a save must not leave half a file
    decide Excludes and UseGpu have no control in the window
      because "unknown, confirm with the owner: they are only reachable by editing settings.json (README names UseGpu)"
    decide the dimensions default to 768
      because 768 is the most accurate size (PR #21)
  uses      DataLocations, ShortcutRules, StartAtLogin, ComputeDevice
  sources
    src/Valafind.Core/Configuration/ValafindSettings.cs; src/Valafind.App/ViewModels/SettingsViewModel.cs, Save, Capture, Restore
    src/Valafind.App/Services/AppHost.cs, Create (first run) and SettingsHost.ApplyAsync
    README.md ("UseGpu": false in settings.json); PR #21 (768 by default); PR #25 (GPU limit)
    driven on Linux 2026-11-25: settings.json in .ail/app/data after the first start (only while driving; checks never read it)
```
