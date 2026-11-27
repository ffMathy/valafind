# Valafind

Find files by what they mean, not what they're called: semantic search for documents, pictures, audio and video that
runs entirely on the user's computer. This spec is written in reverse from the existing app (`/ail:adopt`), one area
at a time; each area's top node is a child of the root below.

```
valafind.ail.md   Valafind (this file)
search/           SearchFiles: the launcher, results, details, keys, filters, ranking and excerpts
indexing/         Indexing: which folders, in what order, watching for changes, pacing, the index panel
kinds/            KindsOfFiles: what is read from documents, pictures, audio and video, and name-only files
model/            ModelAndIndex: the model, its inputs, the device it runs on, chunking, the index database, vector search
app/              AppShell: tray, settings, command line, install, updates, model setup, data, looks, languages
```

## Target

The app as it is built today. Changing this block, then `/ail:build`, moves it to another technology; the end-to-end
checks in `checks/e2e/` prove the new app behaves like this one.

```ail
target
  code         C# on .NET 10, nullable enabled, warnings as errors; Avalonia 12 for windows; ONNX Runtime for the model; SQLite for the index
  structure    Valafind.Core holds indexing, extraction, embedding, storage and search as plain, testable code; Valafind.App is the Avalonia app; Valafind.Model carries the model files
  naming       PascalCase types and members, one public type per file, file named after the type
  copy         user-visible text comes from Strings.resx and its translations, never from code
  path.fn      src/Valafind.Core/Ail/{dir}/{name}.cs
  path.data    src/Valafind.Core/Ail/{dir}/{name}.cs
  path.flow    src/Valafind.App/Ail/{dir}/{name}.cs
  path.view    src/Valafind.App/Ail/{dir}/{name}.axaml
  path.system  src/Valafind.App/Ail/Program.cs
  generated    src/Valafind.Core/Ail/**, src/Valafind.App/Ail/**
  tests        xUnit v3 on Microsoft.Testing.Platform; end-to-end checks are bun tests that drive the desktop with xdotool and read it with tesseract
  checks.path  tests/Valafind.Core.Tests/Ail/{dir}/{name}Tests.cs
  checks.run   dotnet test --project tests/Valafind.Core.Tests -- --filter-class "Valafind.Core.Tests.Ail.{name}Tests"
  checks.path.view  tests/Valafind.App.Tests/Ail/{dir}/{name}Tests.cs
  checks.run.view   VALAFIND_TEST_MODEL_DIR="${VALAFIND_TEST_MODEL_DIR:-$PWD/.model/embeddinggemma-2}" dotnet test --project tests/Valafind.App.Tests -- --filter-class "Valafind.App.Tests.Ail.{name}Tests"
  checks.path.flow  tests/Valafind.App.Tests/Ail/{dir}/{name}Tests.cs
  checks.run.flow   VALAFIND_TEST_MODEL_DIR="${VALAFIND_TEST_MODEL_DIR:-$PWD/.model/embeddinggemma-2}" dotnet test --project tests/Valafind.App.Tests -- --filter-class "Valafind.App.Tests.Ail.{name}Tests"
  checks.harness  the views and flows are the existing app (Valafind.App, Avalonia), so their checks drive the real app on
               Avalonia's headless platform, in tests/Valafind.App.Tests: a check is a public sealed class in the
               file-scoped namespace Valafind.App.Tests.Ail, named after its file, marked
               [Collection(HeadlessUiDefinition.Name)] (using Valafind.App.Tests.Headless) with a primary constructor
               (HeadlessFixture fixture); each test `public Task Name() => AppHarness.RunAsync(fixture, async app => { ... },
               new AppOptions { ... });` (await everything inside; the options are optional). AppOptions: Flavor (UiFlavor.Linux,
               the default; UiFlavor.Windows; UiFlavor.Mac), Language (the system's, as a culture name such as "ar"),
               WithoutModel (a build without the model, with an empty data folder). AppHarness runs the real app host, index,
               model, search, launcher and settings window in a home folder and data folder of its own (no tray, window
               manager or file manager; the global hotkey counts as registered): before or after await app.StartAsync()
               (which opens the launcher), save files with app.SaveText(relative path, text, modified), SaveBytes,
               SaveRandomBytes, SavePicture(path, Picture.RedCircle | Picture.BlueSquare | Picture.Word, word) in the format of
               its extension, SaveFixture(name, path) (speech.wav and speech.mp3 read out a weather forecast),
               SaveWithFfmpeg(path, arguments) ("fixture:x" and "home:x" name inputs), SaveDocument(path, format,
               paragraphs) (docx, xlsx, pptx, odt, epub, rtf, pdf, eml, mbox, ipynb); AppHarness.Unique() gives a code
               nothing else contains. Then await app.WaitUntilIndexedAsync() (status "Up to date"), app.SearchAsync(query),
               or app.SearchUntilAsync(query, () => condition) to search again until the index has caught up;
               app.PressAsync(Key.X, modifiers) presses a key in the launcher; app.SetPausedAsync(true) pauses indexing as
               "Pause indexing" does; app.OpenSettingsAsync() opens and returns the settings window; app.ShowLauncher().
               What there is to see: app.ResultNames (the listed file names, top first), app.Launcher (the launcher's view model: Results, SelectedResult,
               SelectedIndex, Filters and SelectedFilter (each chip's SelectCommand picks it), TimeRanges and
               SelectedTimeRange, ResultSummary, Content, Status (StateText, IsUpToDate, IsPaused…), ModelState),
               app.LauncherWindow, app.SettingsWindow; AppHarness.Texts(window) lists every text it shows, in reading
               order; AppHarness.Find<T>(window) its visible controls of a type; app.Opened the files and links it opened;
               app.Home and app.Data the folders. Each test ends with AppHarness.SaveScreen(window, "<the file name it was
               given>") on the window its scenario ends on.
```

