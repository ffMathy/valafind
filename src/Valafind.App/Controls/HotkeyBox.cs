using System.Globalization;
using Avalonia.Controls;
using Avalonia.Input;

namespace Valafind.App.Controls;

/// <summary>A read-only text box that records the key chord pressed while it has focus ("Ctrl+Alt+Space").</summary>
public sealed class HotkeyBox : TextBox
{
    /// <summary>Creates the box.</summary>
    public HotkeyBox()
    {
        IsReadOnly = true;
        IsUndoEnabled = false;
    }

    /// <inheritdoc />
    protected override Type StyleKeyOverride => typeof(TextBox);

    /// <inheritdoc />
    protected override void OnKeyDown(KeyEventArgs e)
    {
        // Let plain Tab / Shift+Tab move focus.
        if (e.Key == Key.Tab && (e.KeyModifiers & ~KeyModifiers.Shift) == KeyModifiers.None)
        {
            base.OnKeyDown(e);
            return;
        }
        var chord = FormatChord(e.KeyModifiers, e.Key, OperatingSystem.IsMacOS());
        if (chord is not null)
        {
            Text = chord;
            CaretIndex = chord.Length;
        }
        e.Handled = true;
    }

    /// <summary>Builds "Ctrl+Alt+Shift+Super+Key"; null for modifier-only presses or unsupported keys.
    /// A chord must contain at least one modifier.</summary>
    internal static string? FormatChord(KeyModifiers modifiers, Key key, bool mac)
    {
        var name = KeyName(key);
        if (name is null || modifiers == KeyModifiers.None)
            return null;
        var parts = new List<string>(5);
        if (modifiers.HasFlag(KeyModifiers.Control))
            parts.Add("Ctrl");
        if (modifiers.HasFlag(KeyModifiers.Alt))
            parts.Add("Alt");
        if (modifiers.HasFlag(KeyModifiers.Shift))
            parts.Add("Shift");
        if (modifiers.HasFlag(KeyModifiers.Meta))
            parts.Add(mac ? "Cmd" : "Super");
        parts.Add(name);
        return string.Join('+', parts);
    }

    private static string? KeyName(Key key) => key switch
    {
        >= Key.A and <= Key.Z => key.ToString(),
        >= Key.D0 and <= Key.D9 => ((int)(key - Key.D0)).ToString(CultureInfo.InvariantCulture),
        >= Key.F1 and <= Key.F24 => key.ToString(),
        Key.Space => "Space",
        Key.Enter => "Enter",
        Key.Escape => "Escape",
        Key.Tab => "Tab",
        Key.Back => "Backspace",
        Key.Delete => "Delete",
        Key.Insert => "Insert",
        Key.Home => "Home",
        Key.End => "End",
        Key.PageUp => "PageUp",
        Key.PageDown => "PageDown",
        Key.Up => "Up",
        Key.Down => "Down",
        Key.Left => "Left",
        Key.Right => "Right",
        Key.OemPeriod => "Period",
        Key.OemComma => "Comma",
        Key.OemMinus => "Minus",
        Key.OemPlus => "Plus",
        Key.OemTilde => "Backquote",
        Key.OemQuestion => "Slash",
        Key.OemSemicolon => "Semicolon",
        _ => null,
    };
}
