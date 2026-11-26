# Opening a result

What the user does with the file once it is found: open it, see it in the file manager, or copy where it is.

```ail
node ActOnResult : flow   level: experience
  why   Finding the file is only half the job; the user wants it open, or wants to know where it is.   <- SearchFiles
  what
    Scenario: Enter opens the chosen file and closes the launcher
      Given the user searched and the second result is chosen
      When the user presses Enter
      Then the second result's file is opened with the system's default app, and the launcher closes
    Scenario: Ctrl+Enter shows the file in the file manager
      Given a result is chosen
      When the user presses Ctrl+Enter
      Then the file manager is asked to show the file; with no file manager answering, the file's folder is opened
      And the launcher closes
    Scenario: the buttons of the details pane
      Given a result is chosen
      When the user clicks "Open"
      Then the file is opened and the launcher closes
      When the user searches again and clicks "Show in Files"
      Then the file's folder is opened and the launcher closes
    Scenario: Copy Path copies the full path and the launcher stays open
      Given a result is chosen
      When the user clicks "Copy Path"
      Then the clipboard holds the file's full path, and the launcher stays open
    Scenario: Ctrl+C copies the chosen file's path unless text is selected in the search box
      Given the question "deposit" is typed and a result is chosen
      When the user presses Ctrl+C
      Then the clipboard holds the chosen file's full path
      When the user selects the question with Ctrl+A and presses Ctrl+C
      Then the clipboard holds "deposit"
    Scenario: a click chooses a result, a double click opens it
      Given three results
      When the user clicks the third
      Then it is chosen and the details pane shows it
      When the user double-clicks it
      Then it is opened and the launcher closes
    Scenario: Enter with no results does nothing
      Given "No matching files" is shown
      When the user presses Enter
      Then nothing is opened and the launcher stays open
  how
    decide Open and Show in Files close the launcher; Copy Path does not
      because "unknown, confirm with the owner"
    decide the keys act on the chosen result, or on the first one when none is chosen
      because "unknown, confirm with the owner"
    decide on Linux, Show in Files asks the desktop's file manager over D-Bus to show the file, and opens its folder when no file manager answers within 5 s
      because the desktop may have no file manager that answers (ShellIntegration falls back "if no FileManager1 service
        answered")
    decide on Linux a file is opened with xdg-open, then gio, then kde-open
      because "unknown, confirm with the owner"
    decide the reveal button is "Show in Files" on Linux, "Show in Finder" on a Mac and "Show in folder" on Windows
      because "unknown, confirm with the owner"
  uses      LauncherState
  sources
    README.md, "Keyboard"; PR #3 and PR #4 (the buttons of the details pane)
    driven on Linux (Xvfb, openbox) 2026-11-25, state .ail/search, with scripts/ail/bin standing in for xdg-open: Enter
      recorded the file, Ctrl+Enter and Show in Files recorded its folder (no file manager on D-Bus), Copy Path and
      Ctrl+C put the path on the clipboard
    src/Valafind.App/Views/LauncherWindow.axaml.cs, the key handler; src/Valafind.App/Platform/ShellIntegration.cs;
      src/Valafind.App/Services/AppHost.cs, LauncherActions
```
