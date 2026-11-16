namespace Valafind.App.ViewModels;

/// <summary>
/// Builds the "install the model" command shown to the user. The fetch scripts ship next to the executable
/// (<c>&lt;app&gt;/tools/fetch-model.sh|ps1</c>, see Valafind.App.csproj), so the command uses their absolute path and
/// works from any terminal, whatever the current directory.
/// </summary>
public static class ModelInstallHelp
{
    /// <summary>Folder (below the app folder) the fetch scripts are shipped in.</summary>
    public const string ToolsFolder = "tools";

    /// <summary>Absolute path of the shipped fetch script for the platform.</summary>
    /// <param name="windows">PowerShell script (Windows) instead of the POSIX shell script.</param>
    /// <param name="appDirectory">Folder of the executable (<see cref="AppContext.BaseDirectory"/>).</param>
    public static string ScriptPath(bool windows, string appDirectory)
    {
        ArgumentNullException.ThrowIfNull(appDirectory);
        var name = windows ? "fetch-model.ps1" : "fetch-model.sh";
        // Join by hand: the result is shown for the target OS, which in tests may differ from the OS running them.
        var sep = windows ? '\\' : '/';
        var dir = appDirectory.TrimEnd('/', '\\');
        return dir + sep + ToolsFolder + sep + name;
    }

    /// <summary>
    /// The full command line, e.g. <c>bash '/opt/Valafind/tools/fetch-model.sh'</c> or
    /// <c>powershell -ExecutionPolicy Bypass -File "C:\Program Files\Valafind\tools\fetch-model.ps1"</c>.
    /// </summary>
    /// <param name="windows">Build the PowerShell command.</param>
    /// <param name="appDirectory">Folder of the executable.</param>
    /// <param name="target">Explicit target folder, or null for the script's default (the folder the app looks in).</param>
    /// <param name="force">Re-download files that are already present (repairs a broken install).</param>
    public static string FetchCommand(bool windows, string appDirectory, string? target, bool force = false)
    {
        var script = ScriptPath(windows, appDirectory);
        if (windows)
        {
            var command = "powershell -ExecutionPolicy Bypass -File " + PowerShellQuote(script);
            if (target is not null)
                command += " " + PowerShellQuote(target);
            return force ? command + " -Force" : command;
        }
        // Through bash: the script needs bash, and the executable bit does not always survive copying/unzipping.
        var sh = "bash " + PosixQuote(script);
        if (target is not null)
            sh += " " + PosixQuote(target);
        return force ? sh + " --force" : sh;
    }

    /// <summary>Single-quotes for POSIX shells: nothing inside is expanded; embedded quotes become <c>'\''</c>.</summary>
    internal static string PosixQuote(string value) => "'" + value.Replace("'", "'\\''", StringComparison.Ordinal) + "'";

    /// <summary>Double-quotes a path for powershell.exe -File (paths cannot contain double quotes on Windows).</summary>
    internal static string PowerShellQuote(string value) => "\"" + value + "\"";
}
