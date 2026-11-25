# The settings window

One window with every choice Valafind offers, in five cards, and Cancel and Save at the bottom.

```ail
node SettingsWindow : view   level: experience
  why   People need one place for the shortcut, what is searched, how much of the computer indexing may use, and
        the language, start at login and updates, with the version to hand when something goes wrong.   <- AppShell
  what
    It opens from "Settings…" in the tray menu, from the settings button in the launcher, and by itself on a Mac
      App Store copy that has no folders yet. Opening it hides the launcher.
    Only one settings window is ever open; opening it again brings it forward.
    Nothing changes until Save; Cancel, Esc and the window's close button forget every change.
    Scenario: the settings window shows every setting with its current value
      Given the settings were never changed, on Linux, with the model in place and Valafind started from a build folder
      When the settings window opens
      Then the shortcut reads "Ctrl+Alt+Space" with "Default: Ctrl+Alt+Space" under it
      And the folders card shows "Your home folder" and the home folder's path
      And Maximum CPU reads 30%, Maximum disk busy 25% and Maximum GPU 30%, and pause on battery is on
      And Dimensions reads 768, and the model row says "Detected:" and the model's folder
      And Language reads "System default", start at login is off and automatic updates are on
      And the version and the log folder are shown at the end
  look
    window      titled with the "title" copy, 580 by 720, at least 480 by 480, resizable, centred on the screen, the
                app icon; the page is a light grey tint and scrolls, the footer stays at the bottom
    heading     an accent icon and a semibold title over each card: keyboard, folder, processor, magnifier, gear
    card        white, translucent, a hairline border and rounded corners; each setting a row with its title, a smaller
                grey hint under it, and its control on the trailing side; rows split by hairlines
    shortcut    a box that records the keys pressed into it, a Reset button beside it, the default under it, and a
                message in dark amber under that when the shortcut cannot be used
    folders     one row per folder with a folder tile, the path cut in its middle when too long, and Remove; with no
                folder, one row naming the home folder; then the hint and "Add folder…" with a plus
    limits      three sliders the full width of the card, 5 to 100 in steps of 5, the value in accent on the title line;
                the GPU row has its hint; then the pause on battery switch
    model       Dimensions as a drop-down of 768, 512, 256 and 128; a pale yellow warning with a warning icon when the
                choice differs from the saved one; the model row; a lock and the privacy line
    general     Language as a drop-down of "System default" and the 21 languages, each in its own script, with the
                restart hint under it once changed; the start at login switch (on a Microsoft Store copy, a hint in
                its place); the updates switch with its status under it and a "Check for updates" button; the version,
                selectable; the log folder cut in the middle, with "Open log folder"
    footer      its own pale surface under a hairline: an error in red on the leading side, Cancel and the accent Save
                (both at least 96 wide) on the trailing side
    direction   right to left in Arabic and Urdu
    each look   the same window in every design (UiFlavours); on Windows it lets Mica show through the cards
  copy
    title        "Valafind Settings"
    shortcut     "Global shortcut"              with "Default: {chord}", "Reset", and the box's placeholder "Press a key combination"
    folders      "Indexed folders"              rows "Your home folder ({path})", "Remove", "Add folder…"; the picker is titled "Choose a folder to index"
    foldersHint  "Only the folders listed here are indexed — by default your home folder. Add other drives, external disks or network folders with “Add folder…”. Adding a folder replaces the default, so add your home folder too if you still want it."
    limits       "Indexing only while the computer is idle"   rows "Maximum CPU", "Maximum disk busy", "Maximum GPU" with "Applies while the model runs on the GPU", "Pause indexing on battery power"
    model        "Embedding model"              "Dimensions" with "Smaller vectors use less memory and search faster; 768 is most accurate."
    reindex      "Changing the dimensions re-indexes all files."
    detected     "Detected: {folder}"           or ModelInstallHelp's "Not found — …"
    privacy      "Runs entirely on this computer — your files and searches never leave it."
    general      "General"                      rows "Language" ("System default"; hint "Takes effect the next time Valafind starts."), "Start Valafind when I log in", "Install new versions automatically", "Version", "Logs: {folder}" with "Open log folder"
    storeLogin   "Windows starts Valafind when you log in. Change this in Settings › Apps › Startup."
    footer       "Cancel", "Save"
    errors       "Could not save settings: {reason}", "Settings saved, but start at login could not be changed: {reason}", "Could not open the releases page: {reason}"
  how
    decide a Save button, not changes that apply at once
      because "unknown, confirm with the owner: folders and dimensions restart indexing, but every setting waits for Save"
    decide the folders, the limits, the model and the rest each get a card with a heading
      because the old window had no visible cards and nothing lined up (PR #13)
    decide the window scrolls inside a fixed size, the footer always visible
      because "unknown, confirm with the owner: on a screen 800 high the footer sits under a bottom panel"
  uses      ChangeSettings, SettingsFile, ShortcutRules, ModelInstallHelp, AutoUpdate, StartAtLogin, Languages, UiFlavours
  children  ChangeSettings, SettingsFile, ShortcutRules
  sources
    PR #13 "Redesign the settings window"; PR #28 (version row); PR #31 (no custom model folder); PR #10 (language)
    driven on Linux (Xvfb 1280x800, openbox) 2026-11-25: screenshots of the window top, middle and end, the language and
      dimension drop-downs, the reindex warning, the folder picker; in Arabic on display 244
    src/Valafind.App/Views/SettingsWindow.axaml and SettingsWindow.axaml.cs; src/Valafind.App/ViewModels/SettingsViewModel.cs
    src/Valafind.App/Localization/Strings.resx, Settings*
```
