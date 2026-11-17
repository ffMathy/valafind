using System.Text;
using Valafind.App.Localization;

namespace Valafind.App.Platform;

/// <summary>Operating-system flavour used to render a chord for humans.</summary>
internal enum ChordDisplayStyle
{
    Windows,
    MacOS,
    Linux,
}

/// <summary>
/// A parsed global-hotkey chord: a set of modifiers plus exactly one key. <see cref="Key"/> is always one of
/// the canonical key names (see <see cref="SupportedKeys"/>), e.g. "Space", "K", "7", "F5", "Backquote".
/// </summary>
internal sealed record HotkeyChord(bool Ctrl, bool Alt, bool Shift, bool Super, string Key)
{
    private static readonly string[] NamedKeys =
        ["Space", "Enter", "Tab", "Backquote", "Comma", "Period", "Slash", "Semicolon"];

    /// <summary>All canonical key names accepted in a chord.</summary>
    internal static IReadOnlyList<string> SupportedKeys { get; } = BuildSupportedKeys();

    /// <summary>True when at least one of Ctrl/Alt/Super is held (Shift alone does not make a chord safe).</summary>
    internal bool HasNonShiftModifier => Ctrl || Alt || Super;

    internal static bool IsFunctionKey(string key) =>
        key.Length is 2 or 3 && key[0] == 'F' && int.TryParse(key.AsSpan(1), out var n) && n is >= 1 and <= 24;

    /// <summary>
    /// Parses text such as "Ctrl+Alt+Space", "Cmd+Shift+K", "Option+Space" or "Win+F".
    /// Modifier aliases: Ctrl/Control; Alt/Option/Opt; Shift; Super/Win/Windows/Cmd/Command/Meta/Logo.
    /// Parsing is case-insensitive and tolerant of whitespace around '+'.
    /// </summary>
    public static bool TryParse(string text, out HotkeyChord chord, out string? error)
    {
        chord = null!;
        error = null;
        if (string.IsNullOrWhiteSpace(text))
        {
            error = Strings.HotkeyEmpty;
            return false;
        }

        var parts = text.Split('+', StringSplitOptions.TrimEntries);
        bool ctrl = false, alt = false, shift = false, super = false;
        string? key = null;

        for (var i = 0; i < parts.Length; i++)
        {
            var part = parts[i];
            if (part.Length == 0)
            {
                error = Loc.Format(Strings.HotkeyEmptyPart, text);
                return false;
            }

            var isLast = i == parts.Length - 1;
            var modifier = ParseModifier(part);
            if (modifier is not null && !isLast)
            {
                ref var flag = ref ctrl;
                switch (modifier)
                {
                    case "Ctrl": flag = ref ctrl; break;
                    case "Alt": flag = ref alt; break;
                    case "Shift": flag = ref shift; break;
                    default: flag = ref super; break;
                }

                if (flag)
                {
                    error = Loc.Format(Strings.HotkeyDuplicateModifier, modifier, text);
                    return false;
                }

                flag = true;
                continue;
            }

            if (!isLast)
            {
                error = Loc.Format(Strings.HotkeyNotModifier, part);
                return false;
            }

            if (modifier is not null)
            {
                error = Loc.Format(Strings.HotkeyNoKeyAfterModifiers, text);
                return false;
            }

            if (IsEscape(part))
            {
                error = Strings.HotkeyEscape;
                return false;
            }

            key = NormalizeKey(part);
            if (key is null)
            {
                error = Loc.Format(Strings.HotkeyUnsupportedKey, part);
                return false;
            }
        }

        if (key is null)
        {
            error = Loc.Format(Strings.HotkeyNoKey, text);
            return false;
        }

        if (!(ctrl || alt || super) && !IsFunctionKey(key))
        {
            error = Strings.HotkeyNeedsModifier;
            return false;
        }

        chord = new HotkeyChord(ctrl, alt, shift, super, key);
        return true;
    }

    /// <summary>
    /// macOS 15 and later refuse global hotkeys whose only modifiers are Option (⌥) or Option+Shift (⌥⇧) (a guard
    /// against key loggers). Returns the explanation for such a chord, otherwise null.
    /// </summary>
    internal static string? MacRestriction(HotkeyChord chord)
    {
        ArgumentNullException.ThrowIfNull(chord);
        if (chord.Alt && !chord.Ctrl && !chord.Super)
        {
            return Loc.Format(chord.Shift ? Strings.HotkeyMacOptionShiftOnly : Strings.HotkeyMacOptionOnly,
                chord.DisplayString(ChordDisplayStyle.MacOS));
        }
        return null;
    }