## Glossary

```ail
glossary
  term launcher     plain "the search window that opens with Ctrl+Alt+Space"          formal LauncherWindow
  term index        plain "what Valafind knows about the user's files, kept on disk"   formal index.db
  term "the model"  plain "EmbeddingGemma 2, which turns text, pictures and sound into meanings that can be compared"   formal Embedder
  term match        plain "how close a file's meaning is to what was asked, from 0 to 1"   formal the cosine similarity of their embeddings
  term chunk        plain "a passage of a document that is understood and found on its own"   formal TextChunk
  term passage      plain "a piece of a document's text, about a paragraph or two, matched and shown under a result"   formal TextChunk
  term "data folder" plain "the one folder where Valafind keeps its settings, index, model and logs"   formal AppPaths.DataDirectory
  term excerpt      plain "the passage of a file shown under its name"   formal SearchResult.snippet
  term "details pane" plain "the panel beside the results that shows the chosen file"   formal DetailsPane
  term "kind chip"  plain "a button under the results that keeps one kind of file"   formal FilterChip
  term highlight    plain "the words or sentence marked in light blue that explain a match"   formal SearchResult.highlights
  term kind         plain "what sort of file it is: document, PDF, spreadsheet, presentation, text, code, mail, notebook, picture, audio, video or archive"   formal FileKind
  term "found by name only" plain "the file is found through its name and folders, never through what is inside it"   formal one vector of the title and the path below the indexed folder
  term "Phase 1"    plain "first, the files the user used recently or often"   formal IndexPhase.Frequent
  term "Phase 2"    plain "then every other file in the indexed folders"   formal IndexPhase.All
  term "indexed folders" plain "the folders Valafind reads: the home folder unless others are chosen in Settings"   formal ValafindSettings.Roots
  term "index panel" plain "the launcher's progress box shown while indexing runs"   formal IndexPanelView
  term pace         plain "how hard indexing works, kept under the processor and disk limits"   formal IndexingPace.Effort
  term tray         plain "the icon in the notification area (menu bar on a Mac) that shows Valafind runs"   formal TrayIconController
  term "settings window" plain "the window with every choice, opened from the tray or the launcher"   formal SettingsWindow
  term design       plain "the look of the launcher on Windows, Mac or Linux"   formal UiFlavor
```

## Design tokens

The Linux launcher's light theme, from `src/Valafind.App/Views/Linux/LinuxLauncherView.axaml`, and its type from
`src/Valafind.App/Styles/Common.axaml`. The other designs' values are in their own nodes.

```ail
catalog
  tokens
    color.accent=#3584E4  color.text=#1F1F23  color.subtle=#55555C  color.track=#DCDCE0
    color.highlight=#593584E4  color.row.selected=#293584E4  color.good=#1F7A3D  color.good.bar=#2EC27E
    color.busy=#8A4B00  color.busy.bar=#C64600  color.error=#C01C28  color.panel=#FFFFFF  color.panel.border=#12000000
    color.divider=#12000000
    radius.card=14px  radius.boxed=12px  radius.panel=12px  radius.chip=99px
    size.launcher.width=1220px  size.launcher.height=786px  size.pane=440px  size.search.width=470px  size.search.height=34px
```

---

```ail
node Valafind : system   level: product
  why   People remember what a file was about, not what it was called or where they put it.
  what
    1. Press Ctrl+Alt+Space anywhere, describe a file in your own words, and the launcher lists the files closest in meaning, best first.   refined-by: SearchFiles, OpenLauncher
    2. A question finds a file written in another language: an English question finds a Danish lease.   refined-by: SearchFiles
    3. Each result shows where the file is, when it changed, how close the match is, and the passage that explains it.   refined-by: ResultRow, DetailsPane, SearchRules
    4. The keyboard does everything, and chips and a time menu narrow the results.   refined-by: LauncherKeys, ActOnResult, NarrowResults
    5. Documents are found by their text, pictures by what they show, recordings by what is said or heard, videos by their frames and sound.   refined-by: TextDocuments, Pictures, Audio, Video
    6. A file Valafind cannot read is still found by its name and folder.   refined-by: NameOnly, TextSniffing
    7. Valafind reads the home folder, or the folders chosen in Settings, and skips hidden, app-data, build and cache folders.   refined-by: WhatGetsIndexed
    8. Everyday files and documents are read first, then pictures, audio and video.   refined-by: IndexOrder
    9. The index keeps up by itself: a file added, changed, moved or deleted is updated within seconds.   refined-by: WatchChanges
    10. Indexing uses only spare processor and disk capacity, up to limits the user sets, and shows its progress while it runs.   refined-by: Pacing, IndexPanel
    11. Everything runs on the user's computer with the model built in; nothing is downloaded and nothing leaves it.   refined-by: ModelAndIndex, EmbeddingModel
    12. The model runs on the graphics card when one is usable and on the processor otherwise; a search never waits for indexing.   refined-by: ComputeDevice, RunScheduler
    13. The index stays on the computer, readable only by the user.   refined-by: IndexDatabase
    14. Valafind waits in the tray; one settings window holds every choice.   refined-by: TrayMenu, SettingsWindow, ChangeSettings
    15. A downloaded release installs itself for the user without administrator rights, starts at login and updates itself.   refined-by: SelfInstall, AutoUpdate
    16. A build without the model shows the command that installs it, and notices the model without a restart.   refined-by: ModelSetup
    17. It speaks 21 languages, and has a launcher design for Windows, Mac and Linux.   refined-by: Languages, UiFlavours
  entry     SearchFiles
  children  SearchFiles, Indexing, KindsOfFiles, ModelAndIndex, AppShell
  sources
    README.md
```
