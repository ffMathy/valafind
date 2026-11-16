using System.IO.Compression;
using System.Text;

namespace Valafind.Core.Tests.Extraction;

/// <summary>
/// Programmatic builders for hostile files: decompression bombs that are a few hundred kilobytes on disk but
/// expand to gigabytes, written without ever materializing the expanded data.
/// </summary>
internal static class BombFixtures
{
    internal const long OneGigabyte = 1L << 30;
    private const int BlockBytes = 1 << 20;
    private const int WarmupBytes = 64 * 1024;

    /// <summary>
    /// Raw deflate data that decodes to <paramref name="prefix"/>, then about <paramref name="fillBytes"/> bytes of
    /// <paramref name="pattern"/> repeated, then <paramref name="suffix"/>. One sync-flushed block that encodes
    /// ~1 MiB of the pattern is repeated: once the 32 KiB window holds only whole repetitions of the pattern,
    /// every copy of that block decodes identically.
    /// </summary>
    internal static byte[] DeflateRepeated(byte[] prefix, byte[] pattern, long fillBytes, byte[] suffix, out long decodedLength)
    {
        var warmup = Repeat(pattern, (WarmupBytes + pattern.Length - 1) / pattern.Length);
        var block = Repeat(pattern, BlockBytes / pattern.Length);
        using var ms = new MemoryStream();
        long mark;
        long blockEnd;
        using (var deflate = new DeflateStream(ms, CompressionLevel.Optimal, leaveOpen: true))
        {
            deflate.Write(prefix);
            deflate.Write(warmup);
            deflate.Flush();
            mark = ms.Length;
            deflate.Write(block);
            deflate.Flush();
            blockEnd = ms.Length;
            deflate.Write(suffix);
        }

        var all = ms.ToArray();
        var repeated = all.AsSpan((int)mark, (int)(blockEnd - mark));
        var repetitions = (int)Math.Max(1, (fillBytes - warmup.Length + block.Length - 1) / block.Length);
        var output = new MemoryStream();
        output.Write(all.AsSpan(0, (int)blockEnd));
        for (var i = 1; i < repetitions; i++)
        {
            output.Write(repeated);
        }

        output.Write(all.AsSpan((int)blockEnd));
        decodedLength = prefix.Length + warmup.Length + ((long)repetitions * block.Length) + suffix.Length;
        return output.ToArray();
    }

    internal static byte[] DeflateRepeated(byte[] prefix, byte fill, long fillBytes, byte[] suffix, out long decodedLength) =>
        DeflateRepeated(prefix, [fill], fillBytes, suffix, out decodedLength);

    /// <summary>zlib framing (as used by PDF FlateDecode) around <see cref="DeflateRepeated"/>; the trailing
    /// Adler-32 is not valid, which no reader gets far enough to notice.</summary>
    internal static byte[] ZlibRepeated(string prefix, byte fill, long fillBytes, string suffix)
    {
        var deflate = DeflateRepeated(Encoding.ASCII.GetBytes(prefix), fill, fillBytes, Encoding.ASCII.GetBytes(suffix), out _);
        var result = new byte[deflate.Length + 6];
        result[0] = 0x78;
        result[1] = 0x9C;
        deflate.CopyTo(result, 2);
        return result;
    }

    private static byte[] Repeat(byte[] pattern, int times)
    {
        var bytes = new byte[pattern.Length * times];
        for (var i = 0; i < times; i++)
        {
            pattern.CopyTo(bytes, i * pattern.Length);
        }

        return bytes;
    }

    // ------------------------------------------------------------------ zip

    internal sealed record ZipItem(string Name, byte[] Data, long UncompressedLength, uint Crc, bool Deflated);

    /// <summary>A normal, correctly described deflated entry.</summary>
    internal static ZipItem Entry(string name, string content)
    {
        var bytes = Encoding.UTF8.GetBytes(content);
        using var ms = new MemoryStream();
        using (var deflate = new DeflateStream(ms, CompressionLevel.Optimal, leaveOpen: true))
        {
            deflate.Write(bytes);
        }

        return new ZipItem(name, ms.ToArray(), bytes.Length, Crc32(bytes), Deflated: true);
    }

