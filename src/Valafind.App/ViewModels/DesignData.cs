using Valafind.Core.Abstractions;
using Valafind.Core.Configuration;

namespace Valafind.App.ViewModels;

/// <summary>
/// Realistic sample data matching the approved mockups. Used as the XAML previewer design context and by the
/// headless screenshot tests. Only shows states the real app can produce: images (and other media) are matched by
/// file name only, so their rows have no passage and no highlights.
/// </summary>
public static class DesignData
{
    /// <summary>"Now" for all sample data: Mon 16 Nov 2026 14:32 UTC.</summary>
    internal static readonly DateTimeOffset Now = new(2026, 11, 16, 14, 32, 0, TimeSpan.Zero);

    internal const string SampleQuery = "the lease agreement where we negotiated the deposit";

    /// <summary>Windows design sample (indexing paused, system busy).</summary>
    public static LauncherViewModel Windows => Create(UiFlavor.Windows);

    /// <summary>macOS design sample (indexing while idle).</summary>
    public static LauncherViewModel Mac => Create(UiFlavor.Mac);

    /// <summary>Linux design sample (indexing paused, system busy).</summary>
    public static LauncherViewModel Linux => Create(UiFlavor.Linux);

    /// <summary>Sample settings view model (does not touch the user's settings file).</summary>
    public static SettingsViewModel Settings => new(new ValafindSettings(), SettingsViewModel.DesignHost.Instance);

    /// <summary>Builds a populated launcher for <paramref name="flavor"/>.</summary>
    public static LauncherViewModel Create(UiFlavor flavor)
    {
        // The default chord (Ctrl+Alt+Space, ⌃⌥Space on macOS), as a fresh install shows it.
        var settings = new ValafindSettings();
        var home = flavor switch
        {
            UiFlavor.Windows => @"C:\Users\mathi",
            UiFlavor.Mac => "/Users/mathias",
            _ => "/home/mathias",
        };
        var vm = new LauncherViewModel(flavor, settings, new FixedTime(Now), static a => a(), home, AppDirectory(flavor));
        vm.LoadSnapshot(SampleQuery, Results(flavor, home), TimeSpan.FromMilliseconds(flavor == UiFlavor.Linux ? 42 : 38));
        vm.UpdateStatus(flavor == UiFlavor.Mac ? IdleStatus : BusyStatus);
        return vm;
    }

    /// <summary>A launcher in the "model not installed" state.</summary>
    public static LauncherViewModel CreateModelMissing(UiFlavor flavor)
    {
        var vm = new LauncherViewModel(flavor, new ValafindSettings(), new FixedTime(Now), static a => a(), null, AppDirectory(flavor));
        vm.SetModelMissing(flavor switch
        {
            UiFlavor.Windows => @"C:\Users\mathi\AppData\Local\Valafind\models\embeddinggemma-2",
            UiFlavor.Mac => "/Users/mathias/Library/Application Support/Valafind/models/embeddinggemma-2",
            _ => "/home/mathias/.local/share/valafind/models/embeddinggemma-2",
        });
        vm.UpdateStatus(IndexStatus.Initial with { State = IndexingState.ModelUnavailable });
        return vm;
    }

    /// <summary>A launcher whose model could not be loaded.</summary>
    public static LauncherViewModel CreateModelFailed(UiFlavor flavor)
    {
        var vm = CreateModelMissing(flavor);
        vm.SetModelFailed("Protobuf parsing failed: onnx/model_quantized.onnx is truncated.");
        return vm;
    }

    /// <summary>Where a typical install keeps the executable (and tools/fetch-model.*).</summary>
    internal static string AppDirectory(UiFlavor flavor) => flavor switch
    {
        UiFlavor.Windows => @"C:\Users\mathi\AppData\Local\Programs\Valafind",
        UiFlavor.Mac => "/Applications/Valafind.app/Contents/MacOS",
        _ => "/home/mathias/Applications/Valafind",
    };

    internal static IndexStatus BusyStatus { get; } = new(
        IndexingState.PausedSystemBusy, IndexPhase.All, 6214, 6214, 182_930, 446_120, true,
        new PressureSample(Now, 78, 70, 92d * 1024 * 1024, false), null);

    internal static IndexStatus IdleStatus { get; } = new(
        IndexingState.Running, IndexPhase.All, 6214, 6214, 182_930, 446_120, true,
        new PressureSample(Now, 14, 8, 4d * 1024 * 1024, false), null);

    private static DateTimeOffset Date(int y, int m, int d, int h = 10, int min = 0) => new(y, m, d, h, min, 0, TimeSpan.Zero);

    /// <summary>A file matched by its name only (images, audio, video): no passage, no highlights.</summary>
    private static SearchResult NameOnly(string dir, string name, FileKind kind, DateTimeOffset modified, long size, float score)
    {
        var sep = dir.Contains('\\') ? "\\" : "/";
        return new SearchResult(dir + sep + name, name, dir, kind, modified, size, score, string.Empty, []);
    }

    private static SearchResult Hit(string dir, string name, FileKind kind, DateTimeOffset modified, long size, float score,
        string snippet, params string[] highlights)
    {
        // Like Core's Snippet.Build: a passage cut out of a longer chunk carries "…" at both cut ends.
        if (snippet.Length > 0)
            snippet = "…" + snippet + "…";
        var sep = dir.Contains('\\') ? "\\" : "/";
        var spans = new List<TextSpan>();
        foreach (var h in highlights)
        {
            var i = snippet.IndexOf(h, StringComparison.Ordinal);
            if (i >= 0)
                spans.Add(new TextSpan(i, h.Length));
        }
        return new SearchResult(dir + sep + name, name, dir, kind, modified, size, score, snippet, spans);
    }

