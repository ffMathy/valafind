# Updating itself

Described from the code and PR #6; no real release was downloaded, and every driven run had VALAFIND_NO_UPDATE=1.

```ail
node AutoUpdate : fn   level: system
  why   Most people never look for a new version; fixes only reach them if Valafind fetches them itself, safely, and
        restarts into them without interrupting a search.   <- AppShell
  what
    type UpdateState = "Idle" | "Unsupported" | "Disabled" | "Checking" | "UpToDate" | "Downloading" | "ReadyToRestart" | "Failed"
    type Updater = { state: UpdateState, isSupported: boolean, pendingVersion: text or nothing, statusText: text, checkNow() -> later boolean, restart(arguments: list of text) -> nothing }
    FIRST_CHECK: duration = 1 min
    CHECK_INTERVAL: duration = 6 h
    the schedule: FIRST_CHECK after start, then every CHECK_INTERVAL; each time, a check when "Install new versions
      automatically" is on, else the state Disabled; a check while one runs does nothing; after ReadyToRestart, no more
    no updater at all in a Microsoft Store or Mac App Store copy: the store updates it; its settings then read
      "Valafind checks GitHub for new versions" and "Check for updates" opens the releases page
    unsupported, checked in this order (the reason is the status under the switch):
      VALAFIND_NO_UPDATE=1 => "Updates are turned off (VALAFIND_NO_UPDATE)"
      a store copy => "This copy cannot update itself"
      not a single-file build => "Only release builds update themselves"
      the folder of the file (or the .app) is not writable => "Valafind cannot write to {folder}; update it the way it was installed"
    a check:
      GET https://api.github.com/repos/ffMathy/valafind/releases/latest with Accept application/vnd.github+json,
        X-GitHub-Api-Version 2022-11-28 and User-Agent "Valafind/<version>", within 30 s; 404 => up to date
      drafts and tags that are not versions count as none; "latest" never returns a prerelease
      the release's version ≤ the running one => UpToDate
      else the asset "valafind-<rid>" ("valafind-win-x64.exe", "valafind-linux-arm64", "valafind-osx-arm64"), or
        "valafind-<rid>.app.zip" when running from a bundle; none => "Version {v} has no build for {rid} ({asset})"
      the expected SHA-256: the asset's GitHub digest "sha256:<64 hex>", else its line in SHA256SUMS.txt; neither =>
        "Version {v} publishes no checksum for {asset}", nothing downloaded
      download next to the running file under a temporary name (1 hour limit), hashing on the way; a short download or
        another hash => Failed, file deleted; then made executable (a bundle unzipped with ditto) and moved over the
        running file, under the install lock; install.json gets the new version when this is the installed copy
      then ReadyToRestart: the tray item and the settings button read "Restart to update"
    restarting: when neither the launcher nor the settings window is open, quit, then start the new file with
      --replaces <this process> and --background (or --show when the user opened Valafind meanwhile); otherwise try
      again every 15 s. "Restart to update" restarts at once and shows the launcher.
    the status under the switch:
      off (Idle, UpToDate or Disabled) => "Automatic updates are off";  Idle => "Updates are checked automatically"
      Checking => "Checking for updates…";  UpToDate => "Up to date (checked 14:05)" (the time in the user's format)
      Downloading => "Downloading version 1.0.94…";  ReadyToRestart => "Version 1.0.94 is installed and starts the next time Valafind does"
      Failed => "Could not update: {reason}", "GitHub did not answer in time" after 30 s
    examples
      sha256FromDigest("sha256:" + 64 hex digits in lower case) => the same digits in upper case
      sha256FromDigest("sha512:abc") => nothing;  sha256FromDigest("sha256:abc") => nothing
      findChecksum("<64 hex>  valafind-linux-x64\n", "valafind-linux-x64") => the hex in upper case;  "<hex> *name" matches too
      runtimeIdentifier("Windows", "Arm64") => "win-arm64";  runtimeIdentifier("MacOS", "X64") => "osx-x64"
      driven, VALAFIND_NO_UPDATE=1 => the status "Updates are turned off (VALAFIND_NO_UPDATE)", the switch still on
      driven, a build folder => "Check for updates" opens https://github.com/ffMathy/valafind/releases
  how
    decide verify every download against a published SHA-256 and refuse a release without one
      because "unknown, confirm with the owner: PR #6 states the rule, not the threat it guards against"
    decide restart only when the launcher and the settings window are closed
      because "unknown, confirm with the owner: PR #6 says once the launcher and Settings are closed, not why"
    decide check a minute after start and every 6 hours
      because the first check must not compete with starting up (AppHost); "unknown, confirm with the owner: why 6 hours"
    decide store copies never update from GitHub
      because store policies forbid it (AppHost.StartUpdater, PR #19)
    decide a store copy's settings still say it checks GitHub
      because "unknown, confirm with the owner: keep this? the store copy has no updater, so the text is not true"
  sources
    PR #6; PR #8 (no longer "no network requests"); PR #19 (stores); PR #30 ("Restart to update"); PR #36 (install lock)
    src/Valafind.App/Updates/Updater.cs, GitHubReleases.cs, FileSwap.cs, InstallLock.cs
    src/Valafind.App/Services/AppHost.cs, StartUpdater, CheckForUpdates, RestartWhenIdle, RestartToUpdate
    not driven: no real release was downloaded; VALAFIND_NO_UPDATE=1 in every run
```
