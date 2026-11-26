# What gets indexed

The home folder by default, or the folders listed in Settings; inside them every file except those in folders that
hold no files of the user's own.

```ail
node WhatGetsIndexed : flow   level: experience
  why   The index should hold the user's own files and nothing else: installed packages, version control, build
        output and caches would bury real results and cost hours of indexing.   <- Indexing
  what
    Scenario: a file saved anywhere in the home folder is found
      Given a file "Recipes/<id>/pancakes-<id>.txt" in the home folder that says "a recipe for blueberry pancakes"
      When the user searches for "blueberry pancakes recipe"
      Then the launcher lists "pancakes-<id>.txt" under "~/Recipes/<id>"
    Scenario: hidden folders, installed packages and version control are skipped
      Given the same recipe saved in the home folder as "Kitchen/<id>/kept-<id>.txt", ".hidden-<id>/hidden-<id>.txt",
        "node_modules/<id>/package-<id>.txt" and ".git/<id>/git-<id>.txt"
      When the user searches for it, until "kept-<id>.txt" is listed
      Then none of the other three is listed
    Scenario: build output next to a project file is skipped, a folder of the same name elsewhere is not
      Given a folder "Web-<id>" holding "package.json", with the recipe in "Web-<id>/dist/built-<id>.txt"
      And the recipe in "Notes-<id>/dist/notes-<id>.txt", a folder without a project file
      When the user searches for it, until "notes-<id>.txt" is listed
      Then "built-<id>.txt" is not listed
    Scenario: a folder added in Settings is indexed along with the home folder
      Given a folder "Shelf-<id>" next to the home folder (outside it) holding "shelf-<id>.txt" about a lighthouse
      When the user opens Settings, adds the home folder and "Shelf-<id>" with "Add folder…", and presses Save
      Then searching for the lighthouse lists "shelf-<id>.txt"
      When the user removes both folders again and presses Save
      Then "shelf-<id>.txt" is no longer listed
    With no folders listed in Settings the home folder is indexed; adding a folder replaces the home folder, so the
      user adds it too to keep it. A folder listed inside another listed folder counts once.
    Removing a folder from the list removes its files from the index once the folders have been walked again.
    These folders are skipped: every folder whose name starts with a dot (the Trash, caches and settings live in such folders
      on Linux), "node_modules", "bower_components", "venv", "__pycache__", the Windows recycle bin, "System Volume
      Information", "lost+found", the app-data folder of the home folder (AppData on Windows, Library on a Mac, except
      the cloud drive folders inside it), Valafind's own data folder, a folder that holds
      "CACHEDIR.TAG" or "pyvenv.cfg", "bin" and "obj" next to a .NET project, "dist" and "build" next to
      "package.json", "target" next to "Cargo.toml", and packages on a Mac (apps, libraries, bundles).
    These files are skipped: names starting with a dot or "~$" (Office lock files), empty files, links, and on a Mac the
      ".icloud" placeholders. A link to a folder is never followed.
    Further folder names or paths can be skipped through "Excludes" in the settings file; Settings has no control for it.
  how
    decide the home folder by default, and the folders in Settings replace it rather than add to it
      because "unknown, confirm with the owner: why replace rather than add (the hint in Settings tells the user to add the home folder too)"
    decide folder-name rules apply only below the folder the user listed, so a listed folder is never skipped for its own name
      because a folder the user picked on purpose must be indexed (ExclusionRules, code comment)
    decide build folders are skipped only next to the project file that makes them
      because a folder called "build" or "dist" may hold the user's own files (ExclusionRules, IsExcludedByContext)
    decide every dot folder is skipped, not a list of known ones
      because "unknown, confirm with the owner"
    decide changes to the folders restart indexing and walk the new folders straight away
      because files newly in scope must not wait for the daily check (PR #1, "Changing roots or excludes triggers a rescan")
  uses      ExclusionRules
  children  ExclusionRules, FileDiscovery
  sources
    README.md, "What gets indexed" ("Skipped: hidden and app-data folders, node_modules, .git, build output, caches and the Trash")
    GitHub PR #1 (roots and excludes, symlinks never followed)
    driven on Linux (Xvfb, openbox) 2026-11-25: files in node_modules, .git, a dot folder, dist next to package.json and Documents; only the Documents one was listed. Settings: "Add folder…" opens the system folder chooser "Choose a folder to index"; after Save the log said "Index roots or excludes changed; re-walking all roots" and the files outside the new folder left the results
    src/Valafind.App/Localization/Strings.resx: SettingsRootsHint, SettingsHomeFolderRow, SettingsAddFolder, PickFolderToIndex
    src/Valafind.Core/Indexing/UserFileDiscovery.cs, src/Valafind.Core/Indexing/ExclusionRules.cs
```
