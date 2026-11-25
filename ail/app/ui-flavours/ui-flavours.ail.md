# Three designs

The launcher comes in three designs, one per platform. Each platform gets its own by default; VALAFIND_UI shows any
of them anywhere, which is how the other two are tried out. The launcher's own layout and keys are described by the
search area; this node says how a design is chosen and what sets them apart at a glance.

```ail
node UiFlavours : view   level: experience
  why   Each platform has a launcher designed for it: a Fluent command bar on Windows, a Spotlight-like floating bar
        on a Mac, a GNOME dialog on Linux; and each design must be reviewable on any machine.   <- AppShell
  what
    The design follows the system: Windows on Windows, Mac on a Mac, Linux elsewhere.
    The environment variable VALAFIND_UI picks one instead: "windows" or "win", "mac", "macos" or "osx", "linux" or
      "gnome", in any case and with spaces around it; anything else is ignored.
    The design is chosen once, at start; the log's starting line names it ("UI Linux").
    Only the launcher changes; the settings window, the tray menu and the texts of the messages are the same.
    Scenario: the Windows design
      Given Valafind runs on Linux with VALAFIND_UI set to windows
      When the launcher opens for the first time
      Then a wide bar sits in the upper part of the screen with a blue tile holding the magnifier, "Search files by meaning", and a gear
      And the filters and "Any time" sit under the search line
      And the foot shows keycaps for "Navigate", "Open", "Show in folder" and "Preview", and "Up to date"
    Scenario: the Mac design
      Given Valafind runs on Linux with VALAFIND_UI set to mac
      When the launcher opens for the first time
      Then a floating panel near the top shows a large "Describe what you are looking for" with the filters under it
      And the shortcut is written the Mac way, "⌃⌥Space", in the welcome and in the foot
      And the foot shows "Up to date" on the leading side and "Navigate", "Open" and "Reveal" on the other
    Scenario: the Linux design
      Given Valafind runs on Linux without VALAFIND_UI
      When the launcher opens for the first time
      Then a GNOME-style window fills most of the screen: a header bar with a settings button, the search entry in the
        middle with the shortcut in a badge, a menu button and a round close button
      And "Best matches by meaning" heads the results, the filters sit at the bottom
      And the foot shows "Up to date" and "navigate", "open", "show in Files" and "preview" in lower case
  look
    windows     Fluent: a light acrylic bar about 1120 wide, an accent underline under the search line, outlined chips,
                keycaps in small rounded boxes; Mica behind it on Windows 11
    mac         a rounded floating panel about 800 wide on a light grey, a borderless search line, filled grey chips with
                the chosen one in blue, the foot as quiet text with ⌘ and ⌃⌥ glyphs
    linux       libadwaita-like: a grey header bar, a white rounded card for the results, filled pill chips with the chosen
                one in dark blue, the foot as quiet text
  copy
    windows.placeholder   "Search files by meaning"
    mac.placeholder       "Describe what you are looking for"
    linux.placeholder     "Describe what you are looking for"
    windows.keys          "Navigate", "Open", "Show in folder", "Preview"
    mac.keys              "Navigate", "Open", "Reveal"
    linux.keys            "navigate", "open", "show in Files", "preview"
  how
    decide one design per platform, each approved on a design canvas
      because "unknown, confirm with the owner: PR #1 names the approved designs (Command Launcher, Floating Bar,
        GNOME-style dialog), not why each platform gets its own"
    decide VALAFIND_UI overrides the platform
      because the other designs must be testable on any machine (UiFlavor)
    decide the Windows design shows the PowerShell command for the model even on Linux
      because "unknown, confirm with the owner: keep this? it follows the design, not the system (ModelInstallHelp)"
  sources
    PR #1 (the three designs); PR #3, PR #4, PR #5 (Windows details pane, Mica, Linux details pane)
    driven on Linux 2026-11-25: VALAFIND_UI=windows on display 242, mac on 243, linux on 204 and 241 (screenshots)
    src/Valafind.App/ViewModels/UiFlavor.cs; src/Valafind.App/Views/Windows, Views/Mac, Views/Linux
```
