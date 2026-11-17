using System.Globalization;
using System.Text;
using Valafind.App.Platform;
using Valafind.Core.Abstractions;
using Valafind.App.Localization;

namespace Valafind.App.ViewModels;

/// <summary>Pure formatting helpers shared by the launcher views (dates, paths, numbers, hotkeys).</summary>
internal static class DisplayFormat
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    /// <summary>"Today 09:12", "Yesterday" or "12 Sep 2025" (in the given/local time zone).</summary>
    public static string RelativeDate(DateTimeOffset value, DateTimeOffset now, TimeZoneInfo? zone = null)
    {
        zone ??= TimeZoneInfo.Local;
        var local = TimeZoneInfo.ConvertTime(value, zone);
        var today = TimeZoneInfo.ConvertTime(now, zone).Date;
        if (local.Date == today)
            return Loc.Format(Strings.DateToday, local.ToString("HH:mm", Inv));
        if (local.Date == today.AddDays(-1))
            return Strings.DateYesterday;
        return local.ToString("d MMM yyyy", CultureInfo.CurrentCulture);
    }

    /// <summary>Thousands-separated integer, e.g. "182,930".</summary>
    public static string Count(long value) => value.ToString("N0", CultureInfo.CurrentCulture);

    /// <summary>Human file size: "412 KB", "3.4 MB".</summary>
    public static string FileSize(long bytes)
    {
        if (bytes < 1024)
            return bytes.ToString(Inv) + " B";
        string[] units = ["KB", "MB", "GB", "TB"];
        double v = bytes;
        var i = -1;
        do
        {
            v /= 1024;
            i++;
        }
        while (v >= 1024 && i < units.Length - 1);
        return (v >= 10 ? Math.Round(v).ToString("0", Inv) : v.ToString("0.#", Inv)) + " " + units[i];
    }

    /// <summary>Disk throughput, e.g. "92 MB/s", "0.4 MB/s".</summary>
    public static string Throughput(double bytesPerSecond)
    {
        var mb = Math.Max(0, bytesPerSecond) / (1024 * 1024);
        return (mb >= 10 ? Math.Round(mb).ToString("0", Inv) : mb.ToString("0.#", Inv)) + " MB/s";
    }

    /// <summary>Whole percentage, e.g. "41%".</summary>
    public static string Percent(double fraction) =>
        Math.Round(Math.Clamp(fraction, 0, 1) * 100, MidpointRounding.ToZero).ToString("0", Inv) + "%";

    /// <summary>Similarity score, e.g. "0.87".</summary>
    public static string Score(float score) => score.ToString("0.00", Inv);

    /// <summary>Replaces <paramref name="home"/> at the start of <paramref name="directory"/> with "~".</summary>
    public static string ShortenHome(string directory, string? home, StringComparison comparison)
    {
        if (string.IsNullOrEmpty(home) || string.IsNullOrEmpty(directory))
            return directory;
        var trimmedHome = home.TrimEnd('/', '\\');
        if (trimmedHome.Length == 0)
            return directory;
        if (string.Equals(directory.TrimEnd('/', '\\'), trimmedHome, comparison))
            return "~";
        if (directory.Length > trimmedHome.Length
            && directory.StartsWith(trimmedHome, comparison)
            && directory[trimmedHome.Length] is '/' or '\\')
            return "~" + directory[trimmedHome.Length..];
        return directory;
    }

    /// <summary>The last two folders of <paramref name="directory"/> (relative to home when inside it), joined by " › ".</summary>
    public static string Breadcrumb(string directory, string? home, StringComparison comparison)
    {
        var shortened = ShortenHome(directory, home, comparison);
        var parts = shortened.Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries)
            .Where(p => p != "~")
            .ToArray();
        if (parts.Length == 0)
            return shortened;
        return string.Join(" › ", parts.Skip(Math.Max(0, parts.Length - 2)));
    }

    /// <summary>Collapses whitespace runs to single spaces and trims, remapping highlight spans accordingly.</summary>
    public static (string Text, IReadOnlyList<TextSpan> Highlights) CollapseWhitespace(string text, IReadOnlyList<TextSpan>? highlights)
    {
        if (string.IsNullOrEmpty(text))
            return (string.Empty, []);
        var map = new int[text.Length + 1];
        var sb = new StringBuilder(text.Length);
        var pendingSpace = false;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (char.IsWhiteSpace(c))
            {
                // A highlight starting on whitespace starts at the (single) space that replaces the run.
                map[i] = sb.Length;
                if (sb.Length > 0)
                    pendingSpace = true;
                continue;
            }
            if (pendingSpace)
            {
                sb.Append(' ');
                pendingSpace = false;
            }
            map[i] = sb.Length;
            sb.Append(c);
        }
        map[text.Length] = sb.Length;

        var spans = new List<TextSpan>();
        if (highlights is not null)
        {
            foreach (var h in highlights)
            {
                var start = Math.Clamp(h.Start, 0, text.Length);
                var end = Math.Clamp(h.Start + h.Length, start, text.Length);
                var ns = map[start];
                var ne = end == text.Length ? sb.Length : map[end];
                // Do not let a highlight end on the collapsed space that follows it.
                while (ne > ns && sb[ne - 1] == ' ')
                    ne--;
                while (ns < ne && sb[ns] == ' ')
                    ns++;
                if (ne > ns)
                    spans.Add(new TextSpan(ns, ne - ns));
            }
        }
        return (sb.ToString(), spans);
    }

    /// <summary>Splits a chord like "Ctrl+Alt+Space" into display keys for the given flavor.</summary>
    public static IReadOnlyList<string> HotkeyKeys(string chord, UiFlavor flavor)
    {
        var keys = new List<string>();
        foreach (var raw in chord.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var lower = raw.ToLowerInvariant();
            keys.Add(flavor switch
            {
                UiFlavor.Mac => lower switch
                {
                    "cmd" or "command" or "meta" or "super" or "win" => "⌘",
                    "alt" or "option" or "opt" => "⌥",
                    "ctrl" or "control" => "⌃",
                    "shift" => "⇧",
                    _ => KeyName(raw),
                },
                UiFlavor.Windows => lower switch
                {
                    "super" or "meta" or "win" or "cmd" => "Win",
                    "control" => "Ctrl",
                    _ => KeyName(raw),
                },
                _ => lower switch
                {
                    "meta" or "win" or "cmd" => "Super",
                    "control" => "Ctrl",
                    _ => KeyName(raw),
                },
            });
        }
        return keys;
    }

    /// <summary>Single-line label of the configured chord in the platform's notation: "⌃⌥Space" on macOS,
    /// "Ctrl+Alt+Space" on Windows/Linux, "Win+K" / "Super+K" for the logo key.</summary>
    public static string HotkeyLabel(string chord, UiFlavor flavor)
    {
        if (HotkeyChord.TryParse(chord, out var parsed, out _))
        {
            return parsed.DisplayString(flavor switch
            {
                UiFlavor.Mac => ChordDisplayStyle.MacOS,
                UiFlavor.Windows => ChordDisplayStyle.Windows,
                _ => ChordDisplayStyle.Linux,
            });
        }
        // Unparseable (hand-edited settings): show it as written.
        return string.Join(flavor == UiFlavor.Mac ? string.Empty : "+", HotkeyKeys(chord, flavor));
    }

    private static string KeyName(string raw) =>
        raw.Length == 1 ? raw.ToUpperInvariant() : char.ToUpperInvariant(raw[0]) + raw[1..];

    /// <summary>Upper-case extension without the dot ("PDF"), or "FILE".</summary>
    /// <summary>Inserts a zero-width space after each '_', '-' and '.' (never at the end) as line-break opportunities.</summary>
    public static string WithBreakOpportunities(string name)
    {
        if (string.IsNullOrEmpty(name))
            return string.Empty;
        var sb = new System.Text.StringBuilder(name.Length + 8);
        for (var i = 0; i < name.Length; i++)
        {
            sb.Append(name[i]);
            if (name[i] is '_' or '-' or '.' && i < name.Length - 1)
                sb.Append('\u200B');
        }
        return sb.ToString();
    }

    public static string ExtensionLabel(string path)
    {
        var ext = Path.GetExtension(path);
        return string.IsNullOrEmpty(ext) || ext.Length > 6 ? "FILE" : ext[1..].ToUpperInvariant();
    }
}
