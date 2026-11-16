using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using System.Xml;

namespace Valafind.Core.Indexing;

/// <summary>A file reported by an OS "recent documents" source.</summary>
/// <param name="Path">Absolute local path.</param>
/// <param name="Count">Usage count (0 when unknown).</param>
/// <param name="LastUsedUtc">Last use, when the source records it.</param>
internal readonly record struct RecentEntry(string Path, int Count, DateTimeOffset? LastUsedUtc);

/// <summary>Parser for Windows Shell Link (.lnk) files per [MS-SHLLINK]; only the local target path is extracted.</summary>
internal static class ShellLinkParser
{
    private const int HeaderSize = 0x4C;
    private const uint HasLinkTargetIdList = 0x1;
    private const uint HasLinkInfo = 0x2;
    private const uint VolumeIdAndLocalBasePath = 0x1;

    /// <summary>Returns the local target path, or null for network targets, ID-list-only links or malformed data.</summary>
    public static string? TryGetLocalTarget(ReadOnlySpan<byte> data)
    {
        try
        {
            if (data.Length < HeaderSize || BinaryPrimitives.ReadUInt32LittleEndian(data) != HeaderSize)
                return null;
            // LinkCLSID 00021401-0000-0000-C000-000000000046
            if (data[4] != 0x01 || data[5] != 0x14 || data[6] != 0x02 || data[7] != 0x00)
                return null;
            var flags = BinaryPrimitives.ReadUInt32LittleEndian(data[20..]);
            var offset = HeaderSize;

            if ((flags & HasLinkTargetIdList) != 0)
            {
                if (offset + 2 > data.Length)
                    return null;
                offset += 2 + BinaryPrimitives.ReadUInt16LittleEndian(data[offset..]);
            }
            if ((flags & HasLinkInfo) == 0 || offset + 28 > data.Length)
                return null;

            var info = data[offset..];
            var infoSize = (int)BinaryPrimitives.ReadUInt32LittleEndian(info);
            var infoHeaderSize = (int)BinaryPrimitives.ReadUInt32LittleEndian(info[4..]);
            var infoFlags = BinaryPrimitives.ReadUInt32LittleEndian(info[8..]);
            if (infoSize < 28 || infoSize > info.Length || infoHeaderSize < 28)
                return null;
            info = info[..infoSize];
            if ((infoFlags & VolumeIdAndLocalBasePath) == 0)
                return null; // network share only — never index remote files

            var localBaseOffset = (int)BinaryPrimitives.ReadUInt32LittleEndian(info[16..]);
            var commonSuffixOffset = (int)BinaryPrimitives.ReadUInt32LittleEndian(info[24..]);
            string? basePath = null, suffix = null;
            if (infoHeaderSize >= 0x24 && info.Length >= 0x24)
            {
                var localBaseUnicode = (int)BinaryPrimitives.ReadUInt32LittleEndian(info[28..]);
                var suffixUnicode = (int)BinaryPrimitives.ReadUInt32LittleEndian(info[32..]);
                basePath = ReadUtf16Z(info, localBaseUnicode);
                suffix = ReadUtf16Z(info, suffixUnicode);
            }
            basePath ??= ReadAnsiZ(info, localBaseOffset);
            suffix ??= ReadAnsiZ(info, commonSuffixOffset);
            if (string.IsNullOrEmpty(basePath))
                return null;
            if (string.IsNullOrEmpty(suffix))
                return basePath;
            return basePath.EndsWith('\\') ? basePath + suffix : basePath + "\\" + suffix;
        }
        catch (ArgumentOutOfRangeException)
        {
            return null;
        }
    }

    private static string? ReadAnsiZ(ReadOnlySpan<byte> block, int offset)
    {
        if (offset <= 0 || offset >= block.Length)
            return null;
        var slice = block[offset..];
        var end = slice.IndexOf((byte)0);
        if (end < 0)
            return null;
        // The ANSI code page is unknown here; Latin-1 is exact for ASCII paths, and Unicode fields are preferred.
        return Encoding.Latin1.GetString(slice[..end]);
    }

    private static string? ReadUtf16Z(ReadOnlySpan<byte> block, int offset)
    {
        if (offset <= 0 || offset >= block.Length)
            return null;
        var slice = block[offset..];
        for (var i = 0; i + 1 < slice.Length; i += 2)
        {
            if (slice[i] == 0 && slice[i + 1] == 0)
                return Encoding.Unicode.GetString(slice[..i]);
        }
        return null;
    }
}

/// <summary>Parser for freedesktop recently-used.xbel (GTK recent files).</summary>
internal static class XbelParser
{
    private const string BookmarkNs = "http://www.freedesktop.org/standards/desktop-bookmarks";

