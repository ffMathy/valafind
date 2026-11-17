using System.Globalization;

namespace Valafind.App.Platform;

/// <summary>Maps canonical <see cref="HotkeyChord.Key"/> names to each platform's native key identifiers.</summary>
internal static class HotkeyKeyMaps
{
    /// <summary>Windows virtual-key code (VK_*), or null if unmapped.</summary>
    public static uint? WindowsVirtualKey(string key)
    {
        if (TryLetterOrDigit(key, out var c))
            return c; // VK_A..VK_Z == 'A'..'Z', VK_0..VK_9 == '0'..'9'
        if (TryFunctionKey(key, out var f))
            return (uint)(0x70 + f - 1); // VK_F1 = 0x70 .. VK_F24 = 0x87
        return key switch
        {
            "Space" => 0x20,
            "Enter" => 0x0D,
            "Tab" => 0x09,
            "Backquote" => 0xC0, // VK_OEM_3
            "Comma" => 0xBC,     // VK_OEM_COMMA
            "Period" => 0xBE,    // VK_OEM_PERIOD
            "Slash" => 0xBF,     // VK_OEM_2
            "Semicolon" => 0xBA, // VK_OEM_1
            _ => null,
        };
    }

    /// <summary>macOS virtual key code (kVK_*, ANSI layout positions), or null if unmapped (F21-F24).</summary>
    public static uint? MacVirtualKey(string key)
    {
        if (TryLetterOrDigit(key, out var c))
        {
            return c switch
            {
                'A' => 0x00, 'S' => 0x01, 'D' => 0x02, 'F' => 0x03, 'H' => 0x04, 'G' => 0x05, 'Z' => 0x06,
                'X' => 0x07, 'C' => 0x08, 'V' => 0x09, 'B' => 0x0B, 'Q' => 0x0C, 'W' => 0x0D, 'E' => 0x0E,
                'R' => 0x0F, 'Y' => 0x10, 'T' => 0x11, '1' => 0x12, '2' => 0x13, '3' => 0x14, '4' => 0x15,
                '6' => 0x16, '5' => 0x17, '9' => 0x19, '7' => 0x1A, '8' => 0x1C, '0' => 0x1D, 'O' => 0x1F,
                'U' => 0x20, 'I' => 0x22, 'P' => 0x23, 'L' => 0x25, 'J' => 0x26, 'K' => 0x28, 'N' => 0x2D,
                'M' => 0x2E,
                _ => null,
            };
        }

        if (TryFunctionKey(key, out var f))
        {
            return f switch
            {
                1 => 0x7A, 2 => 0x78, 3 => 0x63, 4 => 0x76, 5 => 0x60, 6 => 0x61, 7 => 0x62, 8 => 0x64,
                9 => 0x65, 10 => 0x6D, 11 => 0x67, 12 => 0x6F, 13 => 0x69, 14 => 0x6B, 15 => 0x71,
                16 => 0x6A, 17 => 0x40, 18 => 0x4F, 19 => 0x50, 20 => 0x5A,
                _ => null,
            };
        }

        return key switch
        {
            "Space" => 0x31,
            "Enter" => 0x24,
            "Tab" => 0x30,
            "Backquote" => 0x32,
            "Comma" => 0x2B,
            "Period" => 0x2F,
            "Slash" => 0x2C,
            "Semicolon" => 0x29,
            _ => null,
        };
    }

    /// <summary>
    /// X11/xkb keysym name (as accepted by XStringToKeysym and by the GlobalShortcuts portal trigger syntax),
    /// or null if unmapped.
    /// </summary>
    public static string? XKeysymName(string key)
    {
        if (TryLetterOrDigit(key, out var c))
            return char.ToLowerInvariant(c).ToString();
        if (TryFunctionKey(key, out var f))
            return "F" + f.ToString(CultureInfo.InvariantCulture);
        return key switch
        {
            "Space" => "space",
            "Enter" => "Return",
            "Tab" => "Tab",
            "Backquote" => "grave",
            "Comma" => "comma",
            "Period" => "period",
            "Slash" => "slash",
            "Semicolon" => "semicolon",
            _ => null,
        };
    }

    private static bool TryLetterOrDigit(string key, out char c)
    {
        c = key.Length == 1 ? key[0] : '\0';
        return c is >= 'A' and <= 'Z' or >= '0' and <= '9';
    }

    private static bool TryFunctionKey(string key, out int number)
    {
        number = 0;
        return HotkeyChord.IsFunctionKey(key) && int.TryParse(key.AsSpan(1), NumberStyles.None, CultureInfo.InvariantCulture, out number);
    }
}
