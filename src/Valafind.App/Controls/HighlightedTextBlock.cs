using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Media;
using CoreTextSpan = Valafind.Core.Abstractions.TextSpan;

namespace Valafind.App.Controls;

/// <summary>
/// A <see cref="TextBlock"/> that renders <see cref="Source"/> as runs, emphasising the <see cref="Highlights"/> spans
/// ("why it matched") with a highlight background, foreground, weight and optional underline.
/// </summary>
public class HighlightedTextBlock : TextBlock
{
    /// <summary>Defines <see cref="Source"/>.</summary>
    public static readonly StyledProperty<string?> SourceProperty =
        AvaloniaProperty.Register<HighlightedTextBlock, string?>(nameof(Source));

    /// <summary>Defines <see cref="Highlights"/>.</summary>
    public static readonly StyledProperty<IReadOnlyList<CoreTextSpan>?> HighlightsProperty =
        AvaloniaProperty.Register<HighlightedTextBlock, IReadOnlyList<CoreTextSpan>?>(nameof(Highlights));

    /// <summary>Defines <see cref="Prefix"/>.</summary>
    public static readonly StyledProperty<string?> PrefixProperty =
        AvaloniaProperty.Register<HighlightedTextBlock, string?>(nameof(Prefix));

    /// <summary>Defines <see cref="Suffix"/>.</summary>
    public static readonly StyledProperty<string?> SuffixProperty =
        AvaloniaProperty.Register<HighlightedTextBlock, string?>(nameof(Suffix));

    /// <summary>Defines <see cref="HighlightBackground"/>.</summary>
    public static readonly StyledProperty<IBrush?> HighlightBackgroundProperty =
        AvaloniaProperty.Register<HighlightedTextBlock, IBrush?>(nameof(HighlightBackground));

    /// <summary>Defines <see cref="HighlightForeground"/>.</summary>
    public static readonly StyledProperty<IBrush?> HighlightForegroundProperty =
        AvaloniaProperty.Register<HighlightedTextBlock, IBrush?>(nameof(HighlightForeground));

    /// <summary>Defines <see cref="HighlightFontWeight"/>.</summary>
    public static readonly StyledProperty<FontWeight?> HighlightFontWeightProperty =
        AvaloniaProperty.Register<HighlightedTextBlock, FontWeight?>(nameof(HighlightFontWeight));

    /// <summary>Defines <see cref="HighlightUnderline"/>.</summary>
    public static readonly StyledProperty<IBrush?> HighlightUnderlineProperty =
        AvaloniaProperty.Register<HighlightedTextBlock, IBrush?>(nameof(HighlightUnderline));

    static HighlightedTextBlock()
    {
        foreach (var p in new AvaloniaProperty[]
                 {
                     SourceProperty, HighlightsProperty, PrefixProperty, SuffixProperty, HighlightBackgroundProperty,
                     HighlightForegroundProperty, HighlightFontWeightProperty, HighlightUnderlineProperty,
                 })
        {
            p.Changed.AddClassHandler<HighlightedTextBlock>((t, _) => t.RebuildInlines());
        }
    }

    /// <inheritdoc />
    protected override Type StyleKeyOverride => typeof(TextBlock);

    /// <summary>Plain text to show.</summary>
    public string? Source
    {
        get => GetValue(SourceProperty);
        set => SetValue(SourceProperty, value);
    }

    /// <summary>Spans of <see cref="Source"/> to emphasise. Out-of-range and overlapping spans are tolerated.</summary>
    public IReadOnlyList<CoreTextSpan>? Highlights
    {
        get => GetValue(HighlightsProperty);
        set => SetValue(HighlightsProperty, value);
    }

    /// <summary>Unhighlighted text placed before <see cref="Source"/> (e.g. an opening quote).</summary>
    public string? Prefix
    {
        get => GetValue(PrefixProperty);
        set => SetValue(PrefixProperty, value);
    }

    /// <summary>Unhighlighted text placed after <see cref="Source"/>.</summary>
    public string? Suffix
    {
        get => GetValue(SuffixProperty);
        set => SetValue(SuffixProperty, value);
    }

    /// <summary>Background of highlighted runs.</summary>
    public IBrush? HighlightBackground
    {
        get => GetValue(HighlightBackgroundProperty);
        set => SetValue(HighlightBackgroundProperty, value);
    }

    /// <summary>Foreground of highlighted runs (null = inherit).</summary>
    public IBrush? HighlightForeground
    {
        get => GetValue(HighlightForegroundProperty);
        set => SetValue(HighlightForegroundProperty, value);
    }

    /// <summary>Font weight of highlighted runs (null = inherit).</summary>
    public FontWeight? HighlightFontWeight
    {
        get => GetValue(HighlightFontWeightProperty);
        set => SetValue(HighlightFontWeightProperty, value);
    }

    /// <summary>Underline colour of highlighted runs (null = no underline).</summary>
    public IBrush? HighlightUnderline
    {
        get => GetValue(HighlightUnderlineProperty);
        set => SetValue(HighlightUnderlineProperty, value);
    }

    /// <summary>Splits <paramref name="text"/> into (segment, highlighted) pieces. Exposed for tests.</summary>
    internal static IReadOnlyList<(string Text, bool Highlighted)> Segment(string text, IReadOnlyList<CoreTextSpan>? highlights)
    {
        var result = new List<(string, bool)>();
        if (string.IsNullOrEmpty(text))
            return result;
        var marks = new bool[text.Length];
        if (highlights is not null)
        {
            foreach (var h in highlights)
            {
                var start = Math.Clamp(h.Start, 0, text.Length);
                var end = Math.Clamp(h.Start + Math.Max(0, h.Length), start, text.Length);
                for (var i = start; i < end; i++)
                    marks[i] = true;
            }
        }
        var segStart = 0;
        for (var i = 1; i <= text.Length; i++)
        {
            if (i == text.Length || marks[i] != marks[segStart])
            {
                // Snippets keep line breaks as sentence boundaries; on screen they read as plain spaces (same length,
                // so highlight offsets stay valid).
                result.Add((text[segStart..i].Replace('\n', ' '), marks[segStart]));
                segStart = i;
            }
        }
        return result;
    }

    private void RebuildInlines()
    {
        var inlines = Inlines;
        if (inlines is null)
        {
            inlines = [];
            Inlines = inlines;
        }
        inlines.Clear();

        if (!string.IsNullOrEmpty(Prefix))
            inlines.Add(new Run(Prefix));

        foreach (var (segment, highlighted) in Segment(Source ?? string.Empty, Highlights))
        {
            var run = new Run(segment);
            if (highlighted)
            {
                if (HighlightBackground is { } bg)
                    run.Background = bg;
                if (HighlightForeground is { } fg)
                    run.Foreground = fg;
                if (HighlightFontWeight is { } weight)
                    run.FontWeight = weight;
                if (HighlightUnderline is { } underline)
                {
                    run.TextDecorations =
                    [
                        new TextDecoration
                        {
                            Location = TextDecorationLocation.Underline,
                            Stroke = underline,
                            StrokeThickness = 1.5,
                            StrokeThicknessUnit = TextDecorationUnit.Pixel,
                            StrokeOffset = 2,
                            StrokeOffsetUnit = TextDecorationUnit.Pixel,
                        },
                    ];
                }
            }
            inlines.Add(run);
        }

        if (!string.IsNullOrEmpty(Suffix))
            inlines.Add(new Run(Suffix));
    }
}
