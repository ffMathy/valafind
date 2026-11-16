using System.Buffers.Binary;
using System.Text;
using Valafind.Core.Indexing;
using Xunit;

namespace Valafind.Core.Tests.Indexing;

public class ShellLinkParserTests
{
    private static readonly byte[] LinkClsid = [0x01, 0x14, 0x02, 0x00, 0x00, 0x00, 0x00, 0x00, 0xC0, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x46];

    /// <summary>Builds a minimal MS-SHLLINK file: header, optional IDList, LinkInfo with VolumeID and paths.</summary>
    private static byte[] BuildLink(string? ansiBase, string? unicodeBase, string suffix = "", bool withIdList = true,
        uint linkInfoFlags = 1, bool withLinkInfo = true)
    {
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        uint flags = 0x80; // IsUnicode
        if (withIdList)
            flags |= 0x1;
        if (withLinkInfo)
            flags |= 0x2;

        // ShellLinkHeader (76 bytes)
        w.Write(0x4C);
        w.Write(LinkClsid);
        w.Write(flags);
        w.Write(0x20u); // FILE_ATTRIBUTE_ARCHIVE
        w.Write(0L); w.Write(0L); w.Write(0L); // times
        w.Write(1234u); // file size
        w.Write(0); // icon index
        w.Write(1u); // show normal
        w.Write((ushort)0); // hotkey
        w.Write((ushort)0); w.Write(0u); w.Write(0u); // reserved
        Assert.Equal(76, ms.Length);

        if (withIdList)
        {
            // IDListSize + one fake item + terminal
            var item = new byte[] { 0x06, 0x00, 0xAA, 0xBB, 0xCC, 0xDD };
            w.Write((ushort)(item.Length + 2));
            w.Write(item);
            w.Write((ushort)0);
        }

        if (!withLinkInfo)
            return ms.ToArray();

        var unicode = unicodeBase is not null;
        var headerSize = unicode ? 0x24 : 0x1C;
        var volumeId = new byte[] { 0x11, 0, 0, 0, 3, 0, 0, 0, 0x78, 0x56, 0x34, 0x12, 0x10, 0, 0, 0, 0 }; // size 17, DRIVE_FIXED, serial, label offset, ""
        var ansiBytes = Encoding.Latin1.GetBytes((ansiBase ?? "") + "\0");
        var ansiSuffix = Encoding.Latin1.GetBytes(suffix + "\0");
        var uniBytes = unicode ? Encoding.Unicode.GetBytes(unicodeBase + "\0") : [];
        var uniSuffix = unicode ? Encoding.Unicode.GetBytes(suffix + "\0") : [];

        var volumeOffset = headerSize;
        var localBaseOffset = volumeOffset + volumeId.Length;
        var suffixOffset = localBaseOffset + ansiBytes.Length;
        var uniBaseOffset = suffixOffset + ansiSuffix.Length;
        var uniSuffixOffset = uniBaseOffset + uniBytes.Length;
        var total = uniSuffixOffset + uniSuffix.Length;

        var info = new byte[total];
        BinaryPrimitives.WriteUInt32LittleEndian(info.AsSpan(0), (uint)total);
        BinaryPrimitives.WriteUInt32LittleEndian(info.AsSpan(4), (uint)headerSize);
        BinaryPrimitives.WriteUInt32LittleEndian(info.AsSpan(8), linkInfoFlags);
        BinaryPrimitives.WriteUInt32LittleEndian(info.AsSpan(12), (uint)volumeOffset);
        BinaryPrimitives.WriteUInt32LittleEndian(info.AsSpan(16), (uint)localBaseOffset);
        BinaryPrimitives.WriteUInt32LittleEndian(info.AsSpan(20), 0); // CommonNetworkRelativeLinkOffset
        BinaryPrimitives.WriteUInt32LittleEndian(info.AsSpan(24), (uint)suffixOffset);
        if (unicode)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(info.AsSpan(28), (uint)uniBaseOffset);
            BinaryPrimitives.WriteUInt32LittleEndian(info.AsSpan(32), (uint)uniSuffixOffset);
        }
        volumeId.CopyTo(info, volumeOffset);
        ansiBytes.CopyTo(info, localBaseOffset);
        ansiSuffix.CopyTo(info, suffixOffset);
        uniBytes.CopyTo(info, uniBaseOffset);
        uniSuffix.CopyTo(info, uniSuffixOffset);
        w.Write(info);