    /// <summary>An entry whose content is <paramref name="prefix"/> + <paramref name="fillBytes"/> fill bytes +
    /// <paramref name="suffix"/>, declared with its true uncompressed length (CRC left at 0).</summary>
    internal static ZipItem BombEntry(string name, string prefix, string pattern, long fillBytes, string suffix)
    {
        var data = DeflateRepeated(Encoding.UTF8.GetBytes(prefix), Encoding.UTF8.GetBytes(pattern), fillBytes, Encoding.UTF8.GetBytes(suffix), out var length);

        // Moderate entries may be read to the end, so give them a valid CRC; gigabyte bombs never are.
        var crc = length <= 64L * 1024 * 1024 ? Crc32OfInflated(data) : 0u;
        return new ZipItem(name, data, length, crc, Deflated: true);
    }

    private static uint Crc32OfInflated(byte[] deflated)
    {
        using var inflater = new DeflateStream(new MemoryStream(deflated), CompressionMode.Decompress);
        var buffer = new byte[64 * 1024];
        var crc = 0xFFFFFFFFu;
        int n;
        while ((n = inflater.Read(buffer)) > 0)
        {
            crc = Crc32Update(crc, buffer.AsSpan(0, n));
        }

        return ~crc;
    }

    /// <summary>Writes a zip (ZIP64 records when sizes exceed 4 GiB are not needed: bombs stay below).</summary>
    internal static byte[] Zip(params ZipItem[] items)
    {
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true);
        var offsets = new List<long>();
        foreach (var item in items)
        {
            offsets.Add(ms.Position);
            var name = Encoding.UTF8.GetBytes(item.Name);
            w.Write(0x04034b50u);
            w.Write((ushort)20);
            w.Write((ushort)0x0800); // UTF-8 names
            w.Write((ushort)(item.Deflated ? 8 : 0));
            w.Write(0u); // time/date
            w.Write(item.Crc);
            w.Write((uint)item.Data.Length);
            w.Write((uint)item.UncompressedLength);
            w.Write((ushort)name.Length);
            w.Write((ushort)0);
            w.Write(name);
            w.Write(item.Data);
        }

        var cdStart = ms.Position;
        for (var i = 0; i < items.Length; i++)
        {
            var item = items[i];
            var name = Encoding.UTF8.GetBytes(item.Name);
            w.Write(0x02014b50u);
            w.Write((ushort)20);
            w.Write((ushort)20);
            w.Write((ushort)0x0800);
            w.Write((ushort)(item.Deflated ? 8 : 0));
            w.Write(0u);
            w.Write(item.Crc);
            w.Write((uint)item.Data.Length);
            w.Write((uint)item.UncompressedLength);
            w.Write((ushort)name.Length);
            w.Write((ushort)0); // extra
            w.Write((ushort)0); // comment
            w.Write((ushort)0); // disk
            w.Write((ushort)0); // internal attrs
            w.Write(0u);        // external attrs
            w.Write((uint)offsets[i]);
            w.Write(name);
        }