    private static IReadOnlyList<SearchResult> Results(UiFlavor flavor, string home)
    {
        string P(params string[] parts) => flavor == UiFlavor.Windows ? home + "\\" + string.Join("\\", parts) : home + "/" + string.Join("/", parts);

        return flavor switch
        {
            UiFlavor.Windows =>
            [
                Hit(P("Documents", "Bolig"), "Lejekontrakt_Nørrebrogade_2024.pdf", FileKind.Pdf, Date(2024, 3, 14), 421_888, 0.87f,
                    "Following the meeting on 2 March, the parties agree that the deposit is reduced from three to two months' rent (DKK 31,500), payable before key handover",
                    "the parties agree that the deposit is reduced from three to two months' rent"),
                Hit(P("Documents", "Mail", "Bolig"), "Depositum_mail_thread.eml", FileKind.Mail, Date(2024, 3, 3), 18_204, 0.82f,
                    "happy to accept two months if you sign by Friday", "accept two months"),
                Hit(P("Documents", "notes"), "2025-09-12 landlord call.md", FileKind.Text, Date(2025, 9, 12), 2_310, 0.78f,
                    "deposit refund: he'll deduct for the hallway paint", "deposit refund"),
                NameOnly(P("Pictures", "Camera Roll", "Lejlighed"), "Depositum_kvittering.HEIC", FileKind.Image, Date(2024, 3, 2), 2_811_004, 0.71f),
                Hit(P("OneDrive", "Finance"), "budget_2025.xlsx", FileKind.Spreadsheet, Date(2025, 1, 8), 48_120, 0.58f,
                    "Sheet “Bolig”: Deposit (Nørrebrogade) 31,500", "Deposit (Nørrebrogade)"),
                Hit(P("Downloads"), "Fremleje_tillæg_udkast.docx", FileKind.Document, Date(2024, 2, 19), 31_877, 0.54f,
                    "security deposit terms remain as in the main lease", "security deposit terms"),
            ],
            UiFlavor.Mac =>
            [
                Hit(P("Documents", "Bolig"), "Lejekontrakt_Nørrebrogade_2024.pdf", FileKind.Pdf, Date(2024, 3, 3), 421_888, 0.91f,
                    "Lejer betaler et depositum svarende til tre måneders husleje (DKK 39.000). Parterne er enige om, at depositum er nedsat fra seks til tre måneder efter forhandling den 28. februar 2024",
                    "tre måneders husleje (DKK 39.000)", "nedsat fra seks til tre måneder efter forhandling"),
                Hit(P("Mail", "Udlejer"), "Depositum_mail_thread.eml", FileKind.Mail, Date(2024, 2, 21), 18_204, 0.86f,
                    "we can accept two months if you sign the 24-month term", "accept two months"),
                Hit(P("Documents", "notes"), "2025-09-12 landlord call.md", FileKind.Text, Date(2025, 9, 12), 2_310, 0.84f,
                    "He confirmed the deposit comes back in full after the move-out inspection", "the deposit comes back in full"),
                NameOnly(P("Pictures", "2024"), "Depositum_kvittering.HEIC", FileKind.Image, Date(2024, 3, 3), 2_811_004, 0.79f),
                Hit(P("Documents", "Økonomi"), "budget_2025.xlsx", FileKind.Spreadsheet, Date(2025, 1, 8), 48_120, 0.62f,
                    "Deposit refund, Nørrebrogade 28,500 DKK", "Deposit refund"),
            ],
            _ =>
            [
                Hit(P("Documents", "Bolig"), "Lejekontrakt_Nørrebrogade_2024.pdf", FileKind.Pdf, Date(2024, 3, 3), 421_888, 0.91f,
                    "§ 7 Depositum — depositum nedsættes fra 3 til 2 måneders husleje efter aftale med udlejer",
                    "depositum nedsættes fra 3 til 2 måneders husleje"),
                Hit(P("Mail", "Archive", "2024"), "Depositum_mail_thread.eml", FileKind.Mail, Date(2024, 2, 27), 18_204, 0.88f,
                    "Re: Depositum — we can accept two months if you sign the 24-month term, best, Henrik",
                    "we can accept two months if you sign the 24-month term"),
                Hit(P("Documents", "notes"), "2025-09-12 landlord call.md", FileKind.Text, Date(2025, 9, 12), 2_310, 0.84f,
                    "He confirmed the deposit comes back in full after the move-out inspection, per clause 7",
                    "the deposit comes back in full after the move-out inspection"),
                NameOnly(P("Pictures", "2024", "03"), "Depositum_kvittering.HEIC", FileKind.Image, Date(2024, 3, 4), 2_811_004, 0.76f),
                Hit(P("Documents", "Økonomi"), "budget_2025.xlsx", FileKind.Spreadsheet, Date(2025, 1, 8), 48_120, 0.61f,
                    "Sheet “Bolig”, row 14 — Deposit refund, Nørrebrogade 28,500 DKK", "Deposit refund, Nørrebrogade"),
            ],
        };
    }

    private sealed class FixedTime(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
        public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;
    }
}
