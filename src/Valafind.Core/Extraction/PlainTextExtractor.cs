using System.Buffers;
using Valafind.Core.Abstractions;

namespace Valafind.Core.Extraction;

/// <summary>
/// Reads text and source-code files (plus CSV/TSV and extension-less files that sniff as text). Detects the
/// encoding from the BOM, UTF-8 validity or BOM-less UTF-16 patterns, falling back to Windows-1252. HTML is
/// converted to plain text. Reading is streaming and stops at <see cref="ExtractionLimits.MaxChars"/>.
/// </summary>
public sealed class PlainTextExtractor : ITextExtractor
{
    private const int SniffBytes = 64 * 1024;

    /// <summary>Raw-markup budget multiplier for HTML: markup is typically several times the visible text.</summary>
    private const int HtmlRawFactor = 4;

    /// <inheritdoc />
    public bool CanHandle(string path)
    {
        var kind = FileKinds.Classify(path);
        if (kind is FileKind.Text or FileKind.Code || IsDelimitedText(path))
        {
            return true;
        }

        if (kind != FileKind.Other)
        {
            return false;
        }

        try
        {
            Span<byte> head = stackalloc byte[TextSniffer.RecommendedHeadSize];
            var read = ExtractionIO.ReadHead(path, head);
            return TextSniffer.LooksLikeText(head[..read]);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException
                                       or NotSupportedException)
        {
            return false;
        }
    }

    /// <inheritdoc />
    public async Task<ExtractedText?> ExtractAsync(string path, ExtractionLimits limits, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(limits);
        cancellationToken.ThrowIfCancellationRequested();

        var kind = IsDelimitedText(path) ? FileKind.Spreadsheet : FileKinds.Classify(path);
        var isHtml = ExtractionIO.HasExtension(path, ".html", ".htm", ".xhtml");

        var stream = ExtractionIO.OpenRead(path);
        await using (stream.ConfigureAwait(false))
        {
            var head = ArrayPool<byte>.Shared.Rent(SniffBytes);
            System.Text.Encoding encoding;
            int bomLength;
            try
            {
                var read = await ReadFullyAsync(stream, head.AsMemory(0, SniffBytes), cancellationToken).ConfigureAwait(false);
                encoding = TextSniffer.DetectEncoding(head.AsSpan(0, read), headIsWholeFile: read < SniffBytes, out bomLength);
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(head);
            }

            stream.Position = bomLength;
            using var reader = new StreamReader(stream, encoding, detectEncodingFromByteOrderMarks: false,
                bufferSize: ExtractionIO.BufferSize, leaveOpen: true);

            var maxChars = Math.Max(0, limits.MaxChars);
            var budget = isHtml ? (int)Math.Min((long)maxChars * HtmlRawFactor, Array.MaxLength / 2) : maxChars;
            var sink = new TextAccumulator(budget);
            var buffer = ArrayPool<char>.Shared.Rent(16 * 1024);
            try
            {
                while (!sink.IsFull)
                {
                    var n = await reader.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false);
                    if (n == 0)
                    {
                        break;
                    }

                    // NULs only appear in damaged or mis-detected files; they would confuse downstream consumers.
                    buffer.AsSpan(0, n).Replace('\0', ' ');
                    sink.Append(buffer.AsSpan(0, n));
                }
            }
            finally
            {
                ArrayPool<char>.Shared.Return(buffer);
            }

            var text = sink.ToString();
            if (isHtml)
            {
                cancellationToken.ThrowIfCancellationRequested();
                text = HtmlText.ToText(text, maxChars);
            }

            return new ExtractedText(text, kind);
        }
    }

    private static bool IsDelimitedText(string path) => ExtractionIO.HasExtension(path, ".csv", ".tsv");

    private static async ValueTask<int> ReadFullyAsync(Stream stream, Memory<byte> buffer, CancellationToken cancellationToken)
    {
        var total = 0;
        while (total < buffer.Length)
        {
            var n = await stream.ReadAsync(buffer[total..], cancellationToken).ConfigureAwait(false);
            if (n == 0)
            {
                break;
            }

            total += n;
        }

        return total;
    }
}