        var cdSize = ms.Position - cdStart;
        w.Write(0x06054b50u);
        w.Write((ushort)0);
        w.Write((ushort)0);
        w.Write((ushort)Math.Min(items.Length, 0xFFFF));
        w.Write((ushort)Math.Min(items.Length, 0xFFFF));
        w.Write((uint)cdSize);
        w.Write((uint)cdStart);
        w.Write((ushort)0);
        w.Flush();
        return ms.ToArray();
    }

    internal static uint Crc32(ReadOnlySpan<byte> data) => ~Crc32Update(0xFFFFFFFFu, data);

    private static readonly uint[] CrcTable = BuildCrcTable();

    private static uint[] BuildCrcTable()
    {
        var table = new uint[256];
        for (var i = 0u; i < 256; i++)
        {
            var c = i;
            for (var k = 0; k < 8; k++)
            {
                c = (c & 1) != 0 ? (c >> 1) ^ 0xEDB88320u : c >> 1;
            }

            table[i] = c;
        }

        return table;
    }

    private static uint Crc32Update(uint crc, ReadOnlySpan<byte> data)
    {
        foreach (var b in data)
        {
            crc = CrcTable[(crc ^ b) & 0xFF] ^ (crc >> 8);
        }

        return crc;
    }

    // ------------------------------------------------------------------ OOXML / ODF / EPUB skeletons

    internal const string ContentTypes = """
        <?xml version="1.0" encoding="UTF-8"?><Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types"><Default Extension="xml" ContentType="application/xml"/></Types>
        """;

    internal static string RootRels(string target) =>
        "<?xml version=\"1.0\" encoding=\"UTF-8\"?><Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">" +
        $"<Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument\" Target=\"{target}\"/></Relationships>";

    internal const string WordPrefix = "<?xml version=\"1.0\" encoding=\"UTF-8\"?><w:document xmlns:w=\"http://schemas.openxmlformats.org/wordprocessingml/2006/main\"><w:body>";
    internal const string WordSuffix = "</w:body></w:document>";

    internal const string OdtPrefix = """
        <?xml version="1.0" encoding="UTF-8"?><office:document-content xmlns:office="urn:oasis:names:tc:opendocument:xmlns:office:1.0" xmlns:text="urn:oasis:names:tc:opendocument:xmlns:text:1.0"><office:body><office:text>
        """;

    internal const string OdtSuffix = "</office:text></office:body></office:document-content>";

    internal static string WorkbookXml(int sheets = 1)
    {
        var sb = new StringBuilder("<?xml version=\"1.0\" encoding=\"UTF-8\"?><workbook xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\" xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\"><sheets>");
        for (var i = 1; i <= sheets; i++)
        {
            sb.Append(System.Globalization.CultureInfo.InvariantCulture, $"<sheet name=\"S{i}\" sheetId=\"{i}\" r:id=\"rId{i}\"/>");
        }

        return sb.Append("</sheets></workbook>").ToString();
    }

    internal static string WorkbookRels(int sheets = 1, bool sharedStrings = false)
    {
        var sb = new StringBuilder("<?xml version=\"1.0\" encoding=\"UTF-8\"?><Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">");
        for (var i = 1; i <= sheets; i++)
        {
            sb.Append(System.Globalization.CultureInfo.InvariantCulture,
                $"<Relationship Id=\"rId{i}\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet\" Target=\"worksheets/sheet{i}.xml\"/>");
        }

        if (sharedStrings)
        {
            sb.Append("<Relationship Id=\"rIdS\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/sharedStrings\" Target=\"sharedStrings.xml\"/>");
        }

        return sb.Append("</Relationships>").ToString();
    }

    internal const string SheetPrefix = "<?xml version=\"1.0\" encoding=\"UTF-8\"?><worksheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\"><sheetData>";
    internal const string SheetSuffix = "</sheetData></worksheet>";
    internal const string SstPrefix = "<?xml version=\"1.0\" encoding=\"UTF-8\"?><sst xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\">";

    internal const string EpubContainer = """
        <?xml version="1.0"?><container version="1.0" xmlns="urn:oasis:names:tc:opendocument:xmlns:container"><rootfiles><rootfile full-path="OEBPS/content.opf" media-type="application/oebps-package+xml"/></rootfiles></container>
        """;

    internal const string OpfPrefix = """
        <?xml version="1.0"?><package xmlns="http://www.idpf.org/2007/opf" version="3.0"><manifest><item id="c1" href="c1.xhtml" media-type="application/xhtml+xml"/><item id="c2" href="c2.xhtml" media-type="application/xhtml+xml"/></manifest><spine>
        """;

    // ------------------------------------------------------------------ PDF

    /// <summary>A minimal PDF writer: objects are numbered from 1 in the order given.</summary>
    internal static byte[] Pdf(params byte[][] objects)
    {
        using var ms = new MemoryStream();
        void Ascii(string s) => ms.Write(Encoding.ASCII.GetBytes(s));
        Ascii("%PDF-1.7\n%âã\n");
        var offsets = new List<long>();
        for (var i = 0; i < objects.Length; i++)
        {
            offsets.Add(ms.Position);
            Ascii($"{i + 1} 0 obj\n");
            ms.Write(objects[i]);
            Ascii("\nendobj\n");
        }

        var xref = ms.Position;
        Ascii($"xref\n0 {objects.Length + 1}\n0000000000 65535 f \n");
        foreach (var offset in offsets)
        {
            Ascii($"{offset:D10} 00000 n \n");
        }

        Ascii($"trailer\n<< /Size {objects.Length + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        return ms.ToArray();
    }

    /// <summary>The entries every cross-reference stream written by <see cref="PdfWithXrefStream"/> needs.</summary>
    internal static string XrefStreamCore(int objects) => $"/Type /XRef /Size {objects + 2} /W [1 4 2] /Root 1 0 R";

    /// <summary>
    /// A PDF whose cross-reference table is a FlateDecode xref stream (object <c>objects.Length + 1</c>), which
    /// PdfPig decodes while it opens the file. The stream decodes to the real xref rows followed by about
    /// <paramref name="fillBytes"/> zero bytes. <paramref name="dictionaryEntries"/> is written verbatim after
    /// /Length and must include the filter (and <see cref="XrefStreamCore"/>).
    /// </summary>
    internal static byte[] PdfWithXrefStream(string dictionaryEntries, long fillBytes, params byte[][] objects)
    {
        using var ms = new MemoryStream();
        void Ascii(string s) => ms.Write(Encoding.ASCII.GetBytes(s));
        Ascii("%PDF-1.7\n%\u00e2\u00e3\n");
        var offsets = new List<long>();
        for (var i = 0; i < objects.Length; i++)
        {
            offsets.Add(ms.Position);
            Ascii($"{i + 1} 0 obj\n");
            ms.Write(objects[i]);
            Ascii("\nendobj\n");
        }

        var xref = ms.Position;
        offsets.Add(xref);
        var rows = new MemoryStream();
        rows.Write([0, 0, 0, 0, 0, 0xFF, 0xFF]);
        foreach (var offset in offsets)
        {
            rows.Write([1, (byte)(offset >> 24), (byte)(offset >> 16), (byte)(offset >> 8), (byte)offset, 0, 0]);
        }

        var deflate = DeflateRepeated(rows.ToArray(), 0, fillBytes, [], out _);
        var data = new byte[deflate.Length + 6];
        data[0] = 0x78;
        data[1] = 0x9C;
        deflate.CopyTo(data, 2);

        Ascii($"{objects.Length + 1} 0 obj\n<< /Length {data.Length} {dictionaryEntries} >>\nstream\n");
        ms.Write(data);
        Ascii($"\nendstream\nendobj\nstartxref\n{xref}\n%%EOF\n");
        return ms.ToArray();
    }

    internal static byte[] Obj(string text) => Encoding.ASCII.GetBytes(text);

    internal static byte[] PdfStream(string dictionaryEntries, byte[] data)
    {
        using var ms = new MemoryStream();
        ms.Write(Encoding.ASCII.GetBytes($"<< /Length {data.Length} {dictionaryEntries} >>\nstream\n"));
        ms.Write(data);
        ms.Write(Encoding.ASCII.GetBytes("\nendstream"));
        return ms.ToArray();
    }

    internal static byte[] TextContent(string text) =>
        Encoding.ASCII.GetBytes($"BT /F1 12 Tf 40 750 Td ({text}) Tj ET");

    internal static byte[] Font() => Obj("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>");
}
