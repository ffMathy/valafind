using System.Text;
using System.Text.Unicode;

namespace Valafind.Core.Extraction;

/// <summary>
/// Cheap heuristics for deciding whether a file with an unknown extension holds human-readable text, and
/// which encoding it uses.
/// </summary>
public static class TextSniffer
{
    /// <summary>Recommended number of leading bytes to pass to <see cref="LooksLikeText"/>.</summary>
    public const int RecommendedHeadSize = 8 * 1024;

    /// <summary>
    /// Returns true when <paramref name="head"/> (the first few KB of a file) looks like text: it carries a
    /// Unicode byte-order mark, or looks like BOM-less UTF-16, or contains no NUL bytes and only a small share
    /// of non-whitespace control characters. Empty input is not considered text.
    /// </summary>
    public static bool LooksLikeText(ReadOnlySpan<byte> head)
    {
        if (head.IsEmpty)
        {
            return false;
        }

        var encoding = DetectBom(head, out var bomLength);
        if (encoding is not null)
        {
            if (encoding is UTF8Encoding)
            {
                return ControlRatioOk(head[bomLength..]);
            }

            // UTF-16/32 with BOM: decode a slice and check for control characters.
            return DecodedLooksLikeText(encoding, head[bomLength..]);
        }

        if (GuessBomlessUtf16(head) is { } utf16)
        {
            return DecodedLooksLikeText(utf16, head);
        }

        if (head.Contains((byte)0))
        {
            return false;
        }

        if (!ControlRatioOk(head))
        {
            return false;
        }

        // Valid UTF-8 (allowing a sequence truncated at the end of the sample) is text. Otherwise accept a
        // legacy 8-bit encoding unless high bytes dominate, which suggests compressed or encrypted data.
        if (IsValidUtf8(head, allowTruncatedTail: true))
        {
            return true;
        }

        var high = 0;
        foreach (var b in head)
        {
            if (b >= 0x80)
            {
                high++;
            }
        }

        return high <= head.Length * 0.3;
    }

    /// <summary>Detects a UTF-8/16/32 byte-order mark.</summary>
    internal static Encoding? DetectBom(ReadOnlySpan<byte> head, out int bomLength)
    {
        if (head.StartsWith((ReadOnlySpan<byte>)[0xEF, 0xBB, 0xBF]))
        {
            bomLength = 3;
            return new UTF8Encoding(false);
        }

        if (head.StartsWith((ReadOnlySpan<byte>)[0xFF, 0xFE, 0x00, 0x00]))
        {
            bomLength = 4;
            return new UTF32Encoding(bigEndian: false, byteOrderMark: false);
        }

        if (head.StartsWith((ReadOnlySpan<byte>)[0x00, 0x00, 0xFE, 0xFF]))
        {
            bomLength = 4;
            return new UTF32Encoding(bigEndian: true, byteOrderMark: false);
        }

        if (head.StartsWith((ReadOnlySpan<byte>)[0xFF, 0xFE]))
        {
            bomLength = 2;
            return new UnicodeEncoding(bigEndian: false, byteOrderMark: false);
        }

        if (head.StartsWith((ReadOnlySpan<byte>)[0xFE, 0xFF]))
        {
            bomLength = 2;
            return new UnicodeEncoding(bigEndian: true, byteOrderMark: false);
        }

        bomLength = 0;
        return null;
    }

    /// <summary>
    /// Detects BOM-less UTF-16 (common for files written by Windows tools) by the characteristic pattern of
    /// NUL bytes in every other position for mostly-ASCII text.
    /// </summary>
    internal static Encoding? GuessBomlessUtf16(ReadOnlySpan<byte> head)
    {
        var pairs = head.Length / 2;
        if (pairs < 2)
        {
            return null;
        }

        int evenNul = 0, oddNul = 0;
        for (var i = 0; i + 1 < head.Length; i += 2)
        {
            if (head[i] == 0)
            {
                evenNul++;
            }

            if (head[i + 1] == 0)
            {
                oddNul++;
            }
        }

        if (oddNul >= pairs * 0.6 && evenNul <= pairs * 0.05)
        {
            return new UnicodeEncoding(bigEndian: false, byteOrderMark: false);
        }

        if (evenNul >= pairs * 0.6 && oddNul <= pairs * 0.05)
        {
            return new UnicodeEncoding(bigEndian: true, byteOrderMark: false);
        }

        return null;
    }

    /// <summary>
    /// Chooses an encoding for <paramref name="head"/>: BOM, then BOM-less UTF-16, then UTF-8 if valid,
    /// else Windows-1252 (a superset of printable Latin-1).
    /// </summary>
    internal static Encoding DetectEncoding(ReadOnlySpan<byte> head, bool headIsWholeFile, out int bomLength)
    {
        if (DetectBom(head, out bomLength) is { } bom)
        {
            return bom;
        }

        if (GuessBomlessUtf16(head) is { } utf16)
        {
            return utf16;
        }

        return IsValidUtf8(head, allowTruncatedTail: !headIsWholeFile)
            ? new UTF8Encoding(false)
            : ExtractionIO.Windows1252;
    }

    internal static bool IsValidUtf8(ReadOnlySpan<byte> bytes, bool allowTruncatedTail)
    {
        if (Utf8.IsValid(bytes))
        {
            return true;
        }

        if (!allowTruncatedTail)
        {
            return false;
        }

        // Drop an incomplete trailing sequence (at most 3 bytes) cut off by the sample boundary.
        for (var drop = 1; drop <= 3 && drop < bytes.Length; drop++)
        {
            var lead = bytes[^drop];
            if ((lead & 0xC0) == 0xC0)
            {
                return Utf8.IsValid(bytes[..^drop]);
            }

            if ((lead & 0xC0) != 0x80)
            {
                break;
            }
        }

        return false;
    }

    private static bool ControlRatioOk(ReadOnlySpan<byte> bytes)
    {
        if (bytes.IsEmpty)
        {
            return true;
        }

        var control = 0;
        foreach (var b in bytes)
        {
            if (b == 0)
            {
                return false;
            }

            if (IsSuspiciousControl(b))
            {
                control++;
            }
        }

        return control <= bytes.Length * 0.02;
    }

    private static bool DecodedLooksLikeText(Encoding encoding, ReadOnlySpan<byte> bytes)
    {
        // Trim to whole code units so the decoder does not see a partial character.
        var unit = encoding is UTF32Encoding ? 4 : 2;
        bytes = bytes[..(bytes.Length - bytes.Length % unit)];
        if (bytes.IsEmpty)
        {
            return true;
        }

        var text = encoding.GetString(bytes);
        var control = 0;
        foreach (var c in text)
        {
            if (c == '\0')
            {
                return false;
            }

            if (c < 0x80 && IsSuspiciousControl((byte)c))
            {
                control++;
            }
        }

        return control <= text.Length * 0.02;
    }

    // Control characters that do not normally occur in text. TAB, LF, VT, FF, CR and ESC (ANSI colour codes
    // in logs) are allowed; so is SUB (0x1A), the legacy DOS end-of-file marker.
    private static bool IsSuspiciousControl(byte b) =>
        b == 0x7F || (b < 0x20 && b is not (0x09 or 0x0A or 0x0B or 0x0C or 0x0D or 0x1B or 0x1A));
}