    /// <summary>Canonical, OS-neutral form: "Ctrl+Alt+Shift+Super+Key" (absent modifiers omitted).</summary>
    public override string ToString()
    {
        var sb = new StringBuilder(24);
        if (Ctrl) sb.Append("Ctrl+");
        if (Alt) sb.Append("Alt+");
        if (Shift) sb.Append("Shift+");
        if (Super) sb.Append("Super+");
        sb.Append(Key);
        return sb.ToString();
    }

    /// <summary>Human-readable form for the current OS (macOS: "⌥Space", "⇧⌘K"; elsewhere "Ctrl+Alt+Space").</summary>
    public string DisplayString() => DisplayString(
        OperatingSystem.IsMacOS() ? ChordDisplayStyle.MacOS :
        OperatingSystem.IsWindows() ? ChordDisplayStyle.Windows :
        ChordDisplayStyle.Linux);

    internal string DisplayString(ChordDisplayStyle style)
    {
        if (style == ChordDisplayStyle.MacOS)
        {
            // Apple's canonical modifier order: Control, Option, Shift, Command.
            var sb = new StringBuilder(8);
            if (Ctrl) sb.Append('⌃');
            if (Alt) sb.Append('⌥');
            if (Shift) sb.Append('⇧');
            if (Super) sb.Append('⌘');
            sb.Append(Key switch
            {
                "Enter" => "↩",
                "Tab" => "⇥",
                "Backquote" => "`",
                "Comma" => ",",
                "Period" => ".",
                "Slash" => "/",
                "Semicolon" => ";",
                _ => Key,
            });
            return sb.ToString();
        }

        var text = new StringBuilder(24);
        if (Ctrl) text.Append("Ctrl+");
        if (Alt) text.Append("Alt+");
        if (Shift) text.Append("Shift+");
        if (Super) text.Append(style == ChordDisplayStyle.Windows ? "Win+" : "Super+");
        text.Append(Key switch
        {
            "Backquote" => "`",
            "Comma" => ",",
            "Period" => ".",
            "Slash" => "/",
            "Semicolon" => ";",
            _ => Key,
        });
        return text.ToString();
    }

    private static string? ParseModifier(string part) => part.ToUpperInvariant() switch
    {
        "CTRL" or "CONTROL" or "CTL" or "⌃" => "Ctrl",
        "ALT" or "OPTION" or "OPT" or "⌥" => "Alt",
        "SHIFT" or "⇧" => "Shift",
        "SUPER" or "WIN" or "WINDOWS" or "CMD" or "COMMAND" or "META" or "LOGO" or "⌘" => "Super",
        _ => null,
    };

    private static bool IsEscape(string part) =>
        part.Equals("Escape", StringComparison.OrdinalIgnoreCase) || part.Equals("Esc", StringComparison.OrdinalIgnoreCase);

    private static string? NormalizeKey(string part)
    {
        if (part.Length == 1)
        {
            var c = part[0];
            if (c is >= 'a' and <= 'z') return ((char)(c - 32)).ToString();
            if (c is >= 'A' and <= 'Z' or >= '0' and <= '9') return part;
            return c switch
            {
                ' ' => "Space",
                '`' => "Backquote",
                ',' => "Comma",
                '.' => "Period",
                '/' => "Slash",
                ';' => "Semicolon",
                _ => null,
            };
        }

        if (part[0] is 'f' or 'F' && part.Length <= 3 && int.TryParse(part.AsSpan(1), System.Globalization.NumberStyles.None, null, out var n))
            return n is >= 1 and <= 24 ? "F" + n.ToString(System.Globalization.CultureInfo.InvariantCulture) : null;

        return part.ToUpperInvariant() switch
        {
            "SPACE" or "SPACEBAR" => "Space",
            "ENTER" or "RETURN" => "Enter",
            "TAB" => "Tab",
            "BACKQUOTE" or "GRAVE" or "BACKTICK" or "OEM3" => "Backquote",
            "COMMA" => "Comma",
            "PERIOD" or "DOT" => "Period",
            "SLASH" => "Slash",
            "SEMICOLON" => "Semicolon",
            _ => null,
        };
    }

    private static string[] BuildSupportedKeys()
    {
        var keys = new List<string>(NamedKeys.Length + 26 + 10 + 24);
        keys.AddRange(NamedKeys);
        for (var c = 'A'; c <= 'Z'; c++) keys.Add(c.ToString());
        for (var c = '0'; c <= '9'; c++) keys.Add(c.ToString());
        for (var i = 1; i <= 24; i++) keys.Add("F" + i.ToString(System.Globalization.CultureInfo.InvariantCulture));
        return keys.ToArray();
    }
}
