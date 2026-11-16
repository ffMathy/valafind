namespace Valafind.App.Platform;

/// <summary>
/// A system-wide keyboard shortcut that works while another application has focus.
/// Create one with <see cref="GlobalHotkeyFactory.Create"/>. At most one chord is active per service instance.
/// </summary>
/// <remarks>
/// <para>The <c>onPressed</c> callback is invoked on a thread-pool thread; marshal to the UI thread yourself
/// (e.g. <c>Dispatcher.UIThread.Post</c>). Key auto-repeat does not produce repeated callbacks where the
/// platform allows suppressing it.</para>
/// <para>On macOS, <see cref="TryRegister"/> and <see cref="Unregister"/> must run on the UI (main) thread;
/// calls from other threads are marshalled to it via Avalonia's dispatcher, which must be running.</para>
/// <para>If registering a new chord fails, the previously active chord (if any) stays registered.</para>
/// </remarks>
public interface IGlobalHotkeyService : IDisposable
{
    /// <summary>
    /// Registers <paramref name="chord"/> (e.g. "Ctrl+Alt+Space", "Cmd+Shift+K") replacing any previous chord.
    /// Returns false with a user-presentable <paramref name="error"/> when the text is invalid, the chord is taken
    /// by another application, or global hotkeys are unavailable (e.g. Wayland without the GlobalShortcuts portal).
    /// </summary>
#pragma warning disable CA1716 // 'error' is the agreed contract name; this interface is C#-only.
    bool TryRegister(string chord, Action onPressed, out string? error);
#pragma warning restore CA1716

    /// <summary>Removes the active chord, if any. Safe to call repeatedly.</summary>
    void Unregister();

    /// <summary>The canonical form of the active chord (e.g. "Ctrl+Alt+Space"), or null when none is registered.</summary>
    string? ActiveChord { get; }
}
