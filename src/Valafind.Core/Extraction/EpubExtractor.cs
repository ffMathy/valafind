using System.IO.Compression;
using System.Text;
using System.Xml;
using Valafind.Core.Abstractions;

namespace Valafind.Core.Extraction;

/// <summary>
/// Extracts the text of EPUB e-books: the XHTML documents listed in the package spine (reading order),
/// converted to plain text. Falls back to all (X)HTML entries in name order when the package metadata is
/// missing or broken.
/// </summary>
/// <remarks>
/// Bounded for hostile files: zip limits from <see cref="SafeZip"/>, at most <see cref="MaxManifestItems"/>
/// manifest items and <see cref="MaxDocuments"/> distinct spine documents (duplicates are read once), and each
/// document is read only up to the remaining character budget.
/// </remarks>
public sealed class EpubExtractor : ITextExtractor
{
    /// <summary>Maximum distinct content documents opened per book.</summary>
    internal const int MaxDocuments = 2_000;

    /// <summary>Maximum manifest items considered.</summary>
    internal const int MaxManifestItems = 20_000;

    /// <inheritdoc />
    public bool CanHandle(string path) => ExtractionIO.HasExtension(path, ".epub");

    /// <inheritdoc />
    public Task<ExtractedText?> ExtractAsync(string path, ExtractionLimits limits, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(limits);
        cancellationToken.ThrowIfCancellationRequested();
        using var stream = ExtractionIO.OpenRead(path);
        var sink = new TextAccumulator(limits.MaxChars);
        try
        {
            using var zip = SafeZip.Open(stream);
            var budget = SafeZip.NewFileBudget();
            var documents = GetSpineDocuments(zip, budget, cancellationToken);
            if (documents.Count == 0)
            {
                documents = zip.Entries
                    .Where(e => ExtractionIO.HasExtension(e.FullName, ".xhtml", ".html", ".htm"))
                    .OrderBy(e => e.FullName, StringComparer.Ordinal)
                    .Take(MaxDocuments)
                    .ToList();
            }

            foreach (var entry in documents)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (sink.IsFull)
                {
                    break;
                }

                var rawBudget = (int)Math.Min((long)(limits.MaxChars - sink.Length) * 4 + 4096, Array.MaxLength / 2);
                string html;
                try
                {
                    html = ReadEntry(entry, rawBudget, budget);
                }
                catch (ExtractionLimitException ex) when (!ex.FileBudgetExhausted)
                {
                    continue; // bomb-like chapter: skip it
                }

                sink.EnsureBlankLine();
                HtmlText.AppendText(html, sink);
            }
        }
        catch (InvalidDataException) when (sink.Length == 0)
        {
            return Task.FromResult<ExtractedText?>(null);
        }
        catch (InvalidDataException)
        {
            // Damaged later entry: keep the chapters read so far.
        }
        catch (ExtractionLimitException)
        {
            // Per-file decompression budget used up (or too many entries): keep what was read within it.
        }

        return Task.FromResult<ExtractedText?>(new ExtractedText(sink.ToString(), FileKind.Document));
    }

    private static List<ZipArchiveEntry> GetSpineDocuments(ZipArchive zip, DecompressionBudget budget, CancellationToken cancellationToken)
    {
        var result = new List<ZipArchiveEntry>();
        try
        {
            var container = zip.GetEntry("META-INF/container.xml");
            if (container is null)
            {
                return result;
            }

            string? opfPath = null;
            using (var s = SafeZip.OpenEntry(container, budget))
            using (var reader = XmlReader.Create(s, BoundedXml.Settings))
            {
                while (reader.Read())
                {
                    if (reader.NodeType == XmlNodeType.Element && reader.LocalName == "rootfile")
                    {
                        opfPath = BoundedXml.GetAttribute(reader, "full-path", 2048);
                        break;
                    }
                }
            }

            if (string.IsNullOrEmpty(opfPath) || zip.GetEntry(opfPath) is not { } opfEntry)
            {
                return result;
            }

            var baseDir = opfPath.Contains('/', StringComparison.Ordinal) ? opfPath[..(opfPath.LastIndexOf('/') + 1)] : string.Empty;
            var manifest = new Dictionary<string, string>(StringComparer.Ordinal);
            var spine = new List<string>();
            var spineSeen = new HashSet<string>(StringComparer.Ordinal);
            using (var s = SafeZip.OpenEntry(opfEntry, budget))
            using (var reader = XmlReader.Create(s, BoundedXml.Settings))
            {
                var nodes = 0;
                while (reader.Read() && spine.Count < MaxDocuments)
                {
                    if ((++nodes & 0xFFF) == 0)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                    }

                    if (reader.NodeType != XmlNodeType.Element)
                    {
                        continue;
                    }

                    if (reader.LocalName == "item"
                        && manifest.Count < MaxManifestItems
                        && BoundedXml.GetAttribute(reader, "id", 256) is { } id
                        && BoundedXml.GetAttribute(reader, "href", 2048) is { } href)
                    {
                        manifest[id] = href;
                    }
                    else if (reader.LocalName == "itemref"
                             && BoundedXml.GetAttribute(reader, "idref", 256) is { } idref
                             && spineSeen.Add(idref))
                    {
                        spine.Add(idref);
                    }
                }
            }

            var added = new HashSet<ZipArchiveEntry>();
            foreach (var idref in spine)
            {
                if (!manifest.TryGetValue(idref, out var href))
                {
                    continue;
                }

                var hashAt = href.IndexOf('#', StringComparison.Ordinal);
                if (hashAt >= 0)
                {
                    href = href[..hashAt];
                }

                var entryPath = SafeZip.NormalizePath(baseDir + Uri.UnescapeDataString(href));
                if (zip.GetEntry(entryPath) is { } entry && added.Add(entry))
                {
                    result.Add(entry);
                }
            }
        }
        catch (XmlException)
        {
            result.Clear();
        }
        catch (UriFormatException)
        {
            result.Clear();
        }
        catch (ExtractionLimitException ex) when (!ex.FileBudgetExhausted)
        {
            result.Clear(); // bomb-like package document: fall back to entry names
        }

        return result;
    }

    private static string ReadEntry(ZipArchiveEntry entry, int maxChars, DecompressionBudget budget)
    {
        using var stream = SafeZip.OpenEntry(entry, budget);
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        var buffer = new char[Math.Min(maxChars, 64 * 1024)];
        var sb = new StringBuilder();
        while (sb.Length < maxChars)
        {
            var n = reader.Read(buffer, 0, Math.Min(buffer.Length, maxChars - sb.Length));
            if (n == 0)
            {
                break;
            }

            sb.Append(buffer, 0, n);
        }

        return sb.ToString();
    }
}
