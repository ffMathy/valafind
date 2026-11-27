# Languages

Every text Valafind shows comes in 21 languages. It follows the system's language unless the user picks another one
in the settings, which applies at the next start.

```ail
node Languages : view   level: experience
  why   Valafind is meant for anyone's files, in any language; its own texts should be in the user's language too,
        and read in the direction that language reads.   <- AppShell
  what
    The languages: English, Chinese (Simplified), Hindi, Spanish, Arabic, French, Bengali, Portuguese, Russian,
      Indonesian, Urdu, German, Japanese, Marathi, Vietnamese, Telugu, Hausa, Turkish, Tamil, Korean and Danish.
    The system's language is used when Valafind has it, or a parent of it (Austrian German gets German, Chinese
      for China gets Simplified Chinese); otherwise English.
    A language picked under Settings, General, Language applies at the next start, to every window; numbers and
      dates then follow it too. "System default" goes back to the system's language.
    Arabic and Urdu lay out the launcher and the settings window from right to left.
    Searching works across languages whatever language the app shows.
    Problem texts from the system (error codes, exception messages) and the log stay in English.
    Scenario: Arabic reads from right to left
      Given the system's language is Arabic
      When Valafind starts and its launcher opens
      Then its texts are in Arabic, laid out from right to left: the close and menu buttons on the left, the settings
        button on the right, the filters starting from the right with "الكل"
      And the settings window is titled "إعدادات Valafind", its labels on the right and its controls on the left
    Scenario: a language picked in the settings waits for the next start
      Given Valafind shows English
      When the user picks "Deutsch" and saves
      Then the open windows stay in English, the hint having said "Takes effect the next time Valafind starts."
    Scenario: a parent language stands in for the system's own
      Given the system's language is Austrian German ("de-AT")
      When Valafind starts and its launcher opens
      Then its texts are in German, laid out from left to right
    After a restart following "Deutsch" and Save, every window is in German.
    The tray menu follows the language too, but is never mirrored (TrayMenu).
  look
    direction   right to left for Arabic and Urdu: every row mirrored, the search icon and placeholder on the right,
                the shortcut badge on the left; paths and the shortcut stay left to right inside the text
    picker      the language drop-down lists each language in its own name and script, "System default" first
    quirk       in Arabic a path in a sentence can show its leading slash at the far end, "(home/user/…/home/)"
  copy
    system      "System default"
    hint        "Takes effect the next time Valafind starts."
    names       "English", "中文（简体）", "हिन्दी", "Español", "العربية", "Français", "বাংলা", "Português", "Русский",
                "Bahasa Indonesia", "اردو", "Deutsch", "日本語", "मराठी", "Tiếng Việt", "తెలుగు", "Hausa", "Türkçe",
                "தமிழ்", "한국어", "Dansk"
    ar.title    "إعدادات Valafind"
  how
    decide the most spoken languages by number of speakers, then Danish
      because PR #10 picked the 20 most spoken (leaving out Nigerian Pidgin and Egyptian Arabic, which English and
        Arabic cover) and PR #15 added Danish
    decide the language applies at the next start
      because "unknown, confirm with the owner: PR #10 says so, not why not at once"
    decide machine translations
      because "unknown, confirm with the owner: PR #10 asks for a native review of Hausa, Telugu and Tamil"
  uses      LanguageRules
  children  LanguageRules
  sources
    PR #10 "Localize the UI into 20 languages"; PR #15 "Add Danish translation"; README.md, "Languages"
    driven on Linux 2026-11-25 with LANG=ar_EG.UTF-8 on display 244: the launcher, the tray menu and the settings
      window (screenshots); "Deutsch" picked on display 204 (the hint, then Cancel)
    src/Valafind.App/Localization/Loc.cs, Strings.*.resx
```
