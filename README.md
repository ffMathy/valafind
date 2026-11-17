# Valafind

**Find files by what they mean, not what they're called.** Press a hotkey, describe what you're looking for in plain
language — *"the lease agreement where we negotiated the deposit"* — and Valafind finds the right file even if it's a
Danish PDF called `scan_0042.pdf`.

- **Semantic search** with [EmbeddingGemma-300m](https://ai.google.dev/gemma/docs/embeddinggemma), multilingual (100+ languages).
- **Entirely on-device.** The model runs locally through ONNX Runtime. Valafind makes **no network requests** — no cloud,
  no telemetry, no update checks. Your file contents never leave the computer.
- **Polite background indexing.** It only indexes while CPU and disk are quiet, at background CPU/I-O priority, and
  steps aside the moment you open the search window.
- **Recent files first.** Phase 1 indexes the files you actually use (the OS's recent/frequent file lists); Phase 2 then
  works through all your files; afterwards changes are picked up incrementally.
- **Native-feeling on every desktop**: a Fluent command launcher on Windows, a floating search bar on macOS and a
  GNOME-style dialog on Linux. Built with .NET 10 and [Avalonia](https://avaloniaui.net/).

## Getting started

### 1. Get Valafind

Download the build for your platform from the CI artifacts (`valafind-win-x64`, `valafind-osx-arm64` (zipped
`Valafind.app`), `valafind-linux-x64`, …), or build it yourself:

```bash
dotnet publish src/Valafind.App -c Release -r linux-x64 --self-contained -p:PublishSingleFile=true -o publish
```

Use the runtime identifier for your machine: `win-x64`, `win-arm64`, `osx-arm64`, `osx-x64`, `linux-x64`, `linux-arm64`.
On macOS, wrap the output in an app bundle with `packaging/macos/make-app-bundle.sh publish dist`.

### 2. Install the model (one time)

The embedding model (~310 MB) is not bundled. Download it once with the script that ships next to the app
(`tools/` in the app folder; inside `Valafind.app/Contents/MacOS/tools/` on macOS):

```bash
tools/fetch-model.sh
```

On Windows:

```powershell
powershell -ExecutionPolicy Bypass -File tools\fetch-model.ps1
```

This is the **only** network access involved, and it's you running a script — the app itself never downloads anything.
The model is [EmbeddingGemma](https://huggingface.co/onnx-community/embeddinggemma-300m-ONNX), licensed under the
[Gemma Terms of Use](https://ai.google.dev/gemma/terms). It's installed to:

| OS | Model folder |
|---|---|
| Windows | `%LOCALAPPDATA%\Valafind\models\embeddinggemma-300m` |
| macOS | `~/Library/Application Support/Valafind/models/embeddinggemma-300m` |
| Linux | `${XDG_DATA_HOME:-~/.local/share}/valafind/models/embeddinggemma-300m` |

If Valafind is already running it notices the model by itself and starts indexing. You can also point it at another
folder in Settings, or ship the model inside the app folder (`models/embeddinggemma-300m` next to the executable, or
`Valafind.app/Contents/Resources/models/embeddinggemma-300m`).

### 3. Search

Valafind lives in the system tray / menu bar. Open it with the global hotkey:

| OS | Default hotkey |
|---|---|
| Windows | **Ctrl+Alt+Space** |
| macOS | **⌃⌥Space** (Control+Option+Space) |
| Linux | **Ctrl+Alt+Space** |

Change it in Settings. Defaults deliberately avoid system shortcuts (⌘Space is Spotlight, Super+Space switches input
sources on GNOME, Alt+Space is the window menu on Windows and KRunner on KDE; macOS 15+ also refuses Option-only hotkeys).

**Wayland:** ordinary apps can't grab global keys. On desktops with the *GlobalShortcuts* portal (KDE Plasma, GNOME 48+)
Valafind asks the desktop to bind the shortcut. Otherwise bind a custom shortcut in your desktop's keyboard settings to
`<path-to-valafind> --toggle` (Settings shows the exact command). For the portal to recognise the app, install
`packaging/linux/app.valafind.Valafind.desktop` into `~/.local/share/applications/`.

In the launcher: type to search, **↑/↓** to move, **Enter** to open, **Ctrl+Enter** (⌘↵) to show in the file manager,
**Esc** to close. Filter chips narrow results to documents, PDFs, spreadsheets, slides, mail, code or images.

## How indexing works

1. **Phase 1 – files you use.** Windows *Recent* items, macOS Spotlight's last-used dates, Linux
   `recently-used.xbel` / KDE recent documents, plus recently changed files in Desktop, Documents and Downloads.
2. **Phase 2 – all your files.** A breadth-first walk of your home folder (or the folders you choose in Settings), so
   shallow, relevant folders come before deep ones.
3. **Incremental.** File-system watching picks up new, changed, moved and deleted files; a full re-check runs daily, and
   entries for files that disappeared are removed.

**When it runs.** Indexing waits until the machine has been calm (by default CPU below 30 % and disk busy below 25 % for
15 seconds — Valafind's own usage is excluded from the measurement), pauses on battery (configurable), pauses while the
search window is open, and runs at background CPU and I/O priority. You can pause it from the tray or click
*Resume now* to override the throttle for 30 minutes.

**What's skipped.** Hidden folders and app data (`AppData`, `~/Library`, `.config`, …), build and dependency folders
(`node_modules`, `.git`, `bin`/`obj` next to a project, virtualenvs, caches), the Trash, system folders, symlinks, and
cloud placeholders that would trigger a download (OneDrive / iCloud files that aren't on disk). Add your own exclusions in
Settings.

**What's read.**

| Content is indexed | Indexed by name only |
|---|---|
| Plain text, Markdown, code, HTML, JSON/XML/YAML, logs | Images (no OCR), audio, video, archives |
| PDF | Legacy Office `.doc` / `.xls` / `.ppt`, Outlook `.msg` |
| Word / Excel / PowerPoint (`.docx` `.xlsx` `.pptx`) | Apple iWork (`.pages` `.numbers` `.key`) |
| OpenDocument (`.odt` `.ods` `.odp`), EPUB, RTF | Anything above the size limit (64 MB) |
| E-mail (`.eml`, `.mbox`), Jupyter notebooks | |

To keep a first pass over hundreds of thousands of files feasible on a laptop CPU, each document contributes its first
~32,000 characters (up to 12 chunks of ~320 tokens). Embedding one chunk takes roughly half a second on two CPU threads,
so a large home folder takes days of *idle time* to index fully — Phase 1 makes your everyday files searchable first.

**Hostile files.** Extractors are bounded in memory and time (decompression and XML bombs, oversized streams), and a
file that can't be read is simply indexed by name.

## Your data

Everything lives in one folder (override with `VALAFIND_DATA_DIR`):

| OS | Data folder |
|---|---|
| Windows | `%LOCALAPPDATA%\Valafind` |
| macOS | `~/Library/Application Support/Valafind` |
| Linux | `${XDG_DATA_HOME:-~/.local/share}/valafind` |

It holds `settings.json`, the index (`index.db`, SQLite — it contains text excerpts of your files, so it's created
readable only by you), `models/` and `logs/`. Delete `index.db` to rebuild the index from scratch.

## Development

```bash
dotnet build Valafind.slnx
dotnet test --solution Valafind.slnx
```

Tests that need the real model (tokenizer parity with Hugging Face `tokenizers`, embedding parity with Python ONNX
Runtime, and an end-to-end index-and-search test) are skipped unless you point them at a model folder:

```bash
VALAFIND_TEST_MODEL_DIR=~/.local/share/valafind/models/embeddinggemma-300m dotnet test --solution Valafind.slnx
```

Set `VALAFIND_UI=windows|mac|linux` to try another platform's launcher design, and `VALAFIND_SCREENSHOT_DIR=<dir>`
while running `tests/Valafind.App.Tests` to render the launchers headlessly.

| Project | What's in it |
|---|---|
| `src/Valafind.Core/Abstractions` | Module contracts (`IEmbeddingModel`, `IIndexStore`, `ISearchService`, `IIndexingService`, …) |
| `src/Valafind.Core/Embedding` | EmbeddingGemma on ONNX Runtime; exact Hugging Face BPE tokenizer; model locator |
| `src/Valafind.Core/Extraction` | Text extractors per format; token-aware chunker |
| `src/Valafind.Core/Storage` | SQLite index + in-memory int8 vector matrix with SIMD search and exact re-scoring |
| `src/Valafind.Core/Search` | Query embedding, ranking, snippets and "why it matched" highlights |
| `src/Valafind.Core/Indexing` | Throttle gate, file discovery (Phase 1/2), watcher, indexing coordinator |
| `src/Valafind.Core/Platform` | CPU / disk / battery pressure monitors and background priority per OS |
| `src/Valafind.App` | Avalonia app: launchers per OS, tray, settings, global hotkeys, single instance, shell integration |
| `tools/` | Model download scripts; reference-fixture generator for the parity tests |
| `packaging/` | macOS `Info.plist` + app bundle script, Linux `.desktop` entry |

## License notes

EmbeddingGemma is provided by Google under the [Gemma Terms of Use](https://ai.google.dev/gemma/terms); Valafind does not
redistribute the weights.
