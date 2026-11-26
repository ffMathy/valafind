# The command that installs the model

The exact command line the missing-model screen and the settings show, built from where the app runs.

```ail
node ModelInstallHelp : fn   level: system
  why   A command copied into a terminal must work from any folder, with any path, the first time; so it names the
        shipped script by its full path and quotes it for the shell that runs it.   <- ModelSetup
  what
    TOOLS_FOLDER: text = "tools"
    scriptPath(windows: boolean, appFolder: text) -> text — appFolder without trailing slashes, then the tools folder,
      then "fetch-model.ps1" (windows) or "fetch-model.sh", joined by "\" (windows) or "/"
    fetchCommand(windows: boolean, appFolder: text, target: text or nothing, force: boolean = false) -> text
      windows => "powershell -ExecutionPolicy Bypass -File " + the script in double quotes, then the target in double
        quotes when there is one, then " -Force" when force
      else => "bash " + the script in single quotes, then the target in single quotes, then " --force"
    posixQuote(value: text) -> text — in single quotes, each ' written as '\''
    where each command is shown:
      the launcher (missing or failed model): windows is the Windows design (VALAFIND_UI), not the system; the target
        is the model folder only when VALAFIND_DATA_DIR is set, since the script's default is the usual data folder
      the settings' model row: windows is the system; never a target
      "Download them again with:" (a model that failed to load): the same with force
    the scripts ship in the tools folder next to the executable only in builds without the model; the installer
      copies the executable alone (SelfInstall)
    the model folder the scripts fill: <data folder>/models/embeddinggemma-2 (ModelFolder)
    looking for the model while it is missing: every 5 s, and on opening the launcher, on "Check again" and on Save;
      a found folder whose signature (its path, the model file, and the size and time of the model, its _data file and
      the tokenizer) differs from the one that failed => load it
    examples
      fetchCommand(false, "/opt/Valafind/", nothing) => "bash '/opt/Valafind/tools/fetch-model.sh'"
      fetchCommand(false, "/opt/Valafind", "/data/models/embeddinggemma-2", true) => "bash '/opt/Valafind/tools/fetch-model.sh' '/data/models/embeddinggemma-2' --force"
      fetchCommand(true, "C:\Program Files\Valafind", nothing) => "powershell -ExecutionPolicy Bypass -File \"C:\Program Files\Valafind\tools\fetch-model.ps1\""
      posixQuote("it's") => "'it'\\''s'"
      driven, Linux design, a build folder, VALAFIND_DATA_DIR set => "bash '/home/user/valafind/src/Valafind.App/bin/Release/net10.0/tools/fetch-model.sh' '/home/user/valafind/.ail/app-nomodel/data/models/embeddinggemma-2'"
      driven, Windows design on Linux => "powershell -ExecutionPolicy Bypass -File \"/home/user/valafind/src/Valafind.App/bin/Release/net10.0\tools\fetch-model.ps1\" \"…/data/models/embeddinggemma-2\""
      driven, installed 9.1.0 without VALAFIND_DATA_DIR => "bash '/…/home/.local/bin/tools/fetch-model.sh'", a script that is not there
  how
    decide run the script through bash rather than directly
      because the script needs bash, and the executable bit does not always survive copying or unzipping (ModelInstallHelp)
    decide the full path of the script, quoted
      because the command must work from any terminal, whatever its current folder
    decide the launcher follows the design and the settings follow the system
      because "unknown, confirm with the owner: keep this? VALAFIND_UI=windows on Linux shows a PowerShell command"
    decide the settings never pass the target folder
      because "unknown, confirm with the owner: keep this? with VALAFIND_DATA_DIR set it installs where the app does not look"
  sources
    PR #1 (the exact command, detection without a restart); PR #31 (target passed when VALAFIND_DATA_DIR is set)
    src/Valafind.App/ViewModels/ModelInstallHelp.cs; LauncherViewModel.cs, SetModelMissing; SettingsViewModel.cs, UpdateModelDetection
    src/Valafind.App/Services/CoreServicesFactory.cs, ModelSignature; src/Valafind.App/Services/ModelInstallWatcher.cs
    src/Valafind.App/Valafind.App.csproj (tools shipped only without ValafindModelDir); tools/fetch-model.sh
    driven on Linux 2026-11-25 on displays 241, 242 and 245 (screenshots)
```
