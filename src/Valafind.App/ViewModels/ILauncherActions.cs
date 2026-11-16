namespace Valafind.App.ViewModels;

/// <summary>Side effects the launcher asks its host to perform (shell, clipboard, windows, indexing control).</summary>
public interface ILauncherActions
{
    /// <summary>Opens the file with its default application.</summary>
    void Open(string path);

    /// <summary>Shows the file in Explorer / Finder / the file manager.</summary>
    void Reveal(string path);

    /// <summary>Quick Look preview (macOS only; ignored elsewhere).</summary>
    void QuickLook(string path);

    /// <summary>Puts text on the clipboard.</summary>
    Task CopyTextAsync(string text);

    /// <summary>Hides the launcher window.</summary>
    void HideLauncher();

    /// <summary>Opens the settings window.</summary>
    void ShowSettings();

    /// <summary>Quits the application.</summary>
    void Quit();

    /// <summary>Pauses or resumes indexing at the user's request (persisted).</summary>
    void SetUserPaused(bool paused);

    /// <summary>"Index now": ignore system-pressure throttling for a while.</summary>
    void ResumeNow();
}
