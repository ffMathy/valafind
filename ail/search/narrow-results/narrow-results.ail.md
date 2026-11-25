# Narrowing the results

The kind chips under the list keep only one kind of file, and the "Any time" menu only files changed recently. Both
apply to the question on screen at once.

```ail
node NarrowResults : flow   level: experience
  why   A question in words can match a mail, a slide and a note alike; often the user knows the kind of file, or that
        it is recent, and says so with one click.   <- SearchFiles
  what
    Scenario: a kind chip keeps only files of that kind
      Given a note "pears.txt" and a script "pears.py", both about pears
      When the user searches for "pears" and clicks "Code"
      Then "Code" is chosen, and "pears.py" is listed but "pears.txt" is not
      When the user clicks "All"
      Then both are listed again
    Scenario: a kind with no files shows No matching files
      Given no spreadsheet is indexed
      When the user searches for anything and clicks "Spreadsheets"
      Then the list shows "No matching files", and the heading row reads "0 results · … ms"
    Scenario: the time menu keeps only recent files
      Given a note "old-notes.txt" last changed on 3 March 2020 and a note "new-notes.txt" changed today, both about plums
      When the user searches for "plums" and picks "Past week" from the "Any time" menu
      Then the menu's button reads "Past week", and "new-notes.txt" is listed but "old-notes.txt" is not
      When the user picks "Any time"
      Then "old-notes.txt" is listed again, dated "3 Mar 2020"
    Scenario: the filters are kept until the user changes them
      Given the user chose "Code" and closed the launcher
      When the user opens it with Ctrl+Alt+Space and searches again
      Then "Code" is still chosen and only code is listed
  how
    decide the chips are All, Documents (documents, PDFs, text and notebooks), PDFs, Spreadsheets, Slides, Mail, Code (code and notebooks) and Images
      because "unknown, confirm with the owner: audio and video have no chip"
    decide one chip at a time
      because "unknown, confirm with the owner"
    decide the time menu offers Any time, Today (the last 24 hours), Past week (7 days), Past month (31 days) and Past year (366 days), counted back from the moment of the search
      because "unknown, confirm with the owner"
    decide a change of chip or time searches again at once, without waiting for typing to pause
      because "unknown, confirm with the owner"
    decide the filters are kept when the launcher closes, but not when Valafind restarts
      because "unknown, confirm with the owner: keep this?"
  look
    the chips and the time button sit in one row under the list; the menu opens upwards from its button, right-aligned, listing the five times, the current one shaded
  copy
    chip.all            "All"
    chip.documents      "Documents"
    chip.pdfs           "PDFs"
    chip.spreadsheets   "Spreadsheets"
    chip.slides         "Slides"
    chip.mail           "Mail"
    chip.code           "Code"
    chip.images         "Images"
    time.any            "Any time"
    time.today          "Today"
    time.week           "Past week"
    time.month          "Past month"
    time.year           "Past year"
  uses      LauncherState
  sources
    PR #1 (the filters); PR #12 (counts per kind)
    driven on Linux (Xvfb, openbox) 2026-11-25, state .ail/search: Spreadsheets with no spreadsheets ("No matching
      files", "0 results · 1 ms"), the time menu opened, Past week leaving out a file dated 3 Mar 2020, the filters still
      set after closing and opening the launcher
    src/Valafind.App/ViewModels/FilterChipViewModel.cs; src/Valafind.App/ViewModels/LauncherViewModel.cs, the filter
      and time range commands
```
