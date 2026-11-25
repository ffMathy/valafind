# Opening and closing the launcher

Ctrl+Alt+Space brings the launcher up from anywhere, ready to type into; the same keys or Esc put it away. It keeps
what was asked, so the user can come back to the results.

```ail
node OpenLauncher : flow   level: experience
  why   The search must be one key press away from whatever the user is doing, and gone again just as fast.   <- SearchFiles
  what
    Scenario: Ctrl+Alt+Space opens the launcher, ready to type into
      Given the launcher is closed
      When the user presses Ctrl+Alt+Space
      Then a window titled "Valafind" appears, and what the user types next goes into its search box
    Scenario: Ctrl+Alt+Space closes it again
      Given the launcher was opened with Ctrl+Alt+Space
      When the user presses Ctrl+Alt+Space
      Then the launcher closes
    Scenario: Esc closes it
      Given the launcher is open
      When the user presses Esc
      Then the launcher closes
    Scenario: the question and its results are kept for the next time
      Given the user searched for something and closed the launcher with Esc
      When the user presses Ctrl+Alt+Space
      Then the launcher shows the same question, selected, and the same results with the same one chosen
      And typing replaces the question
    Scenario: opened with the shortcut, it closes when the user clicks another window
      Given the launcher was opened with Ctrl+Alt+Space and another window is on screen
      When the user clicks the other window
      Then the launcher closes
    Scenario: it appears centred across the screen, near the top
      When the user presses Ctrl+Alt+Space
      Then the launcher is centred from left to right on the screen with the pointer
      And its top is at about a fifth of the screen's height, or higher when it would not fit
  how
    decide the shortcut toggles the launcher
      because "unknown, confirm with the owner"
    decide opened by the shortcut, it stays above other windows and closes when another window is clicked; opened in any other way (at the first start, from the tray, by starting Valafind again) it is an ordinary window that stays open
      because a shortcut summons a quick popup, while a launcher opened at start or from the tray is an ordinary window
        that stays open until dismissed (LauncherWindow, ShowLauncher; PR #29, PR #32 say what, not why)
    decide a click elsewhere in the first 300 ms after opening does not close it
      because "unknown, confirm with the owner"
    decide closing only hides the launcher: the question, the results, the chosen result and the filters stay
      because the window stays alive so it shows again at once (LauncherWindow, HideLauncher)
    decide on opening, the question is selected, so typing starts a new one
      because "unknown, confirm with the owner"
    decide Alt+F4 hides the launcher instead of quitting
      because "unknown, confirm with the owner: the code says only that it hides the window instead of destroying it"
  uses      LauncherPlacement
  children  LauncherPlacement
  sources
    README.md, "Keyboard"; PR #1 (global hotkeys, Ctrl+Alt+Space everywhere); PR #29 "Keep the launcher open unless it
      was summoned by the hotkey"; PR #32 "Only keep the launcher on top when summoned by the hotkey"
    driven on Linux (Xvfb, openbox) 2026-11-25, state .ail/search: Ctrl+Alt+Space opening and closing it, Esc, the
      question kept and selected on reopening, the second result still chosen; window at 30,0, 1220 by 786, on a
      1280 by 800 screen with a 33 pixel panel at the bottom
    src/Valafind.App/Views/LauncherWindow.axaml.cs; src/Valafind.App/Services/AppHost.cs, the toggle and show methods
```
