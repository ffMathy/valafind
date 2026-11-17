using System.Text.Json;
using Valafind.Core.Abstractions;

namespace Valafind.Core.Extraction;

/// <summary>
/// Extracts Jupyter notebooks (.ipynb, nbformat 3 and 4): markdown, code and raw cell sources in order,
/// separated by blank lines. Cell outputs (often large, binary or noisy) are skipped.
/// </summary>
public sealed class NotebookExtractor : ITextExtractor
{
    private static readonly JsonDocumentOptions Options = new()
    {
        AllowTrailingCommas = true,
        CommentHandling = JsonCommentHandling.Skip,
        MaxDepth = 256,
    };

    /// <inheritdoc />
    public bool CanHandle(string path) => ExtractionIO.HasExtension(path, ".ipynb");

    /// <inheritdoc />
    public async Task<ExtractedText?> ExtractAsync(string path, ExtractionLimits limits, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(limits);
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            var stream = ExtractionIO.OpenRead(path);
            await using (stream.ConfigureAwait(false))
            {
                using var document = await JsonDocument.ParseAsync(stream, Options, cancellationToken).ConfigureAwait(false);
                var root = document.RootElement;
                if (root.ValueKind != JsonValueKind.Object)
                {
                    return null;
                }

                var sink = new TextAccumulator(limits.MaxChars);
                if (root.TryGetProperty("cells", out var cells))
                {
                    AppendCells(cells, "source", sink, cancellationToken);
                }
                else if (root.TryGetProperty("worksheets", out var worksheets) && worksheets.ValueKind == JsonValueKind.Array)
                {
                    // nbformat 3: worksheets[].cells[], code in "input", markdown in "source".
                    foreach (var sheet in worksheets.EnumerateArray())
                    {
                        if (sheet.ValueKind == JsonValueKind.Object && sheet.TryGetProperty("cells", out var sheetCells))
                        {
                            AppendCells(sheetCells, "input", sink, cancellationToken);
                        }
                    }
                }
                else
                {
                    return null;
                }

                return new ExtractedText(sink.ToString(), FileKind.Notebook);
            }
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static void AppendCells(JsonElement cells, string legacyCodeProperty, TextAccumulator sink, CancellationToken cancellationToken)
    {
        if (cells.ValueKind != JsonValueKind.Array)
        {
            return;
        }

        foreach (var cell in cells.EnumerateArray())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (sink.IsFull)
            {
                return;
            }

            if (cell.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            if (!cell.TryGetProperty("source", out var source) && !cell.TryGetProperty(legacyCodeProperty, out source))
            {
                continue;
            }

            sink.EnsureBlankLine();
            AppendSource(source, sink);
        }
    }

    private static void AppendSource(JsonElement source, TextAccumulator sink)
    {
        switch (source.ValueKind)
        {
            case JsonValueKind.String:
                sink.Append(source.GetString());
                break;
            case JsonValueKind.Array:
                foreach (var line in source.EnumerateArray())
                {
                    if (line.ValueKind == JsonValueKind.String)
                    {
                        sink.Append(line.GetString());
                    }
                }

                break;
        }
    }
}
