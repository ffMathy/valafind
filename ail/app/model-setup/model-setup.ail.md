# Starting without the model

Release builds carry the model inside and unpack it on the first start. A build without it (built from source
without the model) shows the one command that installs it, and starts searching as soon as the model is there.

```ail
node ModelSetup : flow   level: experience
  why   A smaller build without the model must still tell the user exactly how to get it, and must not ask for a
        restart once it is there.   <- AppShell
  what
    Scenario: without the model the launcher shows the command that installs it
      Given a build without the model, started for the first time with an empty data folder
      Then the launcher opens with "EmbeddingGemma 2 model not installed"
      And it says "This build of Valafind comes without the model and does not download it by itself. Install it once by running this command in a terminal:"
      And it shows a command that starts with "bash" and names fetch-model.sh, with a copy button
      And under "It installs into:" the folder models/embeddinggemma-2 in Valafind's data folder, with a copy button
      And a "Check again" button
      And the index panel says "Model unavailable"
    Scenario: the model is noticed without a restart
      Given the launcher shows "EmbeddingGemma 2 model not installed"
      When the model is put into the folder the launcher names
      Then within about ten seconds the launcher shows "Search your files by meaning" instead
    While the model is missing Valafind looks for it every 5 seconds, and also at once when the launcher is opened,
      when "Check again" is clicked, and when the settings are saved.
    The settings' model row says "Not found" with the command and the folder until the model is there, then
      "Detected:" and the folder.
    A release build unpacks its built-in model into the same folder on the first start, in a few seconds, and does
      not show this screen; if unpacking fails it shows "EmbeddingGemma 2 could not be loaded" with "Check again",
      which unpacks it again.
    A model that is there but cannot be loaded shows "EmbeddingGemma 2 could not be loaded" with the reason; it is
      tried again only once its files change.
  copy
    missing.title     "EmbeddingGemma 2 model not installed"
    missing.body      "This build of Valafind comes without the model and does not download it by itself. Install it once by running this command in a terminal:"
    missing.into      "It installs into:"
    missing.footer    "Valafind notices the model by itself as soon as it is there — no restart needed. Already have a copy? Choose its folder in Settings."
    check             "Check again"
    copy              "Copy command"
    loading           "Loading EmbeddingGemma 2…" with "The on-device model takes a few seconds to start. Your search runs as soon as it is ready."
    failed.title      "EmbeddingGemma 2 could not be loaded"
    failed.download   "The model files are probably incomplete. Download them again with:"
    failed.bundled    "The built-in model could not be unpacked or loaded. Free up disk space if needed, then click Check again to unpack it again."
    failed.footer     "Valafind reloads the model by itself once the files change, or choose another model folder in Settings."
    unpack            "Could not unpack the built-in model to {folder}: {reason}"
    state             "Model unavailable — search is off", short "Model unavailable"
  look
    card        a warning icon and the title in semibold, the body, then the command and the folder each in a grey
                rounded box in a monospace font, wrapping, with a copy icon at the trailing end; the footer and a quiet
                "Check again" button under them; the index panel below
    each look   the same card in each design (UiFlavours); the Windows design shows the PowerShell command
  how
    decide the app never downloads the model by itself; the user runs the shipped script
      because the update check is the only network request Valafind makes (PR #8, README)
    decide watch for the model every 5 seconds, with no restart
      because installing the model while the app runs must just work (PR #1)
    decide keep "Choose its folder in Settings" in the footers
      because "unknown, confirm with the owner: keep this? PR #31 removed choosing a model folder, so Settings has no such control"
  uses      ModelInstallHelp, BundledModel, ModelFolder, UiFlavours
  children  ModelInstallHelp
  sources
    PR #1 (first-run screen, detection without a restart); PR #2 (bundled model, "Check again"); PR #8; PR #31
    driven on Linux 2026-11-25: an empty models folder on display 241 (Linux design) and 242 (Windows design); the model
      linked into the named folder while running: the welcome appeared within 9 s; the installed 9.1.0 on display 245
    src/Valafind.App/Services/AppHost.cs, LoadCoreAsync, CheckModel, OnModelFilesChanged; src/Valafind.App/Services/ModelInstallWatcher.cs
    src/Valafind.App/Localization/Strings.resx, Model*
```
