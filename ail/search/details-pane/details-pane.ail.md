# The details pane

Beside the list, the chosen result in full: where it is, what it is, how close it matched, why, and the start of the
file itself, with buttons to open it, show it in Files or copy its path.

```ail
node DetailsPane : view   level: experience
  why   Before opening a file the user wants to be sure it is the right one, without leaving the launcher.   <- SearchFiles
  what
    Scenario: a document in the pane
      Given "scan_0042.txt", 266 bytes, in "~/Documents/Bolig", changed today at 20:30, is the chosen result, with a match such as 0.77
      Then the pane shows its icon, "scan_0042.txt" in bold, "~/Documents/Bolig", and "TXT · 266 B · Modified Today 20:30"
      And "Match" with a bar filled as far as its match, beside the match with two decimals (for 0.77, 77 % and "0.77";
        the match itself is the model's, so it differs a little from run to run)
      And "Why it matched" over up to four lines of its excerpt, the explaining words highlighted
      And "Preview" over the start of the file's text, as it is in the file, on a grey panel that fills the rest of the height
      And at the bottom the buttons "Open", "Show in Files" and "Copy Path"
    Scenario: code in the pane
      Given "sort.py" is the chosen result
      Then its preview is in a fixed-width font, indented as in the file
    Scenario: a picture in the pane
      Given a picture is the chosen result
      Then the pane shows the picture as large as fits, in place of "Why it matched" and "Preview"
    Scenario: the pane follows the choice
      When the user chooses another result
      Then the pane shows that file, its preview scrolled to the top
    Scenario: no pane without results
      When the hint or "No matching files" is shown
      Then there is no pane, and the list fills the width
  how
    decide the preview is the start of the file's text: at most 80 lines or 4,000 characters, read within 5 s
      because "unknown, confirm with the owner" (PR #22 gives the limits, not the reason)
    decide code and data files are previewed in a fixed-width font
      because "unknown, confirm with the owner"
    decide audio, video and archives have no preview
      because "unknown, confirm with the owner" (PR #22: "no preview, same as before")
    decide a long name wraps after "_", "-" and "." over two lines at most
      because a long name then wraps at natural points instead of mid-word (ResultItemViewModel, PR #4)
    decide "Why it matched" is cut at four lines
      because the preview below must stay visible (PR #22)
  look
    a white rounded card (radius 12), 440 wide, 18 by 16 inside, to the right of the list, 12 apart
    at the top: a 48 by 48 rounded square (radius 12) with the kind's glyph or the picture, and beside it the name in bold 15 (two lines at most), the folder in grey 12.5 (shortened in the middle when long, the full folder as a tip), and the kind, size and date in grey 12.5
    under it a row: "Match" in grey, a bar filling the width (6 tall, accent blue on light grey), and the score in bold 13
    "Why it matched" and "Preview" as small bold headings; the excerpt in 13, line height 19, highlights light blue and semibold
    the preview in 12.5 on a light grey panel with rounded corners 8, 12 by 10 inside, scrolling when longer
    the buttons in one row at the bottom, 8 apart: "Open" dark blue with white bold text, the other two light grey pills
  copy
    pane.meta          "{EXTENSION} · {size} · Modified {date}"
    pane.match         "Match"
    pane.why           "Why it matched"
    pane.preview       "Preview"
    pane.open          "Open"
    pane.reveal        "Show in Files"
    pane.copy          "Copy Path"
  binds
    props   selectedResult: ResultItem or nothing, previewText: text or nothing, previewImage: a picture or nothing, isPreviewMonospace: boolean, isPreviewVisible: boolean
    events  "Open" opens the file and hides the launcher; "Show in Files" shows it in the file manager and hides the launcher; "Copy Path" copies its full path and keeps the launcher open
  uses      ResultFormat, ActOnResult
  sources
    PR #3 (the details pane on Windows); PR #4 (the details pane on Linux); PR #22 (previews and thumbnails)
    driven on Linux (Xvfb, openbox) 2026-11-25, state .ail/search: the pane for scan_0042.txt, budget-notes.md, sort.py
      (fixed-width preview) and farm.txt (a preview that scrolls); a picture was not driven here
    src/Valafind.App/Views/Linux/LinuxLauncherView.axaml, the details pane; src/Valafind.App/Services/FilePreviewService.cs
```
