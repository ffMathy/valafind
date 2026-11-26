# The launcher's keys

The whole search works from the keyboard: the arrows choose a result, Tab hides or shows the details pane, and the
footer says which keys do what. Enter and Ctrl+Enter are in ActOnResult, Esc in OpenLauncher.

```ail
node LauncherKeys : flow   level: experience
  why   A launcher is for the keyboard: the hands stay where they typed the question.   <- SearchFiles
  what
    Scenario: the first result is chosen as the results come
      When the user searches
      Then the first result is chosen and the details pane shows it
    Scenario: Down and Up move through the results, stopping at both ends
      Given three results
      When the user presses Down three times
      Then the third result is chosen, and the details pane shows it
      When the user presses Up three times
      Then the first result is chosen again
    Scenario: Tab hides the details pane and shows it again
      Given results are shown with the details pane
      When the user presses Tab
      Then the pane is gone and the rows fill the width, their excerpts longer
      When the user presses Tab again
      Then the pane is back, showing the chosen result
    Scenario: the footer shows the keys
      When the launcher is open
      Then the footer reads "↑↓ navigate", "↵ open", "Ctrl+↵ show in Files" and "Tab preview"
    Scenario: the arrows and Tab never reach the search box
      Given the question "travel budget" with the caret at its end
      When the user presses Up, Down and Tab
      Then the question is still "travel budget" and the search box keeps the keyboard
  how
    decide the arrows stop at the first and the last result instead of wrapping
      because "unknown, confirm with the owner"
    decide the keys work while the search box has the keyboard, so the user never has to leave it
      because "the keyboard drives the result list" (LauncherWindow), with the focus left in the search box
    decide Tab toggles the preview, not the focus
      because the pane is the preview; it was the Mac design's key first, and every design took it over (PR #3)
    decide the choice is kept when the pane is hidden and shown again, and when the launcher closes and opens
      because "unknown, confirm with the owner"
    decide a newly chosen result's preview starts at its top
      because "unknown, confirm with the owner" (LauncherViewBase only says that it does)
  uses      LauncherState
  sources
    README.md, "Keyboard"; PR #3 ("Tab hides the pane, as on macOS"); PR #4
    driven on Linux (Xvfb, openbox) 2026-11-25, state .ail/search: Down, Up, Tab twice, the footer read with OCR
    src/Valafind.App/Views/LauncherWindow.axaml.cs, the key handler; src/Valafind.App/ViewModels/LauncherViewModel.cs, the
      move and toggle commands; src/Valafind.App/Views/LauncherViewBase.cs
```
