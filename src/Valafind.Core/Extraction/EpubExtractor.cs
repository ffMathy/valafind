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
public sealed class EpubExtractor : ITextExtractor
{
    /// <inheritdoc />
    public bool CanHandle(string path) => ExtractionIO.HasExtension(path, ".epub");

    /// <inheritdoc />
    public Task<ExtractedText?> ExtractAsync(string path, ExtractionLimits limits, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(limits);
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            using var stream = ExtractionIO.OpenRead(path);
            using var zip = new ZipArchive(stream, ZipArchiveMode.Read);

            var documents = GetSpineDocuments(zip);
            if (documents.Count == 0)
            {
                documents = zip.Entries
                    .Where(e => ExtractionIO.HasExtension(e.FullName, ".xhtml", ".html", ".htm"))
                    .OrderBy(e => e.FullName, StringComparer.Ordinal)
                    .ToList();
            }

            var sink = new TextAccumulator(limits.MaxChars);
            foreach (var entry in documents)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (sink.IsFull)
                {
                    break;
                }

                var rawBudget = (int)Math.Min((long)(limits.MaxChars - sink.Length) * 4 + 4096, Array.MaxLength / 2);
                var html = ReadEntry(entry, rawBudget);
                sink.EnsureBlankLine();
                HtmlText.AppendText(html, sink);
            }

            return Task.FromResult<ExtractedText?>(new ExtractedText(sink.ToString(), FileKind.Document));
        }
        catch (InvalidDataException)
        {
            return Task.FromResult<ExtractedText?>(null);
        }
    }

    private static List<ZipArchiveEntry> GetSpineDocuments(ZipArchive zip)
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
            using (var s = container.Open())
            using (var reader = XmlReader.Create(s, OpenDocumentExtractor.SafeXmlSettings))
            {
                while (reader.Read())
                {
                    if (reader.NodeType == XmlNodeType.Element && reader.LocalName == "rootfile")
                    {
                        opfPath = reader.GetAttribute("full-path");
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
            using (var s = opfEntry.Open())
            using (var reader = XmlReader.Create(s, OpenDocumentExtractor.SafeXmlSettings))
            {
                while (reader.Read())
                {
                    if (reader.NodeType != XmlNodeType.Element)
                    {
                        continue;
                    }

                    if (reader.LocalName == "item"
                        && reader.GetAttribute("id") is { } id
                        && reader.GetAttribute("href") is { } href)
                    {
                        manifest[id] = href;
                    }
                    else if (reader.LocalName == "itemref" && reader.GetAttribute("idref") is { } idref)
                    {
                        spine.Add(idref);
                    }
                }
            }

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

                var entryPath = NormalizePath(baseDir + Uri.UnescapeDataString(href));
                if (zip.GetEntry(entryPath) is { } entry)
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

        return result;
    }

    /// <summary>Resolves "." and ".." segments of a zip-internal path.</summary>
    private static string NormalizePath(string path)
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

    private static string ReadEntry(ZipArchiveEntry entry, int maxChars)
    {
        using var stream = entry.Open();
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
