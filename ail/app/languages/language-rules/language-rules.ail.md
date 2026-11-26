# Choosing the language

The list of languages, how the saved choice is applied at start, and which ones read from right to left.

```ail
node LanguageRules : data   level: system
  why   The language must be settled once, before any window exists, so that every text and its direction agree;
        and a hand-edited or unknown choice must never stop the app.   <- Languages
  what
    type UiLanguage = { code: text, nativeName: text }
    LANGUAGES: list of UiLanguage = [{ code: "en", nativeName: "English" }, { code: "zh-Hans", nativeName: "中文（简体）" },
      { code: "hi", nativeName: "हिन्दी" }, { code: "es", nativeName: "Español" }, { code: "ar", nativeName: "العربية" },
      { code: "fr", nativeName: "Français" }, { code: "bn", nativeName: "বাংলা" }, { code: "pt", nativeName: "Português" },
      { code: "ru", nativeName: "Русский" }, { code: "id", nativeName: "Bahasa Indonesia" }, { code: "ur", nativeName: "اردو" },
      { code: "de", nativeName: "Deutsch" }, { code: "ja", nativeName: "日本語" }, { code: "mr", nativeName: "मराठी" },
      { code: "vi", nativeName: "Tiếng Việt" }, { code: "te", nativeName: "తెలుగు" }, { code: "ha", nativeName: "Hausa" },
      { code: "tr", nativeName: "Türkçe" }, { code: "ta", nativeName: "தமிழ்" }, { code: "ko", nativeName: "한국어" },
      { code: "da", nativeName: "Dansk" }]
    languageOptions() -> list of UiLanguage — { code: "", nativeName: "System default" } followed by LANGUAGES
    applyLanguage(code: text or nothing) -> text — at start, before any window: a known culture name sets the text
      language, and the number and date formats to the same (a neutral one, such as "de", to its usual region);
      nothing, blank or a name the system does not know keeps the system's; returns the language now used
    matchLanguage(culture: text) -> UiLanguage or nothing — the culture or its nearest parent in LANGUAGES, any case
    isRightToLeft(culture: text) -> boolean — what the system says of the culture's writing; true for ar and ur
    formatText(template: text, values: list of text) -> text — fills {0}, {1}, … with the current culture's formats
    splitAroundValue(template: text) -> { before: text, after: text } — the parts around the single {0}, for
      sentences whose value is bold; without {0}, all of it is before
    examples
      matchLanguage("de-AT") => { code: "de", nativeName: "Deutsch" }
      matchLanguage("zh-CN") => { code: "zh-Hans", nativeName: "中文（简体）" }
      matchLanguage("sv-SE") => nothing
      isRightToLeft("ar") => true;  isRightToLeft("ur") => true;  isRightToLeft("he") => true;  isRightToLeft("de") => false
      splitAroundValue("Summon Valafind from anywhere with {0}.") => { before: "Summon Valafind from anywhere with ", after: "." }
      splitAroundValue("Search your files by meaning") => { before: "Search your files by meaning", after: "" }
      the number of LANGUAGES => 21
    every translation holds every English text and the same {n} placeholders; English is the fallback for a text
      a translation lacks
    the saved choice is Settings.Language (SettingsFile); "System default" saves nothing
  how
    decide translations as resource files per culture, the English one the source
      because the build makes a typed accessor and satellite files, also bundled into single-file builds (PR #10)
    decide the date and number formats follow a chosen language, but the system's region when none is chosen
      because a sentence must not mix conventions (Loc.Apply)
    decide sentences with a bold shortcut are split around their value
      because each language may put the value elsewhere in the sentence (PR #10)
  sources
    PR #10; PR #15; src/Valafind.App/Localization/Loc.cs; src/Valafind.App/Localization/Strings.resx and Strings.*.resx
    src/Valafind.App/ViewModels/SettingsViewModel.cs, LanguageOptions; src/Valafind.App/Services/AppHost.cs, Create
    tests/Valafind.App.Tests (LocalizationTests, per PR #10)
```