    public static List<RecentEntry> Parse(TextReader reader)
    {
        var result = new List<RecentEntry>();
        var settings = new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Ignore,
            XmlResolver = null,
            IgnoreComments = true,
            IgnoreWhitespace = true,
            CheckCharacters = false,
        };
        using var xml = XmlReader.Create(reader, settings);
        string? currentPath = null;
        DateTimeOffset? currentTime = null;
        var currentCount = 0;
        try
        {
            while (xml.Read())
            {
                if (xml.NodeType == XmlNodeType.Element && xml.LocalName == "bookmark" && xml.NamespaceURI.Length == 0)
                {
                    Flush();
                    currentPath = FileUriToPath(xml.GetAttribute("href"));
                    currentTime = Max(Max(ParseTime(xml.GetAttribute("visited")), ParseTime(xml.GetAttribute("modified"))),
                        ParseTime(xml.GetAttribute("added")));
                    currentCount = 0;
                    if (xml.IsEmptyElement)
                        Flush();
                }
                else if (xml.NodeType == XmlNodeType.Element && xml.LocalName == "application" && xml.NamespaceURI == BookmarkNs &&
                         currentPath is not null)
                {
                    if (int.TryParse(xml.GetAttribute("count"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var c) && c > 0)
                        currentCount += c;
                    currentTime = Max(currentTime, ParseTime(xml.GetAttribute("modified")));
                }
                else if (xml.NodeType == XmlNodeType.EndElement && xml.LocalName == "bookmark" && xml.NamespaceURI.Length == 0)
                {
                    Flush();
                }
            }
        }
        catch (XmlException)
        {
            // Truncated / corrupt file: keep what was parsed so far.
        }
        Flush();
        return result;

        void Flush()
        {
            if (currentPath is not null)
                result.Add(new RecentEntry(currentPath, Math.Max(1, currentCount), currentTime));
            currentPath = null;
        }
    }

    private static DateTimeOffset? Max(DateTimeOffset? a, DateTimeOffset? b) =>
        a is null ? b : b is null ? a : a > b ? a : b;

    private static DateTimeOffset? ParseTime(string? value) =>
        DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var t)
            ? t
            : null;

    /// <summary>Converts a file:// URI to a local path; returns null for other schemes or remote hosts.</summary>
    internal static string? FileUriToPath(string? uri)
    {
        if (string.IsNullOrWhiteSpace(uri))
            return null;
        if (!Uri.TryCreate(uri.Trim(), UriKind.Absolute, out var parsed) || !parsed.IsFile)
            return null;
        if (string.IsNullOrEmpty(parsed.Host))
            return parsed.LocalPath;
        if (!parsed.IsLoopback)
            return null; // file://server/share — remote
        // file://localhost/path: drop the host, otherwise .NET renders it as a UNC path.
        return new Uri("file:///" + parsed.AbsolutePath.TrimStart('/')).LocalPath;
    }
}

/// <summary>Parser for ~/.config/user-dirs.dirs (xdg-user-dirs).</summary>
internal static class UserDirsParser
{
    /// <summary>Returns KEY → absolute path (e.g. "XDG_DOCUMENTS_DIR" → "/home/u/Documents").</summary>
    public static Dictionary<string, string> Parse(string text, string home)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line[0] == '#')
                continue;
            var eq = line.IndexOf('=');
            if (eq <= 0)
                continue;
            var key = line[..eq].Trim();
            var value = line[(eq + 1)..].Trim();
            if (value.Length >= 2 && value[0] == '"' && value[^1] == '"')
                value = value[1..^1];
            value = value.Replace("\\\"", "\"", StringComparison.Ordinal);
            if (value.StartsWith("$HOME", StringComparison.Ordinal))
                value = home.TrimEnd('/') + value[5..];
            else if (!value.StartsWith('/'))
                continue; // the spec only allows $HOME-relative or absolute paths
            value = value.TrimEnd('/');
            if (value.Length == 0)
                value = "/";
            result[key] = value;
        }
        return result;
    }
}

/// <summary>Parser for KDE ~/.local/share/RecentDocuments/*.desktop entries.</summary>
internal static class KdeRecentParser
{
    public static string? GetPath(string desktopFileText, string home)
    {
        foreach (var raw in desktopFileText.Split('\n'))
        {
            var line = raw.Trim();
            if (!line.StartsWith("URL", StringComparison.Ordinal))
                continue;
            var eq = line.IndexOf('=');
            if (eq < 0)
                continue;
            var key = line[..eq];
            if (key != "URL" && key != "URL[$e]")
                continue;
            var value = line[(eq + 1)..].Trim();
            if (value.StartsWith("$HOME", StringComparison.Ordinal))
                value = home.TrimEnd('/') + value[5..];
            if (value.StartsWith("file:", StringComparison.OrdinalIgnoreCase))
                return XbelParser.FileUriToPath(value);
            return value.StartsWith('/') ? value : null;
        }
        return null;
    }
}
