using CommunityToolkit.Mvvm.ComponentModel;
using Valafind.Core.Abstractions;

namespace Valafind.App.ViewModels;

/// <summary>A file-type filter chip ("All", "Documents", "PDFs", …).</summary>
public sealed partial class FilterChipViewModel : ObservableObject
{
    internal FilterChipViewModel(string label, IReadOnlySet<FileKind>? kinds)
    {
        Label = label;
        Kinds = kinds;
    }

    /// <summary>Chip caption.</summary>
    public string Label { get; }

    /// <summary>Kinds matched by the chip; null means every kind ("All").</summary>
    public IReadOnlySet<FileKind>? Kinds { get; }

    /// <summary>Activates this chip (wired by the owning launcher).</summary>
    public System.Windows.Input.ICommand? SelectCommand { get; internal set; }

    /// <summary>True for the active chip.</summary>
    [ObservableProperty]
    public partial bool IsSelected { get; set; }

    /// <summary>The standard chip set, in display order.</summary>
    internal static IReadOnlyList<FilterChipViewModel> CreateDefaults() =>
    [
        new("All", null),
        new("Documents", new HashSet<FileKind> { FileKind.Document, FileKind.Pdf, FileKind.Text, FileKind.Notebook }),
        new("PDFs", new HashSet<FileKind> { FileKind.Pdf }),
        new("Spreadsheets", new HashSet<FileKind> { FileKind.Spreadsheet }),
        new("Slides", new HashSet<FileKind> { FileKind.Presentation }),
        new("Mail", new HashSet<FileKind> { FileKind.Mail }),
        new("Code", new HashSet<FileKind> { FileKind.Code, FileKind.Notebook }),
        new("Images", new HashSet<FileKind> { FileKind.Image }),
    ];
}

/// <summary>Modification-time filter ("Any time", "Past week", …).</summary>
public sealed class TimeRangeOption
{
    internal TimeRangeOption(string label, TimeSpan? maxAge)
    {
        Label = label;
        MaxAge = maxAge;
    }

    /// <summary>Menu caption.</summary>
    public string Label { get; }

    /// <summary>Maximum age of matching files; null = no limit.</summary>
    public TimeSpan? MaxAge { get; }

    /// <summary>Activates this range (wired by the owning launcher).</summary>
    public System.Windows.Input.ICommand? SelectCommand { get; internal set; }

    internal static IReadOnlyList<TimeRangeOption> CreateDefaults() =>
    [
        new("Any time", null),
        new("Today", TimeSpan.FromDays(1)),
        new("Past week", TimeSpan.FromDays(7)),
        new("Past month", TimeSpan.FromDays(31)),
        new("Past year", TimeSpan.FromDays(366)),
    ];
}

/// <summary>Grouping of file kinds used for section headers and icon colours.</summary>
internal static class FileKindGroups
{
    /// <summary>Section header used by the macOS design ("Documents", "Mail", …).</summary>
    public static string GroupName(FileKind kind) => kind switch
    {
        FileKind.Document or FileKind.Pdf or FileKind.Text or FileKind.Notebook => "Documents",
        FileKind.Mail => "Mail",
        FileKind.Spreadsheet => "Spreadsheets",
        FileKind.Presentation => "Presentations",
        FileKind.Code => "Code",
        FileKind.Image => "Images",
        FileKind.Audio or FileKind.Video => "Media",
        _ => "Other",
    };

    /// <summary>Palette key suffix for the kind tile colours ("VfKindPdfBg" etc).</summary>
    public static string PaletteKey(FileKind kind) => kind switch
    {
        FileKind.Pdf => "Pdf",
        FileKind.Document => "Document",
        FileKind.Spreadsheet => "Spreadsheet",
        FileKind.Presentation => "Presentation",
        FileKind.Code or FileKind.Notebook => "Code",
        FileKind.Mail => "Mail",
        FileKind.Image => "Image",
        FileKind.Audio or FileKind.Video => "Media",
        FileKind.Archive => "Archive",
        _ => "Text",
    };
}
