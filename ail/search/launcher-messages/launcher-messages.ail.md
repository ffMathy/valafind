# The launcher's messages

What the list shows when there are no results to show: the hint before anything is asked, "Searching…" on the way,
and "No matching files".

```ail
node LauncherMessages : view   level: experience
  why   An empty list says nothing; the user needs to know what to type, and what to try when nothing matched.   <- SearchFiles
  what
    Scenario: the hint
      Given the model is ready
      When the question is blank
      Then the list shows "Search your files by meaning", "Describe what you are looking for in your own words — “the lease where we negotiated the deposit”. Everything runs on this computer." and "Summon Valafind from anywhere with Ctrl+Alt+Space."
    Scenario: nothing matched
      Given no indexed file passes the chosen filters
      When the user searches
      Then the list shows "No matching files" and "Try describing it differently, pick another filter, or wait for indexing to cover more of your files."
      And the heading row still reads "0 results · 1 ms" or so
    Scenario: a search on the way from the hint
      Given the hint is shown
      When the user types and the first search has not answered yet
      Then the hint's title reads "Searching…" until the results come
  how
    decide the summon line shows the shortcut the user chose, in bold, and is left out when the shortcut could not be registered
      because "unknown, confirm with the owner"
    decide "0 results · … ms" stays in the heading row above "No matching files"
      because "unknown, confirm with the owner: keep this?"
    decide the summon line is split around the shortcut in each language
      because word order differs between languages (PR #10)
  look
    inside the list's white card, 20 from its left and 18 from its top, one column, 6 apart
    the title in semibold 15, in the text colour; the lines under it in 13 with a line height of 19, in grey, wrapping
    in the summon line the shortcut is semibold
  copy
    hint.title          "Search your files by meaning"
    hint.searching      "Searching…"
    hint.body           "Describe what you are looking for in your own words — “the lease where we negotiated the deposit”. Everything runs on this computer."
    hint.summon         "Summon Valafind from anywhere with {shortcut}."
    noresults.title     "No matching files"
    noresults.body      "Try describing it differently, pick another filter, or wait for indexing to cover more of your files."
    error.title         "Search failed"
  binds
    props   isHintVisible: boolean, isNoResultsVisible: boolean, isErrorVisible: boolean, isSearching: boolean, hotkeyLabel: text, hasHotkeyError: boolean, errorText: text
  sources
    PR #1 (the launcher's states); PR #10 (translations)
    driven on Linux (Xvfb, openbox) 2026-11-25, state .ail/search: the hint, "No matching files" with the Spreadsheets
      chip and "0 results · 1 ms"; "Searching…" and "Search failed" were not seen: they are read from the code
    src/Valafind.App/Views/Common/LauncherMessageView.axaml; src/Valafind.App/Localization/Strings.resx
```
