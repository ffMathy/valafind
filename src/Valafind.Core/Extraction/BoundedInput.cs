using System.Buffers.Binary;
using System.IO.Compression;
using System.Xml;

namespace Valafind.Core.Extraction;

/// <summary>
/// Thrown when hostile or absurd input would exceed a hard resource limit (decompressed bytes, entry count,
/// expansion ratio). Extractors catch it to keep the text gathered so far; if it escapes,
/// <see cref="DocumentReader"/> indexes the file by name only.
/// </summary>
internal sealed class ExtractionLimitException(string message, bool fileBudgetExhausted = false) : IOException(message)
{
    /// <summary>True when the per-file budget (not just one entry's) is used up: stop reading the file.</summary>
    internal bool FileBudgetExhausted { get; } = fileBudgetExhausted;
}

/// <summary>Bytes that may still be decompressed from one file, shared by all of its entries.</summary>
internal sealed class DecompressionBudget(long bytes)
{
    internal long Remaining { get; private set; } = bytes;

    internal void Consume(int count)
    {
        Remaining -= count;
        if (Remaining < 0)
        {
            throw new ExtractionLimitException("Per-file decompression budget exhausted.", fileBudgetExhausted: true);
        }
    }
}

/// <summary>
/// Read-only forward stream that counts the bytes read through it and throws
/// <see cref="ExtractionLimitException"/> once more than <c>maxBytes</c> (or the shared file budget) has been
/// read. Bounds the memory and time an extractor can spend on a decompression bomb.
/// </summary>
internal sealed class BoundedReadStream(Stream inner, long maxBytes, DecompressionBudget? budget) : Stream
{
    private long _read;

    internal long BytesRead => _read;

    public override bool CanRead => true;

    public override bool CanSeek => false;

    public override bool CanWrite => false;

    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => _read;
        set => throw new NotSupportedException();
    }

    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

    public override int Read(Span<byte> buffer)
    {
        var n = inner.Read(buffer);
        Account(n);
        return n;
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        var n = await inner.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
        Account(n);
        return n;
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    private void Account(int n)
    {
        if (n <= 0)
        {
            return;
        }

        _read += n;
        if (_read > maxBytes)
        {
            throw new ExtractionLimitException($"Stream exceeded {maxBytes} decompressed bytes.");
        }

        budget?.Consume(n);
    }

    public override void Flush()
    {
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            inner.Dispose();
        }

        base.Dispose(disposing);
    }
}

/// <summary>
/// Opens zip-based documents (OOXML, OpenDocument, EPUB) with hard limits: entry count, per-entry and per-file
/// decompressed bytes, and compression ratio. .NET truncates an entry's stream at its declared uncompressed
/// <see cref="ZipArchiveEntry.Length"/>, so the declared length is a trustworthy upper bound.
/// </summary>
internal static class SafeZip
{
    /// <summary>Maximum entries in the central directory; real documents have tens to a few thousand.</summary>
    internal const int MaxEntries = 20_000;

    /// <summary>Maximum decompressed bytes read from one entry (bounds the largest XML token to ~16M chars).</summary>
    internal const long MaxEntryBytes = 16L * 1024 * 1024;

    /// <summary>Maximum decompressed bytes read from all entries of one file.</summary>
    internal const long MaxFileBytes = 64L * 1024 * 1024;

    /// <summary>Entries larger than this are skipped when their compression ratio exceeds <see cref="MaxCompressionRatio"/>.</summary>
    internal const long SuspiciousEntryBytes = 8L * 1024 * 1024;

    internal const long MaxCompressionRatio = 100;

