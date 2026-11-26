# Online-only cloud files

Described from the code only: Windows and macOS, which could not be driven here. On Linux sync clients keep full copies,
so no file is online-only.

```ail
node CloudFiles : fn   level: system
  why   OneDrive Files On-Demand leaves most of a OneDrive folder online-only, so skipping those files left most of it
        out of the index; but indexing must never fill the disk, wait on the cloud, or change the user's files.   <- Indexing
  what
    type CloudFilesSupport = { isOnlineOnly(path: text, attributes: integer) -> boolean, canDownload(path: text, size: integer, isMedia: boolean) -> boolean, requestDownload(path: text) -> CloudMark?, release(path: text, mark: CloudMark, requestedAt: time) -> boolean }
    MAX_REQUESTED_FILES: integer = 1000
    BUDGET_FRACTION: number = 0.10 — of the drive's free space, counting what requested files already take
    STALL_TIMEOUT: duration = 3 min
    MAX_DOCUMENT_BYTES: integer = 67108864
    MAX_MEDIA_BYTES: integer = 1073741824
    FREE_SPACE_RESERVE: integer = 2147483648
    MAX_RELEASE_ATTEMPTS: integer = 8 — retried 1 s, 2 s, 4 s, … apart, about two minutes in all
    RELEASE_CALL_TIMEOUT: duration = 1 min
    MAX_HUNG_RELEASES: integer = 3
    withinSizeLimit(size: integer, isMedia: boolean) -> boolean
    examples
      withinSizeLimit(67108864, false) => true
      withinSizeLimit(67108865, false) => false
      withinSizeLimit(1073741824, true) => true
      withinSizeLimit(0, false) => false
    Online-only: on Windows the "offline" or "recall on data access" attribute; on macOS a dataless file.
    A pass hands every online-only file to its own lane and goes on; the files on the device never wait for a download.
      The lane requests files ahead (Windows: "Always keep on this device"; macOS: start downloading), within the limits,
      gives each arrived file to a worker, which indexes it, and then gives it back on a release thread. Cloud work takes
      at most half of the workers' free places. Within the lane documents come before media.
    A file too large (documents over 64 MB, media over 1 GB), one that would leave under 2 GB free, or one whose download
      failed is indexed by name and marked to be read later, once it is on the device or can be downloaded.
    When nothing arrives for 3 minutes, the waiting requests are undone and those files are read one at a time on the lane.
    Every request is recorded in the index database before the file is touched and removed once given back; at the next
      start, files that arrived meanwhile are indexed and every recorded request is undone. If the record cannot be read,
      no file is requested ahead for that run.
    Giving back on Windows: mark the file unpinned so the sync client frees it once uploaded, then restore its original
      pin. A file written to since, never arrived, or in a pinned folder keeps its original setting; a pin the user
      changed is left alone. On macOS a file is evicted only when uploaded and unchanged since; a provider that does not
      report upload state is never downloaded from.
    Three give-back calls that hang stop cloud downloads for the run (names are still indexed).
    A walk counts as complete only when the lane is empty, and the panel says indexing, not "Up to date", until then.
    Uninstalling gives back every recorded request once the running copy has quit.
  how
    decide download, index, and make online-only again
      because the files must be searchable without staying on the disk (PR #18)
    decide a separate lane with at most half the free workers
      because waiting for the cloud held back, and later stalled, the files already on the device (PRs #26, #34)
    decide never drop a file's data directly; unpin and let the sync client free it
      because dehydrating from Valafind's side deadlocked with OneDrive for hours (PR #36)
    decide the 1,000-file and 10 % limits
      because "unknown, confirm with the owner: why these numbers"
  sources
    README.md, "What gets indexed" (online-only cloud files)
    GitHub PRs #18, #26, #34, #36, #40
    src/Valafind.Core/Indexing/CloudPrefetcher.cs, src/Valafind.Core/Indexing/IndexingCoordinator.CloudLane.cs, src/Valafind.Core/Platform/CloudFiles.cs, src/Valafind.Core/Platform/MacCloudFiles.cs, src/Valafind.Core/Storage/CloudDownloadCleanup.cs
    not driven: Windows and macOS only
```
