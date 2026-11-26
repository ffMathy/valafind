# The launcher on Linux

The GNOME-style launcher as a whole: the header with the search box, the list of results and the details pane, the
kind chips, and the footer with the index status and the keys. Its parts have nodes of their own.

```ail
node LauncherLayout : view   level: experience
  why   One window holds the whole search, laid out like a GNOME dialog so it looks at home on Linux.   <- SearchFiles
  what
    Scenario: the launcher at rest
      Given the index is up to date
      When the launcher opens with nothing typed
      Then the search box shows "Describe what you are looking for" and "Ctrl+Alt+Space" at its right end
      And under the header, "Best matches by meaning" heads the hint
      And the kind chips "All", "Documents", "PDFs", "Spreadsheets", "Slides", "Mail", "Code", "Images" sit at the bottom left, "All" chosen, and "Any time" at the bottom right
      And the footer reads "✓ Up to date" on the left and "↑↓ navigate  ↵ open  Ctrl+↵ show in Files  Tab preview" on the right
    Scenario: while indexing, the index panel takes the place of "Up to date"
      Given files are being indexed
      Then the index panel shows between the list and the chips, and the footer has no "Up to date"
    Scenario: while indexing, a yellow notice under the search box says the results may be incomplete
      Given indexing is running and 41% of the files are searchable
      When the user searches for anything
      Then right under the header a yellow notice reads "Indexing isn’t finished yet, so results may be incomplete · 41% of your files are searchable"
      And it shows above "No matching files" too, but not with the hint before anything is asked
    Scenario: the notice goes once every file is searchable
      Given the notice is shown
      When indexing finishes
      Then the notice is gone, and the footer reads "✓ Up to date"
    Scenario: while paused, the paused banner takes the notice's place
      Given the user paused indexing before every file was searchable
      When the user searches for anything
      Then the paused banner shows, with how much is searchable, and the yellow notice does not
    Scenario: a tip on "Up to date" says how many files can be found
      When the user rests the pointer on "Up to date"
      Then a tip reads "Index up to date · … files searchable", and under it the files of each kind, such as "Documents 2 · Code 1"
  how
    decide the index status is a quiet "Up to date" in the footer once indexing is done, and the full panel only while indexing, paused or waiting for the model
      because PR #5 ("Index panel only while indexing", "quiet up to date")
    decide a yellow notice under the search box, while results or "No matching files" are shown and indexing is running or paused with files still to do, says the results may be incomplete; not while Valafind is starting or the model is missing
      because the owner asked for it (2026-11-26): a search made before indexing is done can miss files, and should say so; while starting, the index from the last run is usually complete, and without the model nothing is searched
      rejected a line in the index panel because the panel sits at the bottom, below the results, where it is read after them
    decide the launcher is 1220 by 786 with a details pane 440 wide
      because more of the preview fits (PR #22)
    decide the hint for a blank question, "No matching files" and the model's messages take the list's place, without the details pane
      because the pane would be an empty box next to them (PR #5)
  look
    a card 1220 wide and 786 tall, light grey, with rounded corners 14 and a soft shadow on a compositing desktop; square, filling the window, without one
    the header, 50 tall, slightly darker, holds a sliders button at the left, the search box centred (470 wide, 34 tall, rounded 8, a blue focus ring, a magnifier, and the shortcut in a small grey pill at its right end), a menu button and a round close button at the right
    under the header, 18 from the edges: the heading row ("Best matches by meaning" bold 13 on the left, the count and time in grey 12 on the right)
    then the list in a white rounded card (radius 12) filling the width, and the details pane, 440 wide, to its right while results are shown
    then the chips row: pill buttons 30 tall, the chosen one dark blue with white bold text, the others light grey; the time menu as a grey rounded button with a chevron at the far right
    the incomplete notice, under the header and 12 from it, 18 from the sides: a pale yellow strip with a darker yellow hairline and rounded corners 10, a warning triangle in amber, then the text 13 in dark brown, its first part semibold; the same yellow in every design, darker in the dark theme
    the footer, 40 tall, a slightly darker strip with a hairline on top, grey text 12: a green tick and "Up to date" at the left; the key hints at the right, each key in bold followed by its word
    the colours follow the system's light or dark theme
  copy
    search.placeholder     "Describe what you are looking for"
    search.name            "Search files by meaning"
    heading                "Best matches by meaning"
    footer.uptodate        "Up to date"
    footer.uptodate.tip    "Index up to date · {count} files searchable"
    footer.navigate        "navigate"        after "↑↓"
    footer.open            "open"            after "↵"
    footer.reveal          "show in Files"   after "Ctrl+↵"
    footer.preview         "preview"         after "Tab"
    header.settings.tip    "Index status and preferences"
    header.close.name      "Close"
    notice.incomplete      "Indexing isn’t finished yet, so results may be incomplete"
    notice.searchable      "{percent} of your files are searchable"   after " · "
  binds
    props   query: text, hotkeyLabel: text, resultSummary: text, isUpToDate: boolean, upToDateDetail: text, showIncompleteNotice: boolean, searchableDetail: text
    events  the close button hides the launcher; the sliders button opens Settings; the menu button opens "Preferences", "Pause indexing" or "Resume indexing", "Index Now" and "Quit Valafind"
  uses      LauncherMessages, ResultRow, DetailsPane, NarrowResults, LauncherKeys
  sources
    PR #4 (details pane on Linux, index panel on every launcher); PR #5 (quiet "Up to date"); PR #22 (sizes)
    driven on Linux (Xvfb, openbox) 2026-11-25, state .ail/search: the launcher at rest, while indexing, with results;
      the tip on "Up to date" was not driven: it is read from the code (IndexStatusViewModel, Update)
    the incomplete notice: asked for by the owner on 2026-11-26; rendered headless on each design (DocsScreenshotTests,
      RendersIncompleteNoticeScreenshot) and pinned by LauncherViewModelTests (IncompleteNotice…)
    src/Valafind.App/Views/Linux/LinuxLauncherView.axaml; src/Valafind.App/Views/Common/IncompleteNoticeView.axaml;
      src/Valafind.App/Localization/Strings.resx
```