    /// <summary>
    /// Opens <paramref name="stream"/> as a zip after checking that its central directory holds at most
    /// <see cref="MaxEntries"/> records (ZipArchive materializes every entry up front).
    /// </summary>
    /// <exception cref="ExtractionLimitException">Too many entries.</exception>
    /// <exception cref="InvalidDataException">Not a zip.</exception>
    internal static ZipArchive Open(Stream stream)
    {
        if (CountCentralDirectoryEntries(stream, MaxEntries) > MaxEntries)
        {
            throw new ExtractionLimitException($"Zip has more than {MaxEntries} entries.", fileBudgetExhausted: true);
        }

        stream.Position = 0;
        return new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: true);
    }

    internal static DecompressionBudget NewFileBudget() => new(MaxFileBytes);

    /// <summary>
    /// Opens an entry for reading through a <see cref="BoundedReadStream"/>.
    /// </summary>
    /// <exception cref="ExtractionLimitException">The entry looks like a decompression bomb (large and
    /// compressed more than <see cref="MaxCompressionRatio"/>:1).</exception>
    internal static Stream OpenEntry(ZipArchiveEntry entry, DecompressionBudget budget, long maxBytes = MaxEntryBytes)
    {
        if (IsSuspicious(entry))
        {
            throw new ExtractionLimitException($"Entry '{entry.FullName}' looks like a decompression bomb.");
        }

        return new BoundedReadStream(entry.Open(), maxBytes, budget);
    }

    internal static bool IsSuspicious(ZipArchiveEntry entry) =>
        entry.Length > SuspiciousEntryBytes
        && entry.Length > Math.Max(entry.CompressedLength, 1) * MaxCompressionRatio;

    /// <summary>
    /// Walks central-directory records the way ZipArchive does (until a non-record signature) and returns the
    /// count, stopping at <paramref name="cap"/> + 1. Returns 0 when the directory cannot be located; ZipArchive
    /// then reports the damage itself.
    /// </summary>
    internal static int CountCentralDirectoryEntries(Stream stream, int cap)
    {
        var length = stream.Length;
        if (length < 22)
        {
            return 0;
        }

        // End of central directory: 22 bytes + up to 64 KB comment, at the end of the file.
        var tailLength = (int)Math.Min(length, 22 + 65_535 + 20);
        var tail = new byte[tailLength];
        stream.Position = length - tailLength;
        stream.ReadExactly(tail);
        var eocd = -1;
        for (var i = tailLength - 22; i >= 0; i--)
        {
            if (BinaryPrimitives.ReadUInt32LittleEndian(tail.AsSpan(i)) == 0x06054b50)
            {
                eocd = i;
                break;
            }
        }

        if (eocd < 0)
        {
            return 0;
        }

        long declared = BinaryPrimitives.ReadUInt16LittleEndian(tail.AsSpan(eocd + 10));
        long cdOffset = BinaryPrimitives.ReadUInt32LittleEndian(tail.AsSpan(eocd + 16));
        if ((declared == 0xFFFF || cdOffset == 0xFFFFFFFF) && eocd >= 20
            && BinaryPrimitives.ReadUInt32LittleEndian(tail.AsSpan(eocd - 20)) == 0x07064b50)
        {
            var zip64Offset = BinaryPrimitives.ReadInt64LittleEndian(tail.AsSpan(eocd - 20 + 8));
            if (zip64Offset >= 0 && zip64Offset <= length - 56)
            {
                Span<byte> z = stackalloc byte[56];
                stream.Position = zip64Offset;
                stream.ReadExactly(z);
                if (BinaryPrimitives.ReadUInt32LittleEndian(z) == 0x06064b50)
                {
                    declared = BinaryPrimitives.ReadInt64LittleEndian(z[32..]);
                    cdOffset = BinaryPrimitives.ReadInt64LittleEndian(z[48..]);
                }
            }
        }

        if (declared > cap)
        {
            return cap + 1;
        }

        if (cdOffset < 0 || cdOffset >= length)
        {
            return 0;
        }

        var count = 0;
        Span<byte> header = stackalloc byte[46];
        var position = cdOffset;
        stream.Position = position;
        while (count <= cap && position + 46 <= length)
        {
            stream.ReadExactly(header);
            if (BinaryPrimitives.ReadUInt32LittleEndian(header) != 0x02014b50)
            {
                break;
            }

            count++;
            var variable = BinaryPrimitives.ReadUInt16LittleEndian(header[28..])
                           + BinaryPrimitives.ReadUInt16LittleEndian(header[30..])
                           + BinaryPrimitives.ReadUInt16LittleEndian(header[32..]);
            position += 46 + variable;
            stream.Position = position;
        }

        return count;
    }

    /// <summary>
    /// Case-insensitive name → entry index (OPC part names are case-insensitive). At most
    /// <see cref="MaxEntries"/> entries, guaranteed by <see cref="Open"/>.
    /// </summary>
    internal static Dictionary<string, ZipArchiveEntry> Index(ZipArchive zip)
    {
        var index = new Dictionary<string, ZipArchiveEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in zip.Entries)
        {
            index.TryAdd(entry.FullName.Replace('\\', '/').TrimStart('/'), entry);
        }

        return index;
    }

    /// <summary>Resolves "." and ".." segments of a zip-internal path.</summary>
    internal static string NormalizePath(string path)
    {
        var parts = new List<string>();
        foreach (var segment in path.Split('/'))
        {
            if (segment is "" or ".")
            {
                continue;
            }

            if (segment == "..")
            {
                if (parts.Count > 0)
                {
                    parts.RemoveAt(parts.Count - 1);
                }

                continue;
            }

            parts.Add(segment);
        }

        return string.Join('/', parts);
    }
}

