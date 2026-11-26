# Searching by meaning

The heart of Valafind: the user describes a file in their own words, and the launcher lists the files closest in
meaning, best first, with the passage that explains each match.

```
search-files.ail.md                                   SearchFiles (this file)
open-launcher/open-launcher.ail.md                    OpenLauncher: Ctrl+Alt+Space, Esc, where it appears
open-launcher/launcher-placement/…                    LauncherPlacement: the position, as numbers
launcher-layout/launcher-layout.ail.md                LauncherLayout: header, list, chips, footer, "Up to date"
launcher-messages/launcher-messages.ail.md            LauncherMessages: the hint and "No matching files"
result-row/result-row.ail.md                          ResultRow: one result in the list
result-row/result-format/result-format.ail.md         ResultFormat: dates, sizes, scores and folders as text
details-pane/details-pane.ail.md                      DetailsPane: the chosen file, why it matched, its preview
launcher-keys/launcher-keys.ail.md                    LauncherKeys: the arrows, Tab and the footer's hints
act-on-result/act-on-result.ail.md                    ActOnResult: open, show in Files, copy the path
narrow-results/narrow-results.ail.md                  NarrowResults: the kind chips and the "Any time" menu
launcher-state/launcher-state.ail.md                  LauncherState: when a search runs and what the launcher shows
search-rules/search-rules.ail.md                      SearchRules: ranking, the file-name boost, the explanation
search-rules/term-matching/term-matching.ail.md       TermMatching: words, stems, stopwords
search-rules/snippet-rules/snippet-rules.ail.md       SnippetRules: sentences and the excerpt
platform-launchers/platform-launchers.ail.md          PlatformLaunchers: the Windows and macOS designs
```

```ail
node SearchFiles : flow   level: experience
  why   People remember what a file was about, not its name or where they put it, and often not even the language it
        is written in: the user asks in their own words and gets the file.   <- Valafind
  what
    Scenario: an English question finds a Danish document
      Given a file "scan_0042.txt" in Danish: "Lejer betaler et depositum svarende til tre måneders husleje, i alt 36.000 kr."
      When the user searches for "how much was the deposit for the apartment"
      Then "scan_0042.txt" is the first result, under "Best matches by meaning"
    Scenario: the results come best first, with their count and the time the search took
      Given some indexed files
      When the user searches for anything
      Then the heading row reads "Best matches by meaning" on the left and "… results · … ms" on the right
      And each result shows its match score, and the scores go down the list
    Scenario: every indexed file is listed, however weak the match
      Given a file "sort.py" with a bubble sort, and a lease about a deposit
      When the user searches for "how much was the deposit for the apartment"
      Then "sort.py" is listed too, below the lease, with a lower score
    Scenario: the sentence closest in meaning is highlighted
      Given a file "budget.md" with "# Quarterly budget meeting", "We agreed to cut the travel budget by twenty percent next quarter." and "Marketing keeps its spend, but the conference booth is cancelled."
      When the user searches for "the travel budget was cut"
      Then under "Why it matched" the whole sentence "We agreed to cut the travel budget by twenty percent next quarter." is highlighted
      And neither the heading nor the sentence about marketing is
    Scenario: a weak match highlights the words of the question
      Given a long file "farm.txt" about the weather, with one sentence "The pineapple harvest failed in June because of the frost."
      When the user searches for "pineapple"
      Then the word "pineapple" is highlighted, and the words around it are not
    Scenario: the excerpt is cut around the sentence that matches
      Given the same "farm.txt": eight sentences "Sentence number 1 … 8 talks about the weather …", then the pineapple sentence, then four "Line 1 … 4 lists tractor parts for the workshop."
      When the user searches for "pineapple"
      Then its excerpt starts with "…Sentence number 8" and goes on to the pineapple sentence and "Line 2 lists tractor parts"
    Scenario: a one-letter question still searches
      When the user types "a"
      Then the launcher lists results with their scores, and nothing is highlighted
    Scenario: a blank question shows the hint again
      Given results are shown
      When the user deletes the question, or types only spaces
      Then the results go and "Search your files by meaning" is shown
  how
    decide every indexed file can be a result, up to 30, however weak; there is no lower limit on the score
      because "unknown, confirm with the owner: keep this? An unrelated file is listed with a score of about 0.5"
    decide the search runs as the user types, a moment after the last key, never with a search button
      because "unknown, confirm with the owner" (PR #1 and SearchService say that each key press cancels the search before it)
    decide the excerpt and its highlight come at once from the words of the question; the sentence closest in meaning replaces them a moment later, for the two best results only
      because the model's second pass is slower, and the list must not wait for it (PR #1, "Storage & search")
    decide no sentence is highlighted for a weak match or a sentence that is unrelated
      because an explanation for a weak match is noise; PR #1 fixed "invented why it matched reasons"
  uses      SearchRules, LauncherState
  children  OpenLauncher, LauncherLayout, LauncherMessages, ResultRow, DetailsPane, LauncherKeys, ActOnResult, NarrowResults, LauncherState, SearchRules, PlatformLaunchers
  sources
    README.md, the opening, "Keyboard"; PR #1 "Valafind: local semantic file search"; PR #9 (EmbeddingGemma 2 thresholds)
    driven on Linux (Xvfb, openbox) 2026-11-25, state .ail/search: the Danish lease first at 0.77 for an English
      question, sort.py listed at 0.52; "travel budget" highlighting the sentence at 0.72; "pineapple" at 0.68
      highlighting the one word; "a" listing every file; blank and spaces-only questions showing the hint
    src/Valafind.Core/Search/SearchService.cs; src/Valafind.App/ViewModels/LauncherViewModel.cs
```
