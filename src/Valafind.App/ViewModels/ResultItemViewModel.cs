using CommunityToolkit.Mvvm.ComponentModel;
using Valafind.Core.Abstractions;
using Valafind.App.Localization;

namespace Valafind.App.ViewModels;

/// <summary>Context used to format results (flavor, home directory, clock).</summary>
internal sealed record ResultDisplayContext(UiFlavor Flavor, string? Home, DateTimeOffset Now, TimeZoneInfo? Zone = null)
{
    public StringComparison PathComparison =>
        Flavor == UiFlavor.Linux ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
}

/// <summary>One search hit as shown in the launcher.</summary>
public sealed partial class ResultItemViewModel : ObservableObject
{
    internal ResultItemViewModel(SearchResult result, ResultDisplayContext context)
    {
        Result = result;
        FileName = string.IsNullOrEmpty(result.FileName) ? System.IO.Path.GetFileName(result.Path) : result.FileName;
        var directory = string.IsNullOrEmpty(result.Directory) ? System.IO.Path.GetDirectoryName(result.Path) ?? string.Empty : result.Directory;
        FullDirectory = directory;
        DisplayDirectory = context.Flavor == UiFlavor.Windows
            ? directory
            : DisplayFormat.ShortenHome(directory, context.Home, context.PathComparison);
        Breadcrumb = DisplayFormat.Breadcrumb(directory, context.Home, context.PathComparison);
        DateText = DisplayFormat.RelativeDate(result.ModifiedUtc, context.Now, context.Zone);
        ScoreText = DisplayFormat.Score(result.Score);
        ScoreFraction = Math.Clamp(result.Score, 0f, 1f);
        ExtensionLabel = DisplayFormat.ExtensionLabel(result.Path);
        SizeText = DisplayFormat.FileSize(result.Size);
        (Snippet, Highlights) = DisplayFormat.CollapseWhitespace(result.Snippet ?? string.Empty, result.Highlights);
        GroupName = FileKindGroups.GroupName(result.Kind);
    }

    /// <summary>The launcher that owns this row (lets row buttons reach the launcher commands).</summary>
    public LauncherViewModel? Launcher { get; internal set; }

    /// <summary>The underlying search result (replaced when the semantic explanation arrives).</summary>
    public SearchResult Result { get; private set; }

    /// <summary>Absolute path of the file.</summary>
    public string Path => Result.Path;

    /// <summary>File name with extension.</summary>
    public string FileName { get; }

    /// <summary><see cref="FileName"/> with invisible break opportunities after '_', '-' and '.', so a long name
    /// wraps at natural points in the details panes ("…_2024." / "pdf") instead of mid-word.</summary>
    public string WrappableFileName => DisplayFormat.WithBreakOpportunities(FileName);

    /// <summary>Containing directory, unabbreviated.</summary>
    public string FullDirectory { get; }

    /// <summary>Containing directory; the home directory is shown as "~" on macOS/Linux.</summary>
    public string DisplayDirectory { get; }

    /// <summary>Last two folders, e.g. "Documents › Bolig".</summary>
    public string Breadcrumb { get; }

    /// <summary>File category.</summary>
    public FileKind Kind => Result.Kind;

    /// <summary>Relative modification date ("Today 09:12", "Yesterday", "12 Sep 2025").</summary>
    public string DateText { get; }

    /// <summary>"Modified 12 Sep 2025".</summary>
    public string ModifiedText => Loc.Format(Strings.ModifiedOn, DateText);

    /// <summary>"C:\Users\…\Bolig · 12 Sep 2025".</summary>
    public string Caption => DisplayDirectory + " · " + DateText;

    /// <summary>"Documents › Bolig · 12 Sep 2025" (macOS list).</summary>
    public string BreadcrumbCaption => Breadcrumb + " · " + DateText;

    /// <summary>"C:\Users\…\Bolig · 12 Sep 2025 · “…snippet…”" (compact Windows row). The snippet already carries
    /// "…" where it was cut (Core Snippet.Build), so only quotes are added here.</summary>
    public string CaptionWithSnippet => HasSnippet ? Caption + " · “" + Snippet + "”" : Caption;

    /// <summary>"PDF · 412 KB · Modified 3 Mar 2024" (macOS preview).</summary>
    public string MetaText => ExtensionLabel + " · " + SizeText + " · " + ModifiedText;

    /// <summary>Similarity formatted with two decimals.</summary>
    public string ScoreText { get; }

    /// <summary>Similarity clamped to 0..1 for score bars.</summary>
    public double ScoreFraction { get; }

    /// <summary>Upper-case extension ("PDF").</summary>
    public string ExtensionLabel { get; }

    /// <summary>Human-readable size.</summary>
    public string SizeText { get; }

    /// <summary>Best-matching passage, whitespace-collapsed. Empty for files matched by name only (images, media).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSnippet), nameof(CaptionWithSnippet))]
    public partial string Snippet { get; private set; }

    /// <summary>Spans of <see cref="Snippet"/> that explain the match.</summary>
    [ObservableProperty]
    public partial IReadOnlyList<TextSpan> Highlights { get; private set; }

    /// <summary>True when there is a passage to show.</summary>
    public bool HasSnippet => Snippet.Length > 0;

    /// <summary>Takes over the snippet and highlights of the explained result (semantic second pass) in place, so the
    /// list is not rebuilt and the selection stays.</summary>
    internal void ApplyExplanation(SearchResult explained)
    {
        ArgumentNullException.ThrowIfNull(explained);
        Result = explained;
        var (snippet, highlights) = DisplayFormat.CollapseWhitespace(explained.Snippet ?? string.Empty, explained.Highlights);
        // HighlightedTextBlock re-renders on each change and clamps spans, so the brief text/spans mismatch is harmless.
        Snippet = snippet;
        Highlights = highlights;
    }

    /// <summary>Category section name ("Documents", "Mail", …).</summary>
    public string GroupName { get; }

    /// <summary>Section header shown above this row (macOS design), or null.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasGroupHeader))]
    public partial string? GroupHeader { get; set; }

    /// <summary>True when <see cref="GroupHeader"/> is set.</summary>
    public bool HasGroupHeader => GroupHeader is not null;

    /// <summary>True for the keyboard-selected row.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsCompact))]
    public partial bool IsSelected { get; set; }

    /// <summary>True when the selected row shows its expanded "why it matched" card.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsCompact))]
    public partial bool IsExpanded { get; set; }

    /// <summary>Inverse of <see cref="IsExpanded"/>.</summary>
    public bool IsCompact => !IsExpanded;
}
