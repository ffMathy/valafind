using System.Text;

namespace Valafind.Core.Extraction;

/// <summary>Shared file-access helpers for extractors.</summary>
internal static class ExtractionIO
{
    internal const int BufferSize = 64 * 1024;

    /// <summary>
    /// Opens a file for sequential reading while tolerating other processes that hold it open for
    /// writing or deletion (editors, mail clients, sync tools).
    /// </summary>
    internal static FileStream OpenRead(string path) => new(path, new FileStreamOptions
    {
        Mode = FileMode.Open,
        Access = FileAccess.Read,
        Share = FileShare.ReadWrite | FileShare.Delete,
        Options = FileOptions.SequentialScan,
        BufferSize = BufferSize,
    });

    /// <summary>Reads up to <paramref name="buffer"/>.Length bytes from the start of the file.</summary>
    internal static int ReadHead(string path, Span<byte> buffer)
    {
        using var stream = OpenRead(path);
        var total = 0;
        while (total < buffer.Length)
        {
            var read = stream.Read(buffer[total..]);
            if (read == 0)
            {
                break;
            }

            total += read;
        }

        return total;
    }

    internal static bool HasExtension(string path, params ReadOnlySpan<string> extensions)
    {
        var ext = Path.GetExtension(path.AsSpan());
        foreach (var candidate in extensions)
        {
            if (ext.Equals(candidate, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Windows-1252 without registering a process-wide encoding provider; Latin-1 if unavailable.</summary>
    internal static Encoding Windows1252 { get; } = GetCodePage(1252) ?? Encoding.Latin1;

    /// <summary>Resolves a Windows/ANSI code page; null if the runtime does not know it.</summary>
    internal static Encoding? GetCodePage(int codePage)
    {
        try
        {
            return CodePagesEncodingProvider.Instance.GetEncoding(codePage) ?? Encoding.GetEncoding(codePage);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException)
        {
            return null;
        }
    }
}

/// <summary>
/// Accumulates extracted text up to a character budget. Once the budget is reached further appends are
/// ignored and <see cref="IsFull"/> becomes true, so extractors can stop early. Never splits a surrogate pair.
/// </summary>
internal sealed class TextAccumulator
{
    private readonly StringBuilder _builder;
    private readonly int _maxChars;
    private bool _full;

    internal TextAccumulator(int maxChars)
    {
        _maxChars = Math.Max(0, maxChars);
        _builder = new StringBuilder(Math.Min(_maxChars, 16 * 1024));
        _full = _maxChars == 0;
    }

    internal bool IsFull => _full;

    internal int Length => _builder.Length;

    internal void Append(string? text)
    {
        if (text is not null)
        {
            Append(text.AsSpan());
        }
    }

    internal void Append(ReadOnlySpan<char> text)
    {
        if (_full || text.IsEmpty)
        {
            return;
        }

        var remaining = _maxChars - _builder.Length;
        if (text.Length >= remaining)
        {
            var cut = remaining;
            if (cut > 0 && cut < text.Length && char.IsHighSurrogate(text[cut - 1]))
            {
                cut--;
            }

            _builder.Append(text[..cut]);
            _full = true;
            return;
        }

        _builder.Append(text);
    }

    internal void Append(char c)
    {
        if (_full)
        {
            return;
        }

        _builder.Append(c);
        if (_builder.Length >= _maxChars)
        {
            _full = true;
        }
    }

    /// <summary>Starts a new line unless the text is empty or already ends with one.</summary>
    internal void EnsureLineBreak()
    {
        if (_builder.Length > 0 && _builder[^1] != '\n')
        {
            Append('\n');
        }
    }

    /// <summary>Separates blocks with one blank line (no-op at the very start).</summary>
    internal void EnsureBlankLine()
    {
        if (_builder.Length == 0)
        {
            return;
        }

        EnsureLineBreak();
        if (_builder.Length < 2 || _builder[^2] != '\n')
        {
            Append('\n');
        }
    }

    /// <summary>True when the last appended character is whitespace (or nothing was appended).</summary>
    internal bool EndsWithWhitespace => _builder.Length == 0 || char.IsWhiteSpace(_builder[^1]);

    public override string ToString()
    {
        var end = _builder.Length;
        while (end > 0 && char.IsWhiteSpace(_builder[end - 1]))
        {
            end--;
        }

        var start = 0;
        while (start < end && char.IsWhiteSpace(_builder[start]))
        {
            start++;
        }

        return _builder.ToString(start, end - start);
    }
}
