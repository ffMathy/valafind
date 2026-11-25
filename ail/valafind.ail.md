# Valafind

Find files by what they mean, not what they're called: semantic search for documents, pictures, audio and video that
runs entirely on the user's computer. This spec is written in reverse from the existing app (`/ail:adopt`), one area
at a time; each area's top node is a child of the root below.

## Target

The app as it is built today. Changing this block, then `/ail:build`, moves it to another technology; the end-to-end
checks in `checks/e2e/` prove the new app behaves like this one.

```ail
target
  code         C# on .NET 10, nullable enabled, warnings as errors; Avalonia 12 for windows; ONNX Runtime for the model; SQLite for the index
  structure    Valafind.Core holds indexing, extraction, embedding, storage and search as plain, testable code; Valafind.App is the Avalonia app; Valafind.Model carries the model files
  naming       PascalCase types and members, one public type per file, file named after the type
  copy         user-visible text comes from Strings.resx and its translations, never from code
  path.fn      src/Valafind.Core/Ail/{dir}/{Pascal}.cs
  path.data    src/Valafind.Core/Ail/{dir}/{Pascal}.cs
  path.flow    src/Valafind.App/Ail/{dir}/{Pascal}.cs
  path.view    src/Valafind.App/Ail/{dir}/{Pascal}.axaml
  path.system  src/Valafind.App/Ail/Program.cs
  generated    src/Valafind.Core/Ail/**, src/Valafind.App/Ail/**
  tests        xUnit v3 on Microsoft.Testing.Platform; end-to-end checks are bun tests that drive the desktop with xdotool and read it with tesseract
```

## Glossary

```ail
glossary
  term launcher     plain "the search window that opens with Ctrl+Alt+Space"          formal LauncherWindow
  term index        plain "what Valafind knows about the user's files, kept on disk"   formal index.db
  term "the model"  plain "EmbeddingGemma 2, which turns text, pictures and sound into meanings that can be compared"   formal Embedder
  term match        plain "how close a file's meaning is to what was asked, from 0 to 1"   formal the cosine similarity of their embeddings
```

---

```ail
node Valafind : system   level: product
  why   People remember what a file was about, not what it was called or where they put it.
  sources
    README.md
```
