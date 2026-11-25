# Exclusion rules

Which paths inside the indexed folders are left out, decided by name, by place and by what sits next to a folder.

```ail
node ExclusionRules : fn   level: system
  why   The walk, the watchers and the clean-up of stale entries must all agree on what is out of scope, on every
        platform, and the rules must be testable without the platform at hand.   <- WhatGetsIndexed
  what
    type PathFlavor = "windows" | "macOS" | "linux"
    type ExclusionProbe = { fileExists(path: text) -> boolean, containsFile(folder: text, namesOrExtensions: list of text) -> boolean }
    ExclusionRules(flavor: PathFlavor, home: text, roots: list of text, dataFolder: text?, userExcludes: list of text, probe: ExclusionProbe?) -> ExclusionRules
    ExclusionRules.isExcluded(path: text, isDirectory: boolean) -> boolean — the full check of any path, every component below its root
    ExclusionRules.isExcludedDirectory(folder: text, parent: text) -> boolean — the walk's check of one folder whose parents were accepted
    ExclusionRules.isExcludedFileName(name: text) -> boolean
    ExclusionRules.isExcludedByNameOnly(path: text) -> boolean — the watchers' check, without touching the disk; the last part is tested as a folder and as a file
    ExclusionRules.findRoot(path: text) -> text?
    ALWAYS_EXCLUDED_FOLDERS: list of text = ["node_modules", "bower_components", "venv", "__pycache__", "$RECYCLE.BIN", "System Volume Information", "lost+found"]
    MAC_PACKAGE_EXTENSIONS: list of text = [".app", ".framework", ".bundle", ".kext", ".photoslibrary", ".musiclibrary", ".tvlibrary", ".photolibrary", ".xcarchive", ".dSYM", ".plugin", ".appex"]
    Names compare exactly on Linux and ignoring case on Windows and macOS; package extensions and project markers always ignore case.
    A user exclude "~" or starting "~/" is under the home folder; a rooted one is a path prefix; one with a separator is a
      path under the home folder; any other is a folder name excluded at every depth.
    in the examples, r is ExclusionRules("linux", "/home/u", ["/home/u"], "/home/u/.local/share/valafind", ["Archive", "~/Old", "Projects/tmp"], disk) unless said otherwise
    examples
      r.isExcluded("/home/u/Documents/lease.pdf", false) => false
      r.isExcluded("/srv/shared/lease.pdf", false) => true
      r.isExcluded("/home/u/.cache/thumbs/a.png", false) => true
      r.isExcluded("/home/u/.local/share/Trash/files/a.txt", false) => true
      r.isExcluded("/home/u/code/app/node_modules/x/readme.md", false) => true
      r.isExcluded("/home/u/Documents/.draft.txt", false) => true
      r.isExcluded("/home/u/Documents/~$report.docx", false) => true
      r.isExcluded("/home/u/Documents/Archive/2019.txt", false) => true
      r.isExcluded("/home/u/Old/a.txt", false) => true
      r.isExcluded("/home/u/Projects/tmp/a.txt", false) => true
      r.isExcluded("/home/u/Projects/src/a.txt", false) => false
      r.isExcludedDirectory("/home/u/web/dist", "/home/u/web") => true   where /home/u/web holds package.json
      r.isExcludedDirectory("/home/u/notes/dist", "/home/u/notes") => false   where /home/u/notes holds no package.json
      r.isExcludedDirectory("/home/u/app/bin", "/home/u/app") => true   where /home/u/app holds app.csproj
      r.isExcludedDirectory("/home/u/rust/target", "/home/u/rust") => true   where /home/u/rust holds Cargo.toml
      r.isExcludedDirectory("/home/u/data/cache", "/home/u/data") => true   where /home/u/data/cache holds CACHEDIR.TAG
      ExclusionRules("linux", "/home/u", ["/home/u/.notes"], nothing, [], disk).isExcluded("/home/u/.notes/a.txt", false) => false
      ExclusionRules("windows", "C:\Users\u", ["C:\Users\u"], nothing, [], disk).isExcluded("C:\Users\u\AppData\Local\x.txt", false) => true
      ExclusionRules("windows", "C:\Users\u", ["C:\Users\u\AppData\Roaming\Notes"], nothing, [], disk).isExcluded("C:\Users\u\AppData\Roaming\Notes\a.txt", false) => false
      ExclusionRules("macOS", "/Users/u", ["/Users/u"], nothing, [], disk).isExcluded("/Users/u/Library/Caches/a.txt", false) => true
      ExclusionRules("macOS", "/Users/u", ["/Users/u"], nothing, [], disk).isExcluded("/Users/u/Library/Mobile Documents/com~apple~CloudDocs/a.txt", false) => false
      ExclusionRules("macOS", "/Users/u", ["/Users/u"], nothing, [], disk).isExcluded("/Users/u/Apps/Tool.app/Contents/a.txt", false) => true
      ExclusionRules("macOS", "/Users/u", ["/Users/u"], nothing, [], disk).isExcludedFileName("report.pdf.icloud") => true
      r.isExcludedByNameOnly("/home/u/Documents/.git") => true
      r.isExcludedByNameOnly("/home/u/web/dist/a.js") => false
  how
    decide name rules apply only to components below the root that contains the path
      because a folder the user listed explicitly is never excluded by its own name (code comment)
    decide every dot folder is excluded by name
      because it covers .git, .cache, .config, .local, the Trash and .venv at once (code comment)
    decide a root placed inside AppData or Library by the user is honoured
      because the user chose it (code comment)
    decide the watchers' pre-filter never touches the disk; context rules (project files, CACHEDIR.TAG) are checked when the change is handled
      because watcher events arrive in bursts and must be cheap to drop (code comment)
  sources
    src/Valafind.Core/Indexing/ExclusionRules.cs
    src/Valafind.Core/Indexing/UserFileDiscovery.cs (IsExcluded, IsExcludedByNameOnly, data folder and user excludes)
```
