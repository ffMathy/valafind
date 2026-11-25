# Which shortcuts work

The global shortcut is written as text such as "Ctrl+Alt+Space". These rules say which texts are shortcuts, what the
settings box records, and what Valafind says when a desktop does not let it register one (Wayland).

```ail
node ShortcutRules : fn   level: system
  why   A shortcut that silently does nothing makes Valafind look broken; a bad one must be refused with a reason,
        and where the desktop will not allow one, the user must be told the exact command to bind.   <- SettingsWindow
  what
    type Chord = { ctrl: boolean, alt: boolean, shift: boolean, super: boolean, key: text }
    parseChord(text: text) -> { chord: Chord } | { error: text }
      parts split on "+", trimmed; every part but the last is a modifier, the last is the key
      modifiers, any case: Ctrl, Control, Ctl, ⌃ / Alt, Option, Opt, ⌥ / Shift, ⇧ / Super, Win, Windows, Cmd, Command, Meta, Logo, ⌘
      keys: A–Z, 0–9, F1–F24, Space, Enter, Tab, Backquote, Comma, Period, Slash, Semicolon (and their usual spellings)
      a chord needs Ctrl, Alt or Super, except a function key, which may stand alone or with Shift only
    checkChord(text: text, mac: boolean) -> text or nothing — the message to show, or nothing when it may be saved;
      blank is fine (the default); on a Mac a chord of only Option, or Option and Shift, is refused
    recordedChord(keys pressed in the shortcut box) -> text or nothing — the modifiers held, in the order Ctrl, Alt,
      Shift, Super (Cmd on a Mac), then the key's name; nothing (the box keeps its text) without a modifier or for a
      key it has no name for; plain Tab and Shift+Tab move the focus instead
    toggleCommand(executable: text, entryFile: text or nothing) -> text — the command a desktop shortcut should run
    examples
      parseChord("ctrl+alt+space") => { chord: { ctrl: true, alt: true, shift: false, super: false, key: "Space" } }
      parseChord("Ctrl+Escape") => { error: "Escape cannot be used as a global shortcut key." }
      parseChord("Shift+A") => { error: "A global shortcut needs Ctrl, Alt or Super (only function keys may be used alone or with just Shift)." }
      parseChord("F9") => { chord: { ctrl: false, alt: false, shift: false, super: false, key: "F9" } }
      parseChord("Ctrl+Ctrl+K") => { error: "The modifier 'Ctrl' appears more than once in 'Ctrl+Ctrl+K'." }
      parseChord("Ctrl+Alt") => { error: "'Ctrl+Alt' has no key; add a key after the modifiers (e.g. 'Ctrl+Alt+Space')." }
      parseChord("Ctrl+Home") => { error: "'Home' is not a supported key. Supported: Space, A-Z, 0-9, F1-F24, Enter, Tab, Backquote, Comma, Period, Slash, Semicolon." }
      checkChord("Alt+Space", true) => "macOS does not allow shortcuts that use only Option (⌥Space). Add Control or Command, for example ⌃⌥Space."
      checkChord("Alt+Space", false) => nothing
      recordedChord(Ctrl and Alt held, K pressed) => "Ctrl+Alt+K";  recordedChord(K alone) => nothing
      toggleCommand("/opt/Valafind/Valafind", nothing) => "/opt/Valafind/Valafind --toggle"
      toggleCommand("/home/u/My Apps/valafind", nothing) => "\"/home/u/My Apps/valafind\" --toggle"
      toggleCommand("/usr/bin/dotnet", "/src/Valafind.dll") => "/usr/bin/dotnet /src/Valafind.dll --toggle"
    Registering, per platform: Windows RegisterHotKey; macOS a Carbon hot key; Linux on X11 a key grab; on Wayland the
      desktop's GlobalShortcuts portal (KDE Plasma, GNOME 48 and later), described as "Show or hide Valafind"
    a Linux session is X11 when XDG_SESSION_TYPE is x11 and DISPLAY is set, else Wayland when WAYLAND_DISPLAY is set or
      XDG_SESSION_TYPE is wayland, else X11 when DISPLAY is set, else none
    when it cannot be registered, the launcher shows the reason in a banner, and after Save so does the settings window:
      another app holds it => "Ctrl+Alt+Space is already in use by another app. Choose a different shortcut."
      Wayland without the portal => "Global hotkeys are not available on Wayland. Bind a shortcut in your desktop settings to run: {toggleCommand}"
      no session => "Global hotkeys need a graphical session (neither DISPLAY nor WAYLAND_DISPLAY is set)."
      any other failure => "Could not register {chord} — bind a system shortcut to: {toggleCommand}"
    a chord that fails keeps the previous one registered
  how
    decide Ctrl+Alt+Space on every platform
      because Alt+Space, Super+Space, Cmd+Space and Option+Space belong to the system or are refused (ValafindSettings)
    decide on Wayland ignore XWayland's DISPLAY and use the portal
      because X key grabs there fire only while an X11 window has focus, which would mislead (GlobalHotkeyFactory)
    decide the Wayland help names the real executable with its full path
      because nothing installs a "valafind" command on PATH, so a bare name would fail silently
  sources
    PR #1 (hotkeys for Win32, Carbon, X11 and the Wayland portal); README.md, "Keyboard", the Wayland paragraph
    driven on Linux 2026-11-25: Ctrl+Escape refused; Ctrl+Alt+K saved and working; a second copy on the same display
      ("already in use"); WAYLAND_DISPLAY=wayland-0 on display 246 (the banner, and the settings after Save)
    src/Valafind.App/Platform/HotkeyChord.cs, GlobalHotkeyFactory.cs; src/Valafind.App/Controls/HotkeyBox.cs
```
