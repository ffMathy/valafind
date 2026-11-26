# One row per file

How a path is stored and turned into the unique key of its row, so that a file has one row however its path is spelled.

```ail
node PathKeys : fn   level: system
  why   Windows and macOS treat "Report.pdf" and "report.pdf" as one file, and macOS hands out the same name in two
        Unicode spellings; Linux keeps them apart. A file indexed twice would show up twice.   <- IndexDatabase
  what
    type PathKeyPolicy = { caseInsensitive: boolean, normalizeUnicode: boolean, separator: text }
    the policy per platform: Windows => case-insensitive; macOS => case-insensitive and NFC; Linux => exact
    normalizePath(path: text) -> text — the full path with "." and ".." resolved; a path that cannot be resolved is kept as it is
    PathKeyPolicy.keyOf(normalizedPath: text) -> text — NFC when normalizeUnicode (a lone surrogate keeps the raw
      spelling), then lower case (invariant) when caseInsensitive
    PathKeyPolicy.keyForPath(path: text) -> text — keyOf(normalizePath(path))
    PathKeyPolicy.directoryRange(directory: text) -> { exact: text, lower: text, upper: text } — exact is the key of
      the directory without trailing separators, lower is exact + separator, upper is lower with its last character
      replaced by the next character; a key k is the directory or below it when k is exact, or lower ≤ k < upper in
      byte order; a root ("/", "C:\") keeps its separator
    examples
      CaseSensitive.directoryRange("/home/a/docs/") => { exact: "/home/a/docs", lower: "/home/a/docs/", upper: "/home/a/docs0" }
      "/home/a/docs/z/ü/𝄞" is in that range => true
      "/home/a/docs2/x", "/home/a/docs.txt" and "/home/a/docs-old/x" are in it => false
      CaseSensitive.directoryRange("/") => { lower: "/", upper: "0" }
      CaseInsensitiveNfc.keyForPath("/A/Ä.txt") is CaseInsensitiveNfc.keyForPath("/a/A" + U+0308 + ".TXT") => true
      CaseSensitive.keyForPath("/a/./c/../b") => "/a/b"
      case-insensitive: upsertFile("/Users/A/Report.PDF") then upsertFile("/users/a/report.pdf") => one file, found as "/users/a/report.pdf"
      case-insensitive: removeUnder("/USERS/a/") over that file and "/Users/A/Docs2/x" => 2
      case-sensitive: upsertFile("/a/Report.pdf") then upsertFile("/a/report.pdf") => two files, and fingerprint("/a/REPORT.pdf") => nothing
    a result shows path, the spelling of the last write; the key is only for lookups
  how
    decide list and remove a folder's files by a range of the unique key, paging by the key
      because each page is then an index range scan, and removals between pages cannot make the listing skip or
      repeat a file (SqliteIndexStore.GetIndexedPaths)
    decide the separator plus one as the upper bound
      because every key below the directory continues with the separator, and both are ASCII, untouched by case
      folding or NFC
    decide fold case on Windows and macOS only
      because their usual file systems are case-insensitive and Linux's are not (PathKeyPolicy)
  sources
    src/Valafind.Core/Storage/PathKeyPolicy.cs; SqliteIndexStore.cs
    tests/Valafind.Core.Tests/Storage/VectorIndexTests.cs, PathKeyPolicyTests; SqliteIndexStoreTests.cs,
      Case_insensitive_policy_treats_case_variants_as_one_file, Case_sensitive_policy_keeps_case_variants_apart,
      RemoveUnder_matches_whole_path_components
```