        // StringData (NAME_STRING) and the terminal extra-data block are irrelevant to the parser.
        w.Write((ushort)2);
        w.Write(Encoding.Unicode.GetBytes("hi"));
        w.Write(0u);
        return ms.ToArray();
    }

    [Fact]
    public void ReadsAnsiLocalBasePath()
    {
        var link = BuildLink(@"C:\Temp\notes.txt", null);
        Assert.Equal(@"C:\Temp\notes.txt", ShellLinkParser.TryGetLocalTarget(link));
    }

    [Fact]
    public void PrefersUnicodeLocalBasePath()
    {
        var link = BuildLink(@"C:\Users\Zo?\Documents\Report.docx", @"C:\Users\Zoë\Documents\Report.docx");
        Assert.Equal(@"C:\Users\Zoë\Documents\Report.docx", ShellLinkParser.TryGetLocalTarget(link));
    }

    [Fact]
    public void AppendsCommonPathSuffix()
    {
        var link = BuildLink(@"C:\Data", @"C:\Data", suffix: @"sub\file.pdf", withIdList: false);
        Assert.Equal(@"C:\Data\sub\file.pdf", ShellLinkParser.TryGetLocalTarget(link));
    }

    [Fact]
    public void NetworkOnlyLinksAreIgnored()
    {
        var link = BuildLink(@"\\server\share\x.docx", null, linkInfoFlags: 2);
        Assert.Null(ShellLinkParser.TryGetLocalTarget(link));
    }

    [Fact]
    public void LinksWithoutLinkInfoAreIgnored() =>
        Assert.Null(ShellLinkParser.TryGetLocalTarget(BuildLink(null, null, withLinkInfo: false)));

    [Fact]
    public void MalformedInputReturnsNull()
    {
        Assert.Null(ShellLinkParser.TryGetLocalTarget([]));
        Assert.Null(ShellLinkParser.TryGetLocalTarget(new byte[76]));
        var link = BuildLink(@"C:\Temp\notes.txt", null);
        Assert.Null(ShellLinkParser.TryGetLocalTarget(link.AsSpan(0, 100)));
        // Corrupt LinkInfo size
        var corrupt = (byte[])link.Clone();
        BinaryPrimitives.WriteUInt32LittleEndian(corrupt.AsSpan(76 + 2 + 8), 0xFFFFFF);
        Assert.Null(ShellLinkParser.TryGetLocalTarget(corrupt));
    }
}

public class XbelParserTests
{
    private const string Sample = """
        <?xml version="1.0" encoding="UTF-8"?>
        <xbel version="1.0"
              xmlns:bookmark="http://www.freedesktop.org/standards/desktop-bookmarks"
              xmlns:mime="http://www.freedesktop.org/standards/shared-mime-info">
          <bookmark href="file:///home/u/Documents/My%20Report.pdf" added="2026-01-01T10:00:00.000000Z" modified="2026-03-01T10:00:00.000000Z" visited="2026-03-05T08:30:00.123456Z">
            <info>
              <metadata owner="http://freedesktop.org">
                <mime:mime-type type="application/pdf"/>
                <bookmark:applications>
                  <bookmark:application name="Evince" exec="&apos;evince %u&apos;" modified="2026-03-05T08:30:00Z" count="7"/>
                  <bookmark:application name="Firefox" exec="&apos;firefox %u&apos;" modified="2026-02-01T08:30:00Z" count="2"/>
                </bookmark:applications>
              </metadata>
            </info>
          </bookmark>
          <bookmark href="https://example.com/x" added="2026-01-01T10:00:00Z" modified="2026-01-01T10:00:00Z" visited="2026-01-01T10:00:00Z"/>
          <bookmark href="file:///home/u/notes.md" added="2025-12-01T10:00:00Z" modified="2025-12-01T10:00:00Z" visited="2025-12-02T10:00:00Z">
            <info><metadata owner="http://freedesktop.org"><bookmark:applications>
              <bookmark:application name="gedit" exec="gedit %u" modified="2026-04-01T00:00:00Z" count="1"/>
            </bookmark:applications></metadata></info>
          </bookmark>
          <bookmark href="file://otherhost/share/remote.txt" added="2026-01-01T10:00:00Z"/>
        </xbel>
        """;