/// <summary>XML reading helpers that never materialize a whole (possibly gigantic) text node.</summary>
internal static class BoundedXml
{
    internal const int ChunkSize = 4096;

    /// <summary>Settings for untrusted XML: no DTDs, no external resolution.</summary>
    internal static XmlReaderSettings Settings { get; } = new()
    {
        DtdProcessing = DtdProcessing.Ignore,
        XmlResolver = null,
        IgnoreComments = true,
        IgnoreProcessingInstructions = true,
        CheckCharacters = false,
        CloseInput = false,
    };

    internal static bool IsText(XmlNodeType type) =>
        type is XmlNodeType.Text or XmlNodeType.CDATA or XmlNodeType.Whitespace or XmlNodeType.SignificantWhitespace;

    /// <summary>
    /// Appends the current text node to <paramref name="sink"/> in chunks of at most
    /// <paramref name="buffer"/>.Length characters, stopping as soon as the sink is full. The rest of the node
    /// is skipped by the next <see cref="XmlReader.Read"/> without being buffered.
    /// </summary>
    internal static void AppendValue(XmlReader reader, TextAccumulator sink, char[] buffer)
    {
        if (!reader.CanReadValueChunk)
        {
            sink.Append(reader.Value);
            return;
        }

        int n;
        while (!sink.IsFull && (n = reader.ReadValueChunk(buffer, 0, buffer.Length)) > 0)
        {
            sink.Append(buffer.AsSpan(0, n));
        }
    }

    /// <summary>Appends at most <paramref name="maxChars"/> total characters of the current text node to
    /// <paramref name="target"/>.</summary>
    internal static void AppendValue(XmlReader reader, System.Text.StringBuilder target, int maxChars, char[] buffer)
    {
        if (target.Length >= maxChars)
        {
            return;
        }

        if (!reader.CanReadValueChunk)
        {
            var value = reader.Value;
            target.Append(value, 0, Math.Min(value.Length, maxChars - target.Length));
            return;
        }

        int n;
        while (target.Length < maxChars
               && (n = reader.ReadValueChunk(buffer, 0, Math.Min(buffer.Length, maxChars - target.Length))) > 0)
        {
            target.Append(buffer, 0, n);
        }
    }

    /// <summary>An attribute value, or null when absent or longer than <paramref name="maxChars"/>.</summary>
    internal static string? GetAttribute(XmlReader reader, string localName, int maxChars = 4096)
    {
        var value = reader.GetAttribute(localName);
        return value is not null && value.Length <= maxChars ? value : null;
    }
}
