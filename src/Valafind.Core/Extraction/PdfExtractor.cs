using UglyToad.PdfPig;
using UglyToad.PdfPig.DocumentLayoutAnalysis.TextExtractor;
using UglyToad.PdfPig.Exceptions;
using Valafind.Core.Abstractions;

namespace Valafind.Core.Extraction;

/// <summary>
/// Extracts PDF text page by page (in content order) with PdfPig. Pages that fail to parse are skipped;
/// encrypted documents yield null. Scanned/image-only PDFs produce empty text (still findable by name).
/// </summary>
public sealed class PdfExtractor : ITextExtractor
{
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
        using var stream = ExtractionIO.OpenRead(path);
        var options = new ParsingOptions
        {
            UseLenientParsing = true,
            SkipMissingFonts = true,
            ClipPaths = false,
        };

        PdfDocument document;
        try
        {
            document = PdfDocument.Open(stream, options);
        }
        catch (PdfDocumentEncryptedException)
        {
            return null;
        }

        using (document)
        {
            if (document.IsEncrypted && !CanReadAnyPage(document))
            {
                return null;
            }

            var sink = new TextAccumulator(limits.MaxChars);
            var failedPages = 0;
            for (var pageNumber = 1; pageNumber <= document.NumberOfPages && !sink.IsFull; pageNumber++)
            {
                cancellationToken.ThrowIfCancellationRequested();
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

            if (sink.Length == 0 && failedPages > 0 && failedPages == document.NumberOfPages)
            {
                return null; // nothing parsed at all: treat as corrupt
            }

            return new ExtractedText(sink.ToString(), FileKind.Pdf);
        }
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

    private static bool CanReadAnyPage(PdfDocument document)
    {
        try
        {
            return document.NumberOfPages > 0 && document.GetPage(1) is not null;
        }
        catch (PdfDocumentEncryptedException)
        {
            return false;
        }
    }
}
