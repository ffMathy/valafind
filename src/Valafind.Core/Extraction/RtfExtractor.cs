using System.Text;
using Valafind.Core.Abstractions;

namespace Valafind.Core.Extraction;

/// <summary>
/// Extracts text from Rich Text Format files with a small control-word interpreter: skips font/colour/style
/// tables, pictures, objects, headers/footers and field instructions; honours <c>\par</c>, <c>\tab</c>,
/// <c>\'hh</c> escapes (decoded via <c>\ansicpgN</c>), <c>\uN</c> Unicode escapes with <c>\ucN</c> fallback
/// skipping, and <c>\binN</c> binary runs.
/// </summary>
public sealed class RtfExtractor : ITextExtractor
{
    private static readonly HashSet<string> SkippedDestinations = new(StringComparer.Ordinal)
    {
        "fonttbl", "colortbl", "stylesheet", "info", "pict", "object", "objdata", "header", "headerl", "headerr",
        "headerf", "footer", "footerl", "footerr", "footerf", "generator", "listtable", "listoverridetable",
        "rsidtbl", "latentstyles", "themedata", "colorschememapping", "datastore", "xmlnstbl", "mmathPr",
        "fldinst", "pgdsctbl", "revtbl", "filetbl", "bkmkstart", "bkmkend", "nonshppict", "shppict", "xe", "tc",
        "template", "private", "listtext", "pntext", "pntxta", "pntxtb", "falt", "panose", "sp", "sn", "sv",
        "userprops", "docvar", "wgrffmtfilter", "mhtmltag", "htmltag", "factoidname", "atnid", "atnauthor",
    };

    /// <inheritdoc />
    public bool CanHandle(string path) => ExtractionIO.HasExtension(path, ".rtf");

    /// <inheritdoc />
    public Task<ExtractedText?> ExtractAsync(string path, ExtractionLimits limits, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(limits);
        cancellationToken.ThrowIfCancellationRequested();
        using var stream = ExtractionIO.OpenRead(path);
        // RTF is 7-bit ASCII; Latin-1 maps any stray 8-bit byte to the char with the same value so it can be
        // re-decoded with the document code page.
        using var reader = new StreamReader(stream, Encoding.Latin1, detectEncodingFromByteOrderMarks: false,
            bufferSize: ExtractionIO.BufferSize);

        Span<char> prefix = stackalloc char[5];
        var read = reader.ReadBlock(prefix);
        if (read < 5 || !prefix.SequenceEqual("{\\rtf"))
        {
            return Task.FromResult<ExtractedText?>(null);
        }

        var sink = new TextAccumulator(limits.MaxChars);
        new Interpreter(reader, sink, cancellationToken).Run();
        return Task.FromResult<ExtractedText?>(new ExtractedText(sink.ToString(), FileKind.Document));
    }

    /// <summary>Converts RTF (positioned just after the leading "{\rtf") to text.</summary>
    internal static string ToText(string rtf, int maxChars)
    {
        if (!rtf.StartsWith("{\\rtf", StringComparison.Ordinal))
        {
            return string.Empty;
        }

        var sink = new TextAccumulator(maxChars);
        using var reader = new StringReader(rtf[5..]);
        new Interpreter(reader, sink, CancellationToken.None).Run();
        return sink.ToString();
    }

    private sealed class Interpreter(TextReader reader, TextAccumulator sink, CancellationToken cancellationToken)
    {
        private readonly Stack<(bool Skip, int Uc)> _groups = new();
        private readonly List<byte> _pendingBytes = [];
        private readonly StringBuilder _word = new();
        private Encoding _encoding = ExtractionIO.Windows1252;
        private bool _skip;
        private int _uc = 1;
        private int _fallbackToSkip;
        private long _consumed;

        // The leading "{" was consumed with the "{\rtf" prefix: start inside the document group.
        public void Run()
        {
            _groups.Push((false, 1));
            ReadControl('r'); // finish the "\rtf1" control word

            int c;
            while (!sink.IsFull && (c = Next()) >= 0)
            {
                switch (c)
                {
                    case '{':
                        FlushBytes();
                        _groups.Push((_skip, _uc));
                        _fallbackToSkip = 0;
                        break;
                    case '}':
                        FlushBytes();
                        if (_groups.Count > 0)
                        {
                            (_skip, _uc) = _groups.Pop();
                        }

                        _fallbackToSkip = 0;
                        break;
                    case '\\':
                        var next = Next();
                        if (next < 0)
                        {
                            return;
                        }

                        ReadControl((char)next);
                        break;
                    case '\r' or '\n':
                        break;
                    default:
                        EmitRaw((char)c);
                        break;
                }
            }

            FlushBytes();
        }

