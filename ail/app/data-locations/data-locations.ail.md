# Where the data lives

Everything Valafind keeps is in one folder per user, in each platform's usual place, and only that user can read it.

```ail
node DataLocations : data   level: system
  why   The index holds excerpts of the user's files, so other users of the computer must not be able to read it, and
        people must be able to find, back up or delete what Valafind keeps.   <- AppShell
  what
    dataFolder() -> text
      VALAFIND_DATA_DIR set and not blank => that folder, as it is
      Windows => %LOCALAPPDATA%\Valafind
      macOS => H/Library/Application Support/Valafind (in the Mac App Store sandbox, H is the app's container)
      Linux => $XDG_DATA_HOME/valafind, or H/.local/share/valafind when XDG_DATA_HOME is unset or blank
    in the data folder:
      settings.json (SettingsFile), index.db (the index), models/embeddinggemma-2 (ModelFolder), logs/ (AppLog),
      instance.lock (SingleInstance), install.json (SelfInstall), gpu-check.pending (ComputeDevice)
    makePrivateFolder(folder: text) -> nothing — creates the folder and its parents; on macOS and Linux it is created
      with mode 0700 and an existing one loses every group and other permission; failing to tighten is ignored;
      Windows relies on the per-user profile's permissions
    privateFileMode: on macOS and Linux files that hold private data are created with mode 0600
    the folder is made private when the index, the settings, a log file or the instance lock is first written
    the user's home for the indexed folders is the real home, also in the Mac App Store sandbox (the part of the
      container path before "/Library/Containers/")
    examples
      dataFolder() with VALAFIND_DATA_DIR="/tmp/v" => "/tmp/v"
      dataFolder() on Linux with H="/home/u", XDG_DATA_HOME unset => "/home/u/.local/share/valafind"
      dataFolder() on Linux with XDG_DATA_HOME="/x" => "/x/valafind"
      realHome("/Users/me/Library/Containers/app.valafind.Valafind/Data", inSandbox: true) => "/Users/me"
      driven, Linux, a throwaway HOME => .local/share/valafind 0700 holding settings.json 0600, instance.lock 0600,
        logs 0700 with valafind-20261125.log 0600, and install.json 0644
      driven, the run script's data folder => index.db, index.db-wal and index.db-shm 0600
    Deleting index.db makes Valafind build the index again; --uninstall leaves the data folder as it is.
    Valafind never writes to the user's files; the only data that leaves the computer is the update check (AutoUpdate).
  how
    decide follow each platform's own place for per-user app data
      because "unknown, confirm with the owner"
    decide owner-only folders and files on macOS and Linux
      because the index holds excerpts of the user's files (README, "Where your data lives"; PR #1)
    decide install.json is written without the owner-only mode
      because "unknown, confirm with the owner: keep this? it sits inside the private folder, so others cannot read it"
    decide VALAFIND_DATA_DIR moves everything at once
      because tests and portable copies need their own folder (AppPaths)
  sources
    README.md, "Where your data lives"; PR #1 ("private data is owner-only (0700/0600)")
    src/Valafind.Core/Configuration/AppPaths.cs; src/Valafind.App/Updates/SelfInstaller.cs, WriteInstalledVersion
    driven on Linux 2026-11-25: the folders and modes in .ail/app/data and in the throwaway home of .ail/app-install
```
