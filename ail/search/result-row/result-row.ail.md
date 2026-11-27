# One result in the list

A row of the list: what kind of file it is, its name, where it is and when it changed, the passage that matched, and
how close the match is.

```ail
node ResultRow : view   level: experience
  why   The user must tell at a glance which result is the file they meant, from what it says, not only from its name.   <- SearchFiles
  what
    Scenario: a document's row
      Given a text file "scan_0042.txt" in the folder "Documents/Bolig" of the home folder, saved today at 20:30
      When it is a result, with a match such as 0.77
      Then its row shows a document icon, "scan_0042.txt" in bold, "~/Documents/Bolig · Today 20:30" under it, and the start of its excerpt under that, cut with "…" at the row's end
      And at the right its match with two decimals in bold, above a bar filled as far as the match (for 0.77, "0.77" and
        77 %; the match itself is the model's, so it differs a little from run to run)
    Scenario: the dates
      Given files changed today at 09:12, yesterday, and on 3 March 2020
      Then their rows read "Today 09:12", "Yesterday" and "3 Mar 2020"
    Scenario: the chosen row
      Then the chosen row has a light blue background, and the details pane beside the list shows that file
    Scenario: a picture's row
      Given a picture among the results
      Then its row shows a small cropped copy of the picture in place of the icon, and no excerpt
  how
    decide the excerpt is one line in the row; the details pane shows more of it
      because "unknown, confirm with the owner"
    decide the folder starts with "~" for the home folder on Linux and on a Mac, and is written in full on Windows
      because "unknown, confirm with the owner"
    decide pictures, audio and video have no excerpt
      because they match by what they show or say, or by name, and have no passage to show (ResultItemViewModel)
    decide the score has two decimals, and its bar is the score itself, from 0 to 1
      because "unknown, confirm with the owner"
  look
    a row is 16 from the card's sides and 11 from its top and bottom, with a hairline under it
    three columns: a 38 by 38 rounded square (radius 9) with the file kind's glyph in the kind's colour on its pale tint, or the picture itself; the text, filling the width; and the score, 96 wide, right-aligned
    the text column: the name in bold 14, the folder and date in grey 12, the excerpt in 12.5, each one line, cut with "…"
    the score in bold 13 over a bar 84 wide and 6 tall, rounded, the filled part in the accent blue on a light grey track
    words of the excerpt that explain the match have a light blue background and are semibold
    the chosen row's background is the accent blue at about a sixth of its strength; a row under the pointer is slightly darker
    the kinds' colours: PDF red, documents and mail blue, text grey, code and notebooks purple, pictures violet, spreadsheets green, slides orange, audio and video pink, archives ochre
  copy
    caption           "{folder} · {date}"
    date.today        "Today {HH:mm}"
    date.yesterday    "Yesterday"
    date.other        "{d MMM yyyy}"
  binds
    props   fileName: text, caption: text, snippet: text, highlights: list of TextSpan, scoreText: text, scoreFraction: number, kind: FileKind, thumbnail: a picture or nothing, isSelected: boolean
    events  a click on the row chooses it; a double click opens it
  uses      ResultFormat
  children  ResultFormat
  sources
    PR #1; PR #22 (thumbnails in result rows)
    driven on Linux (Xvfb, openbox) 2026-11-25, state .ail/search: rows for scan_0042.txt, budget-notes.md (3 Mar 2020),
      sort.py (Yesterday), farm.txt; a picture's row was not driven here
    src/Valafind.App/Views/Linux/LinuxLauncherView.axaml, the results list; src/Valafind.App/ViewModels/ResultItemViewModel.cs
```
