# The Windows and macOS launchers

The same launcher in two other designs, which cannot be driven on this Linux desktop: a Windows command launcher and
a macOS floating bar. They share LauncherState, the messages, the chips, the time menu and the keys; this node holds
what differs. Read from the code and the README's screenshots.

```ail
node PlatformLaunchers : view   level: system
  why   On each system the launcher should look like part of it: Fluent on Windows, a Spotlight-like bar on macOS,
        GNOME on Linux (PR #1: "Each OS uses the design direction approved on the design canvas").   <- SearchFiles
  what
    LauncherWindow.createView(flavor: "windows" | "mac" | "linux") -> a rendered view — the Windows, macOS or Linux launcher
    revealLabel(flavor: "windows" | "mac" | "linux") -> text — the design is chosen from the operating system, and VALAFIND_UI overrides it
    groupForMac(results: list of ResultItem) -> list of ResultItem — the first result under "Top Match", then the rest grouped by kind group in order of first appearance, each group headed by its name on its first row
    kindGroup(kind: FileKind) -> text — documents, PDFs, text and notebooks => "Documents"; mail => "Mail"; spreadsheets => "Spreadsheets"; slides => "Presentations"; code => "Code"; pictures => "Images"; audio and video => "Media"; anything else => "Other"
    the command key: Ctrl on Windows and Linux, Cmd on macOS; Cmd+Enter shows in Finder, Cmd+C copies the path, Cmd+, opens Settings
    examples
      revealLabel("linux") => "Show in Files";  revealLabel("mac") => "Show in Finder";  revealLabel("windows") => "Show in folder"
      the fileName of each of groupForMac(r) => ["a.pdf", "b.eml", "d.eml", "c.txt"]   where r = results for a.pdf, b.eml, c.txt, d.eml, in that order
      the groupHeader of each of groupForMac(r) => ["Top Match", "Mail", nothing, "Documents"]   where r = results for a.pdf, b.eml, c.txt, d.eml, in that order
      DisplayFormat.hotkeyLabel("Ctrl+Alt+Space", "mac") => "⌃⌥Space";  DisplayFormat.hotkeyLabel("Ctrl+Alt+Space", "linux") => "Ctrl+Alt+Space"
      DisplayFormat.hotkeyKeys("Super+Space", "windows") => ["Win", "Space"]
  how
    decide Windows: a card 1120 wide; compact rows whose caption is "{folder} · {date} · “{excerpt}”", the folder in full; a pane with "Semantic match", "WHY IT MATCHED · CLOSEST PASSAGE", the preview, and "Open", "Show in folder", "Copy path"; the footer "↑ ↓ Navigate", "↵ Open", "Ctrl ↵ Show in folder", "Tab Preview", each key in its own key cap
      because PR #3 gave Windows the details pane of the Mac design
    decide Windows: Mica first (the system's own, then Avalonia's), then acrylic, blur, transparency, then an opaque card; dark mode read from the registry
      because Mica is "the backdrop of Settings and other native windows" (LauncherWindow; PR #4, PR #5)
    decide macOS: a bar 800 wide; rows grouped under "Top Match" and kind headings, each captioned "{last two folders} · {date}"; a pane with "Semantic match", "Why it matched", the preview, and "Open", "Show in Finder", "Quick Look"; the footer "↑↓ Navigate", "↵ Open", "⌘↵ Reveal" and the shortcut
      because "unknown, confirm with the owner"
    decide macOS: Quick Look shows the chosen file with the system's previewer (qlmanage -p)
      because "unknown, confirm with the owner"
    decide the time menu opens downwards from the top on Windows and macOS, upwards from the bottom on Linux
      because "unknown, confirm with the owner"
  sources
    README.md, the table of screenshots; docs/screenshots/windows-launcher.png, macos-launcher.png
    PR #1, PR #3, PR #4, PR #5, PR #22
    src/Valafind.App/Views/Windows/WindowsLauncherView.axaml; src/Valafind.App/Views/Mac/MacLauncherView.axaml
    src/Valafind.App/ViewModels/LauncherViewModel.cs, RevealLabel and GroupForMac; FilterChipViewModel.cs, FileKindGroups
    src/Valafind.App/ViewModels/DisplayFormat.cs, HotkeyLabel and HotkeyKeys (tests/Valafind.App.Tests/ViewModels/DisplayFormatTests.cs)
    not driven: Windows and macOS are not available here
```