        private int Next()
        {
            if ((++_consumed & 0xFFFF) == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            return reader.Read();
        }

        private void ReadControl(char first)
        {
            if (!char.IsAsciiLetter(first))
            {
                ReadControlSymbol(first);
                return;
            }

            _word.Clear();
            _word.Append(first);
            while (char.IsAsciiLetter((char)reader.Peek()) && _word.Length < 32)
            {
                _word.Append((char)Next());
            }

            int? parameter = null;
            var negative = false;
            if (reader.Peek() == '-')
            {
                Next();
                negative = true;
            }

            if (char.IsAsciiDigit((char)reader.Peek()))
            {
                long value = 0;
                while (char.IsAsciiDigit((char)reader.Peek()))
                {
                    value = Math.Min(value * 10 + (Next() - '0'), int.MaxValue);
                }

                parameter = (int)(negative ? -value : value);
            }

            if (reader.Peek() == ' ')
            {
                Next(); // the delimiter space belongs to the control word
            }

            ApplyControlWord(_word.ToString(), parameter);
        }

        private void ReadControlSymbol(char symbol)
        {
            switch (symbol)
            {
                case '\'':
                    var hi = HexValue(Next());
                    var lo = HexValue(Next());
                    if (hi < 0 || lo < 0)
                    {
                        return;
                    }

                    if (_fallbackToSkip > 0)
                    {
                        _fallbackToSkip--;
                        return;
                    }

                    if (!_skip)
                    {
                        _pendingBytes.Add((byte)(hi * 16 + lo));
                    }

                    return;
                case '\\' or '{' or '}':
                    EmitRaw(symbol);
                    return;
                case '~':
                    Emit('\u00A0');
                    return;
                case '_':
                    Emit('-');
                    return;
                case '*':
                    _skip = true; // ignorable destination we do not interpret
                    return;
                case '\r' or '\n':
                    Emit('\n');
                    return;
                default:
                    return; // \- optional hyphen, \| \: formula/index symbols
            }
        }

        private void ApplyControlWord(string word, int? parameter)
        {
            if (SkippedDestinations.Contains(word))
            {
                _skip = true;
                return;
            }

            switch (word)
            {
                case "par" or "line" or "sect" or "page" or "row":
                    Emit('\n');
                    break;
                case "tab" or "cell":
                    Emit('\t');
                    break;
                case "emdash":
                    Emit('\u2014');
                    break;
                case "endash":
                    Emit('\u2013');
                    break;
                case "bullet":
                    Emit('\u2022');
                    break;
                case "lquote":
                    Emit('\u2018');
                    break;
                case "rquote":
                    Emit('\u2019');
                    break;
                case "ldblquote":
                    Emit('\u201C');
                    break;
                case "rdblquote":
                    Emit('\u201D');
                    break;
                case "emspace" or "enspace" or "qmspace":
                    Emit(' ');
                    break;
                case "uc" when parameter is { } uc:
                    _uc = Math.Clamp(uc, 0, 16);
                    break;
                case "u" when parameter is { } code:
                    Emit((char)(code < 0 ? code + 65536 : code));
                    _fallbackToSkip = _uc;
                    break;
                case "ansicpg" when parameter is { } cp:
                    _encoding = ExtractionIO.GetCodePage(cp) ?? ExtractionIO.Windows1252;
                    break;
                case "bin" when parameter is > 0:
                    FlushBytes();
                    for (var i = 0; i < parameter && reader.Read() >= 0; i++)
                    {
                    }

                    break;
            }
        }

        private void EmitRaw(char c)
        {
            if (_fallbackToSkip > 0)
            {
                _fallbackToSkip--;
                return;
            }

            if (_skip)
            {
                return;
            }

            if (c >= 0x80)
            {
                _pendingBytes.Add((byte)c); // raw 8-bit byte: decode with the document code page
                return;
            }

            FlushBytes();
            sink.Append(c);
        }

        private void Emit(char c)
        {
            if (_skip)
            {
                return;
            }

            FlushBytes();
            sink.Append(c);
        }

        private void FlushBytes()
        {
            if (_pendingBytes.Count == 0)
            {
                return;
            }

            var bytes = _pendingBytes.ToArray();
            _pendingBytes.Clear();
            sink.Append(_encoding.GetString(bytes));
        }

        private static int HexValue(int c) => c switch
        {
            >= '0' and <= '9' => c - '0',
            >= 'a' and <= 'f' => c - 'a' + 10,
            >= 'A' and <= 'F' => c - 'A' + 10,
            _ => -1,
        };
    }
}