    [Fact]
    public void ParsesFileBookmarksWithCountsAndTimes()
    {
        var entries = XbelParser.Parse(new StringReader(Sample));
        Assert.Equal(2, entries.Count);

        var report = entries[0];
        Assert.Equal("/home/u/Documents/My Report.pdf", report.Path);
        Assert.Equal(9, report.Count);
        Assert.Equal(new DateTimeOffset(2026, 3, 5, 8, 30, 0, 123, TimeSpan.Zero).AddTicks(4560), report.LastUsedUtc);

        var notes = entries[1];
        Assert.Equal("/home/u/notes.md", notes.Path);
        Assert.Equal(1, notes.Count);
        Assert.Equal(new DateTimeOffset(2026, 4, 1, 0, 0, 0, TimeSpan.Zero), notes.LastUsedUtc); // application modified is newest
    }

    [Fact]
    public void TruncatedXmlKeepsParsedEntries()
    {
        var truncated = Sample[..Sample.IndexOf("<bookmark href=\"https", StringComparison.Ordinal)] + "<bookmark href=\"file:///x";
        var entries = XbelParser.Parse(new StringReader(truncated));
        Assert.Single(entries);
    }

    [Theory]
    [InlineData("file:///home/u/a%20b.txt", "/home/u/a b.txt")]
    [InlineData("file://localhost/home/u/a.txt", "/home/u/a.txt")]
    [InlineData("https://example.com/a.txt", null)]
    [InlineData("file://server/share/a.txt", null)]
    [InlineData("not a uri", null)]
    [InlineData("", null)]
    public void FileUriToPath(string uri, string? expected)
    {
        Assert.SkipWhen(OperatingSystem.IsWindows() && expected is not null, "Unix-style local paths");
        Assert.Equal(expected, XbelParser.FileUriToPath(uri));
    }
}

public class UserDirsParserTests
{
    [Fact]
    public void ParsesXdgUserDirs()
    {
        const string text = """
            # This file is written by xdg-user-dirs-update
            XDG_DESKTOP_DIR="$HOME/Desktop"
            XDG_DOWNLOAD_DIR="$HOME/Téléchargements"
            XDG_TEMPLATES_DIR="$HOME/"
            XDG_DOCUMENTS_DIR="/data/docs/"
            XDG_MUSIC_DIR="relative/not/allowed"
            garbage line
            """;
        var dirs = UserDirsParser.Parse(text, "/home/u");
        Assert.Equal("/home/u/Desktop", dirs["XDG_DESKTOP_DIR"]);
        Assert.Equal("/home/u/Téléchargements", dirs["XDG_DOWNLOAD_DIR"]);
        Assert.Equal("/home/u", dirs["XDG_TEMPLATES_DIR"]);
        Assert.Equal("/data/docs", dirs["XDG_DOCUMENTS_DIR"]);
        Assert.False(dirs.ContainsKey("XDG_MUSIC_DIR"));
        Assert.Equal(4, dirs.Count);
    }
}

public class KdeRecentParserTests
{
    [Theory]
    [InlineData("[Desktop Entry]\nIcon=x\nName=a.pdf\nType=Link\nURL[$e]=file:///home/u/a%20b.pdf\n", "/home/u/a b.pdf")]
    [InlineData("[Desktop Entry]\nURL=$HOME/notes.txt\n", "/home/u/notes.txt")]
    [InlineData("[Desktop Entry]\nURL=/abs/file.txt\n", "/abs/file.txt")]
    [InlineData("[Desktop Entry]\nURL=https://example.com\n", null)]
    [InlineData("[Desktop Entry]\nURLX=/x\n", null)]
    [InlineData("", null)]
    public void ExtractsUrl(string text, string? expected)
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Unix-style local paths");
        Assert.Equal(expected, KdeRecentParser.GetPath(text, "/home/u"));
    }
}
