using UglyToad.PdfPig;
using UglyToad.PdfPig.DocumentLayoutAnalysis.TextExtractor;
using UglyToad.PdfPig.Exceptions;
using Valafind.Core.Abstractions;

namespace Valafind.Core.Extraction;

/// <summary>
/// Extracts PDF text page by page (in content order) with PdfPig. Pages that fail to parse are skipped;
/// encrypted documents yield null. Scanned/image-only PDFs produce empty text (still findable by name).
/// </summary>
/// <remarks>
/// PdfPig decodes whole streams into memory with no cap, so every stream is checked by
/// <see cref="PdfStreamGuard"/> before PdfPig sees it. If the file holds a decompression bomb, it is indexed by
/// name only (empty text). A page whose streams decode to more than
/// <see cref="PdfStreamGuard.MaxPageDecodedBytes"/> is skipped. At most <see cref="MaxPages"/> pages are read,
/// and reading stops once the character budget is full.
/// </remarks>
public sealed class PdfExtractor : ITextExtractor
{
    /// <summary>Maximum pages parsed per document.</summary>
    internal const int MaxPages = 2_000;

    /// <inheritdoc />
    public bool CanHandle(string path) => ExtractionIO.HasExtension(path, ".pdf");

    /// <inheritdoc />
    public Task<ExtractedText?> ExtractAsync(string path, ExtractionLimits limits, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(limits);
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(Extract(path, limits, cancellationToken));
    }

    private static ExtractedText? Extract(string path, ExtractionLimits limits, CancellationToken cancellationToken)
    {
        byte[] bytes;
        using (var stream = ExtractionIO.OpenRead(path))
        {
            if (stream.Length > limits.MaxFileBytes)
            {
                return new ExtractedText(string.Empty, FileKind.Pdf);
            }

            bytes = new byte[stream.Length];
            stream.ReadExactly(bytes);
        }

        if (PdfStreamGuard.ScanFile(bytes, cancellationToken) is { } reason)
        {
            throw new ExtractionLimitException("PDF rejected: " + reason);
        }

        var options = new ParsingOptions
        {
            UseLenientParsing = true,
            SkipMissingFonts = true,
            ClipPaths = false,
        };

        PdfDocument document;
        try
        {
            document = PdfDocument.Open(bytes, options);
        }
        catch (PdfDocumentEncryptedException)
        {
            return null;
        }

        using (document)
        {
            // Encrypted documents that PdfPig cannot decrypt fail in GetPage with PdfDocumentEncryptedException,
            // which returns null below; no unguarded probe page is parsed up front.
            var guard = new PdfStreamGuard.DocumentState(document);
            var pageCount = Math.Min(document.NumberOfPages, MaxPages);
            var pages = PdfStreamGuard.GetPages(document, MaxPages);
            var perPage = pages.Count == document.NumberOfPages || (document.NumberOfPages > MaxPages && pages.Count > MaxPages);
            if (!perPage && !CheckAllPages(guard, pages, cancellationToken))
            {
                // Our view of the page tree does not match PdfPig's numbering, so an oversized page cannot be
                // skipped individually.
                throw new ExtractionLimitException("PDF page exceeds the decoded-size limit.");
            }

            var sink = new TextAccumulator(limits.MaxChars);
            var failedPages = 0;
            for (var pageNumber = 1; pageNumber <= pageCount && !sink.IsFull; pageNumber++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (perPage)
                {
                    var (pageDictionary, resources) = pages[pageNumber - 1];
                    if (PdfStreamGuard.MeasurePage(guard, pageDictionary, resources, cancellationToken) is null or > PdfStreamGuard.MaxPageDecodedBytes)
                    {
                        continue; // PdfPig would have to decode too much for this page: skip it
                    }
                }

                string pageText;
                try
                {
                    var page = document.GetPage(pageNumber);
                    pageText = GetPageText(page);
                }
                catch (PdfDocumentEncryptedException)
                {
                    return null;
                }
                catch (Exception ex) when (ex is not OperationCanceledException and not OutOfMemoryException)
                {
                    // One damaged page (bad font, broken content stream) should not lose the whole document.
                    failedPages++;
                    continue;
                }

                if (!string.IsNullOrWhiteSpace(pageText))
                {
                    sink.EnsureBlankLine();
                    sink.Append(pageText.Trim());
                }
            }

            if (sink.Length == 0 && failedPages > 0 && failedPages == pageCount)
            {
                return null; // nothing parsed at all: treat as corrupt
            }

            return new ExtractedText(sink.ToString(), FileKind.Pdf);
        }
    }

    private static bool CheckAllPages(PdfStreamGuard.DocumentState guard, List<(UglyToad.PdfPig.Tokens.DictionaryToken Page, UglyToad.PdfPig.Tokens.IToken? Resources)> pages, CancellationToken cancellationToken)
    {
        foreach (var (page, resources) in pages)
        {
            if (PdfStreamGuard.MeasurePage(guard, page, resources, cancellationToken) is null or > PdfStreamGuard.MaxPageDecodedBytes)
            {
                return false;
            }
        }

        return true;
    }

    private static string GetPageText(UglyToad.PdfPig.Content.Page page)
    {
        try
        {
            return ContentOrderTextExtractor.GetText(page);
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not OutOfMemoryException)
        {
            // Layout analysis can fail on unusual glyph data; raw page text is a reasonable fallback.
            return page.Text;
        }
    }
}
