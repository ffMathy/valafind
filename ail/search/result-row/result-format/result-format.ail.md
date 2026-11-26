# Results as text

How a result's date, size, score, folder and kind become the text in its row and in the details pane.

```ail
node ResultFormat : fn   level: system
  why   The same file must read the same way in the row and in the pane, on every platform.   <- ResultRow
  what
    DisplayFormat.relativeDate(value: time, now: time, zone: text or nothing) -> text — in the given zone (the local one by default): "Today HH:mm" on the day of now, "Yesterday" the day before, else "d MMM yyyy" in the user's culture
    DisplayFormat.fileSize(bytes: integer) -> text — under 1024: "N B"; else divided by 1024 until under 1024 (KB, MB, GB, TB): whole when 10 or more, else one decimal at most
    DisplayFormat.score(score: number) -> text — two decimals, invariant culture
    DisplayFormat.shortenHome(directory: text, home: text or nothing, comparison: "ordinal" | "ignore case") -> text — the home folder at the start becomes "~", only at a folder boundary
    DisplayFormat.breadcrumb(directory: text, home: text or nothing, comparison: "ordinal" | "ignore case") -> text — the last two folders below home, joined by " › "
    DisplayFormat.extensionLabel(path: text) -> text — the extension in upper case without its dot; "FILE" when there is none or it is longer than five letters
    DisplayFormat.withBreakOpportunities(name: text) -> text — a zero-width space after each "_", "-" and "." except the last character, so long names wrap there
    DisplayFormat.collapseWhitespace(text: text, highlights: list of TextSpan) -> { text: text, highlights: list of TextSpan } — every run of white space becomes one space, trimmed; the highlights move with the text and never start or end on a space
    type ResultItem = { path: text, fileName: text, displayDirectory: text, fullDirectory: text, dateText: text, scoreText: text, scoreFraction: number, extensionLabel: text, sizeText: text, snippet: text, highlights: list of TextSpan }
    ResultItem.caption = displayDirectory + " · " + dateText
    ResultItem.metaText = extensionLabel + " · " + sizeText + " · Modified " + dateText
    examples
      DisplayFormat.relativeDate(2026-11-16 09:12:00, 2026-11-16 10:00:00, "UTC") => "Today 09:12"
      DisplayFormat.relativeDate(2026-11-15 23:59:00, 2026-11-16 10:00:00, "UTC") => "Yesterday"
      DisplayFormat.relativeDate(2020-03-03 10:00:00, 2026-11-16 10:00:00, "UTC") => "3 Mar 2020"
      DisplayFormat.fileSize(0) => "0 B";  DisplayFormat.fileSize(266) => "266 B";  DisplayFormat.fileSize(1023) => "1023 B"
      DisplayFormat.fileSize(1024) => "1 KB";  DisplayFormat.fileSize(1536) => "1.5 KB";  DisplayFormat.fileSize(421888) => "412 KB"
      DisplayFormat.fileSize(2811004) => "2.7 MB";  DisplayFormat.fileSize(10485761) => "10 MB"
      DisplayFormat.score(0.8749) => "0.87";  DisplayFormat.score(0.875) => "0.88"
      DisplayFormat.shortenHome("/home/ana/Documents/Bolig", "/home/ana", "ordinal") => "~/Documents/Bolig"
      DisplayFormat.shortenHome("/home/anax/Documents", "/home/ana", "ordinal") => "/home/anax/Documents"
      DisplayFormat.shortenHome("/home/ana", "/home/ana/", "ordinal") => "~"
      DisplayFormat.breadcrumb("/home/ana/Pictures/2024/03", "/home/ana", "ordinal") => "2024 › 03"
      DisplayFormat.breadcrumb("/home/ana/Downloads", "/home/ana", "ordinal") => "Downloads"
      DisplayFormat.extensionLabel("/a/b/report.PDF") => "PDF";  DisplayFormat.extensionLabel("/a/b/Makefile") => "FILE"
      DisplayFormat.extensionLabel("/a/b/x.verylong") => "FILE"
      DisplayFormat.collapseWhitespace("a  \n b", []).text => "a b"
  how
    decide the folder is shortened with "~" on Linux and macOS, compared exactly on Linux and ignoring case elsewhere
      because Linux file names are case-sensitive; on Windows the full folder is shown (ResultItemViewModel)
    decide the score's bar is the score clamped to 0 … 1
      because a cosine can in principle be negative
    decide the time of today is always 24-hour "HH:mm"; other dates follow the user's culture
      because "unknown, confirm with the owner"
  sources
    src/Valafind.App/ViewModels/DisplayFormat.cs; src/Valafind.App/ViewModels/ResultItemViewModel.cs
    tests/Valafind.App.Tests/ViewModels/DisplayFormatTests.cs
    examples run against the built app on 2026-11-25 (a small program calling DisplayFormat by reflection, culture en-GB)
```
