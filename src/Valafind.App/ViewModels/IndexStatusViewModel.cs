using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using Valafind.Core.Abstractions;
using Valafind.Core.Indexing;
using Valafind.App.Localization;

namespace Valafind.App.ViewModels;

/// <summary>Visual tone of the indexing state (drives colours).</summary>
public enum StatusTone
{
    /// <summary>Neutral / starting.</summary>
    Neutral,
    /// <summary>Indexing or up to date.</summary>
    Good,
    /// <summary>Throttled by system pressure or battery.</summary>
    Busy,
    /// <summary>Paused by the user.</summary>
    Paused,
    /// <summary>Model unavailable.</summary>
    Error,
}

/// <summary>Indexed-file count of one group of file kinds ("PDFs 312"), shown in the index panel.</summary>
public sealed partial class KindCountViewModel : ObservableObject
{
    internal KindCountViewModel(string label, params FileKind[] kinds)
    {
        Label = label;
        Kinds = kinds;
    }

    /// <summary>Group caption ("PDFs").</summary>
    public string Label { get; }

    /// <summary>Kinds counted in this group (groups do not overlap).</summary>
    internal IReadOnlyList<FileKind> Kinds { get; }

    /// <summary>Indexed files in this group.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasFiles))]
    public partial long Count { get; private set; }

    /// <summary>"1,204".</summary>
    [ObservableProperty] public partial string CountText { get; private set; } = "0";

    /// <summary>True when at least one file of the group is indexed (empty groups are hidden).</summary>
    public bool HasFiles => Count > 0;

    internal void Update(IReadOnlyDictionary<FileKind, long> counts)
    {
        long n = 0;
        foreach (var kind in Kinds)
            n += counts.TryGetValue(kind, out var c) ? c : 0;
        Count = n;
        CountText = DisplayFormat.Count(n);
    }

    /// <summary>The groups in display order: the filter chips' categories, made disjoint, plus media and other.</summary>
    internal static IReadOnlyList<KindCountViewModel> CreateDefaults() =>
    [
        new(Strings.FilterDocuments, FileKind.Document, FileKind.Text),
        new(Strings.FilterPdfs, FileKind.Pdf),
        new(Strings.FilterSpreadsheets, FileKind.Spreadsheet),
        new(Strings.FilterSlides, FileKind.Presentation),
        new(Strings.FilterMail, FileKind.Mail),
        new(Strings.FilterCode, FileKind.Code, FileKind.Notebook),
        new(Strings.FilterImages, FileKind.Image),
        new(Strings.GroupMedia, FileKind.Audio, FileKind.Video),
        new(Strings.GroupOther, FileKind.Other, FileKind.Archive),
    ];
}

/// <summary>UI projection of <see cref="IndexStatus"/>: texts, progress fractions and pressure meters.</summary>
public sealed partial class IndexStatusViewModel : ObservableObject
{
    /// <summary>Disk throughput that fills the disk meter when the OS cannot report busy time.</summary>
    internal const double DiskMeterFullScaleBytesPerSecond = 150d * 1024 * 1024;

    /// <summary>Creates a projection of <see cref="IndexStatus.Initial"/>.</summary>
    public IndexStatusViewModel() => Update(IndexStatus.Initial, 30, 25);

    /// <summary>Latest raw status.</summary>
    [ObservableProperty] public partial IndexStatus Raw { get; private set; } = IndexStatus.Initial;

    /// <summary>"Indexing paused — system busy", "Indexing in the background", "Up to date", …</summary>
    [ObservableProperty] public partial string StateText { get; private set; } = string.Empty;

    /// <summary>Short state for the Linux pressure card ("Paused — system busy").</summary>
    [ObservableProperty] public partial string ShortStateText { get; private set; } = string.Empty;

    /// <summary>Colour tone of the state.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsToneGood), nameof(IsToneBusy), nameof(IsTonePaused), nameof(IsToneError), nameof(IsToneNeutral), nameof(IsActive))]
    public partial StatusTone Tone { get; private set; }

    /// <summary>Tone flags for style classes.</summary>
    public bool IsToneGood => Tone == StatusTone.Good;
    /// <inheritdoc cref="IsToneGood"/>
    public bool IsToneBusy => Tone == StatusTone.Busy;
    /// <inheritdoc cref="IsToneGood"/>
    public bool IsTonePaused => Tone == StatusTone.Paused;
    /// <inheritdoc cref="IsToneGood"/>
    public bool IsToneError => Tone == StatusTone.Error;
    /// <inheritdoc cref="IsToneGood"/>
    public bool IsToneNeutral => Tone == StatusTone.Neutral;

    /// <summary>Neither paused nor in error (starting, indexing or up to date).</summary>
    public bool IsActive => !IsPaused && Tone != StatusTone.Error;

    /// <summary>Paused for any reason (system busy, battery, user).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsActive))]
    public partial bool IsPaused { get; private set; }

    /// <summary>Paused by the user (tray / launcher toggle). Mirrors the persisted setting pushed through
    /// <see cref="SetUserPaused"/>, not the indexer's state, so the tray and launcher toggles always agree with their
    /// captions, also while the Core is starting or the model is unavailable.</summary>
    [ObservableProperty] public partial bool IsUserPaused { get; private set; }

    /// <summary>Sets the user-pause flag (the persisted setting) and updates the captions that depend on it.</summary>
    public void SetUserPaused(bool paused)
    {
        IsUserPaused = paused;
        ResumeActionText = paused ? Strings.Resume : Strings.IndexNow;
        PauseMenuText = paused ? Strings.ResumeIndexing : Strings.PauseIndexing;
    }

    /// <summary>All phases done.</summary>
    [ObservableProperty] public partial bool IsUpToDate { get; private set; }

    /// <summary>True while some files are still waiting to be indexed, so a search can miss them: indexing is running
    /// or paused and less than all of the known files are searchable. Not while starting (the index from the last run
    /// may well be complete) nor while the model is unavailable (nothing can be searched).</summary>
    [ObservableProperty] public partial bool IsIncomplete { get; private set; }

    /// <summary>Tooltip of the discreet "up to date" indicator shown instead of the index panel.</summary>
    [ObservableProperty] public partial string UpToDateDetail { get; private set; } = string.Empty;

    /// <summary>Phase 1 progress 0..1.</summary>
    [ObservableProperty] public partial double FrequentFraction { get; private set; }

    /// <summary>"6,214 / 6,214".</summary>
    [ObservableProperty] public partial string FrequentText { get; private set; } = string.Empty;

    /// <summary>"6,214 / 6,214 files".</summary>
    [ObservableProperty] public partial string FrequentFilesText { get; private set; } = string.Empty;

    /// <summary>"Frequent" when phase 1 is done, otherwise "Frequent 52%".</summary>
    [ObservableProperty] public partial string FrequentShortText { get; private set; } = string.Empty;

    /// <summary>Phase 1 finished.</summary>
    [ObservableProperty] public partial bool IsFrequentComplete { get; private set; }

    /// <summary>Phase 2 progress 0..1.</summary>
    [ObservableProperty] public partial double AllFraction { get; private set; }

    /// <summary>"41%".</summary>
    [ObservableProperty] public partial string AllPercentText { get; private set; } = string.Empty;

    /// <summary>"182,930 of 446,120" ("+" while discovery is still counting).</summary>
    [ObservableProperty] public partial string AllCountText { get; private set; } = string.Empty;

    /// <summary>"41% · 182,930 of 446,120".</summary>
    [ObservableProperty] public partial string AllSummaryText { get; private set; } = string.Empty;

    /// <summary>"182,930 of 446,120 · paused" (Linux card caption).</summary>
    [ObservableProperty] public partial string AllCaptionText { get; private set; } = string.Empty;

    /// <summary>Share of all known files that are searchable, 0..1.</summary>
    [ObservableProperty] public partial double SearchableFraction { get; private set; }

    /// <summary>True once a pressure sample is available.</summary>
    [ObservableProperty] public partial bool HasPressure { get; private set; }

    /// <summary>CPU meter 0..1.</summary>
    [ObservableProperty] public partial double CpuFraction { get; private set; }

    /// <summary>"78%".</summary>
    [ObservableProperty] public partial string CpuText { get; private set; } = "—";

    /// <summary>CPU above the user's limit.</summary>
    [ObservableProperty] public partial bool IsCpuHigh { get; private set; }

    /// <summary>Where the CPU meter's target marker sits, 0..1: the level indexing steers whole-machine CPU towards
    /// (a little under the limit, <see cref="IndexingPace.TargetFor"/>). NaN hides it.</summary>
    [ObservableProperty] public partial double CpuTargetFraction { get; private set; } = double.NaN;

    /// <summary>"Target 27%" (tooltip of the CPU meter).</summary>
    [ObservableProperty] public partial string CpuTargetText { get; private set; } = string.Empty;

    /// <summary>The disk meter's target marker, 0..1; NaN (hidden) when the meter shows throughput rather than busy
    /// time, since the target is a busy-time percentage.</summary>
    [ObservableProperty] public partial double DiskTargetFraction { get; private set; } = double.NaN;

    /// <summary>"Target 22%" (tooltip of the disk meter), empty without a marker.</summary>
    [ObservableProperty] public partial string DiskTargetText { get; private set; } = string.Empty;

    /// <summary>Disk meter 0..1 (busy time when known, otherwise throughput scaled).</summary>
    [ObservableProperty] public partial double DiskFraction { get; private set; }

    /// <summary>"92 MB/s" or "35%".</summary>
    [ObservableProperty] public partial string DiskText { get; private set; } = "—";

    /// <summary>Disk above the user's limit.</summary>
    [ObservableProperty] public partial bool IsDiskHigh { get; private set; }

    /// <summary>True when the platform reports GPU utilisation, so the GPU meter is shown.</summary>
    [ObservableProperty] public partial bool HasGpu { get; private set; }

    /// <summary>GPU meter 0..1 (the busiest GPU).</summary>
    [ObservableProperty] public partial double GpuFraction { get; private set; }

    /// <summary>"42%".</summary>
    [ObservableProperty] public partial string GpuText { get; private set; } = "—";

    /// <summary>GPU above the user's limit.</summary>
    [ObservableProperty] public partial bool IsGpuHigh { get; private set; }

    /// <summary>The GPU meter's target marker, 0..1 (a little under the GPU limit); NaN hides it.</summary>
    [ObservableProperty] public partial double GpuTargetFraction { get; private set; } = double.NaN;

    /// <summary>"Target 27%" (tooltip of the GPU meter), empty without a marker.</summary>
    [ObservableProperty] public partial string GpuTargetText { get; private set; } = string.Empty;

    /// <summary>Compact one-liner for the macOS footer: "Indexing in the background 41%".</summary>
    [ObservableProperty] public partial string CompactText { get; private set; } = string.Empty;

    /// <summary>Whether the Linux "paused" banner is shown.</summary>
    [ObservableProperty] public partial bool ShowBanner { get; private set; }

    /// <summary>Banner title, e.g. "Indexing paused while the system is busy".</summary>
    [ObservableProperty] public partial string BannerTitle { get; private set; } = string.Empty;

    /// <summary>Banner detail, e.g. "41% of your files are searchable".</summary>
    [ObservableProperty] public partial string BannerDetail { get; private set; } = string.Empty;

    /// <summary>Banner/strip action caption: "Index Now" or "Resume".</summary>
    [ObservableProperty] public partial string ResumeActionText { get; private set; } = Strings.IndexNow;

    /// <summary>Tray menu caption: "Pause indexing" / "Resume indexing".</summary>
    [ObservableProperty] public partial string PauseMenuText { get; private set; } = Strings.PauseIndexing;

    /// <summary>Indexed files per type group, in display order (updated in place).</summary>
    public IReadOnlyList<KindCountViewModel> KindCounts { get; } = KindCountViewModel.CreateDefaults();

    /// <summary>True once any file is indexed, so the per-type row has something to show.</summary>
    [ObservableProperty] public partial bool HasKindCounts { get; private set; }

    /// <summary>"Documents 1,204 · PDFs 312 · …" (tooltips).</summary>
    [ObservableProperty] public partial string KindCountsText { get; private set; } = string.Empty;

    /// <summary>Recomputes every projected value from <paramref name="status"/>.</summary>
    /// <param name="status">Raw status from the indexing service.</param>
    /// <param name="maxCpuPercent">User CPU threshold (for "high" colouring).</param>
    /// <param name="maxDiskBusyPercent">User disk threshold.</param>
    /// <param name="maxGpuPercent">User GPU threshold.</param>
    public void Update(IndexStatus status, double maxCpuPercent, double maxDiskBusyPercent, double maxGpuPercent = 100)
    {
        ArgumentNullException.ThrowIfNull(status);
        Raw = status;
        var inv = CultureInfo.InvariantCulture;

        // Phase progress.
        IsFrequentComplete = status.FrequentTotal > 0 ? status.FrequentDone >= status.FrequentTotal : status.Phase > IndexPhase.Frequent;
        FrequentFraction = status.FrequentTotal > 0 ? Math.Clamp((double)status.FrequentDone / status.FrequentTotal, 0, 1) : (IsFrequentComplete ? 1 : 0);
        FrequentText = DisplayFormat.Count(status.FrequentDone) + " / " + DisplayFormat.Count(status.FrequentTotal);
        FrequentFilesText = Loc.Format(Strings.IndexFilesCount, FrequentText);

        FrequentShortText = IsFrequentComplete ? Strings.IndexFrequent : Loc.Format(Strings.IndexFrequentPercent, DisplayFormat.Percent(FrequentFraction));

        AllFraction = status.AllTotal > 0 ? Math.Clamp((double)status.AllDone / status.AllTotal, 0, 1) : 0;
        if (status.State == IndexingState.UpToDate)
            AllFraction = 1;
        AllPercentText = DisplayFormat.Percent(AllFraction);
        AllCountText = Loc.Format(Strings.IndexCountOf, DisplayFormat.Count(status.AllDone), DisplayFormat.Count(status.AllTotal) + (status.DiscoveryComplete ? string.Empty : "+"));
        AllSummaryText = AllPercentText + " · " + AllCountText;

        // Indexed files per type.
        var parts = new List<string>(KindCounts.Count);
        foreach (var group in KindCounts)
        {
            group.Update(status.IndexedByKind);
            if (group.HasFiles)
                parts.Add(group.Label + " " + group.CountText);
        }
        HasKindCounts = parts.Count > 0;
        KindCountsText = string.Join(" · ", parts);

        var done = status.FrequentDone + status.AllDone;
        var total = status.FrequentTotal + status.AllTotal;
        SearchableFraction = status.State == IndexingState.UpToDate ? 1 : total > 0 ? Math.Clamp((double)done / total, 0, 1) : 0;

        // Pressure.
        var sample = status.LastSample;
        HasPressure = sample is not null;
        if (sample is not null)
        {
            CpuFraction = Math.Clamp(sample.CpuPercent / 100, 0, 1);
            CpuText = Math.Round(Math.Clamp(sample.CpuPercent, 0, 100)).ToString("0", inv) + "%";
            IsCpuHigh = sample.CpuPercent > maxCpuPercent;
            var cpuTarget = IndexingPace.TargetFor(maxCpuPercent);
            CpuTargetFraction = cpuTarget / 100;
            CpuTargetText = Loc.Format(Strings.IndexTargetTooltip, DisplayFormat.Percent(cpuTarget / 100));
            if (sample.DiskBytesPerSecond is { } bps)
                DiskText = DisplayFormat.Throughput(bps);
            else if (sample.DiskBusyPercent is { } busyText)
                DiskText = Math.Round(busyText).ToString("0", inv) + "%";
            else
                DiskText = "—";
            DiskFraction = sample.DiskBusyPercent is { } busy
                ? Math.Clamp(busy / 100, 0, 1)
                : sample.DiskBytesPerSecond is { } b ? Math.Clamp(b / DiskMeterFullScaleBytesPerSecond, 0, 1) : 0;
            IsDiskHigh = sample.DiskBusyPercent is { } db && db > maxDiskBusyPercent;
            // The meter shows busy time when known (then the target applies), throughput otherwise.
            var diskTarget = IndexingPace.TargetFor(maxDiskBusyPercent);
            DiskTargetFraction = sample.DiskBusyPercent is not null ? diskTarget / 100 : double.NaN;
            DiskTargetText = sample.DiskBusyPercent is not null ? Loc.Format(Strings.IndexTargetTooltip, DisplayFormat.Percent(diskTarget / 100)) : string.Empty;
            HasGpu = sample.GpuPercent is not null;
            var gpu = Math.Clamp(sample.GpuPercent ?? 0, 0, 100);
            GpuFraction = gpu / 100;
            GpuText = HasGpu ? Math.Round(gpu).ToString("0", inv) + "%" : "—";
            IsGpuHigh = HasGpu && gpu > maxGpuPercent;
            var gpuTarget = IndexingPace.TargetFor(maxGpuPercent);
            GpuTargetFraction = HasGpu ? gpuTarget / 100 : double.NaN;
            GpuTargetText = HasGpu ? Loc.Format(Strings.IndexTargetTooltip, DisplayFormat.Percent(gpuTarget / 100)) : string.Empty;
        }
        else
        {
            CpuFraction = DiskFraction = GpuFraction = 0;
            CpuText = DiskText = GpuText = "—";
            IsCpuHigh = IsDiskHigh = IsGpuHigh = HasGpu = false;
            CpuTargetFraction = DiskTargetFraction = GpuTargetFraction = double.NaN;
            CpuTargetText = DiskTargetText = GpuTargetText = string.Empty;
        }

        // State.
        IsPaused = status.State is IndexingState.PausedByUser or IndexingState.PausedSystemBusy or IndexingState.PausedOnBattery;
        IsUpToDate = status.State == IndexingState.UpToDate;
        UpToDateDetail = Loc.Format(Strings.IndexUpToDateDetail, DisplayFormat.Count(status.AllDone))
            + (HasKindCounts ? Environment.NewLine + KindCountsText : string.Empty);
        (StateText, ShortStateText, Tone) = status.State switch
        {
            IndexingState.Starting => (Strings.StatePreparing, Strings.StateStarting, StatusTone.Neutral),
            IndexingState.Running => (Strings.StateIndexingInBackground, Strings.StateIndexingInBackground, StatusTone.Good),
            IndexingState.PausedSystemBusy => (Strings.StatePausedSystemBusy, Strings.StatePausedSystemBusyShort, StatusTone.Busy),
            IndexingState.PausedOnBattery => (Strings.StatePausedOnBattery, Strings.StatePausedOnBatteryShort, StatusTone.Busy),
            IndexingState.PausedByUser => (Strings.StatePaused, Strings.StatePausedByYou, StatusTone.Paused),
            IndexingState.UpToDate => (Strings.UpToDate, Strings.UpToDate, StatusTone.Good),
            IndexingState.ModelUnavailable => (Strings.StateModelUnavailable, Strings.StateModelUnavailableShort, StatusTone.Error),
            _ => (status.State.ToString(), status.State.ToString(), StatusTone.Neutral),
        };

        var phaseWord = status.State switch
        {
            IndexingState.UpToDate => Strings.PhaseDone,
            IndexingState.Running => status.Phase == IndexPhase.All ? Strings.PhaseIndexing : Strings.PhaseQueued,
            IndexingState.Starting => Strings.PhaseQueued,
            IndexingState.ModelUnavailable => Strings.PhaseStopped,
            _ => Strings.PhasePaused,
        };
        AllCaptionText = AllCountText + " · " + phaseWord;

        CompactText = status.State switch
        {
            IndexingState.UpToDate => Strings.IndexUpToDate,
            IndexingState.Running => Loc.Format(Strings.StateIndexingInBackgroundPercent, DisplayFormat.Percent(SearchableFraction)),
            IndexingState.Starting => Strings.StatePreparingShort,
            _ => StateText + " · " + DisplayFormat.Percent(SearchableFraction),
        };

        IsIncomplete = status.State is not (IndexingState.UpToDate or IndexingState.Starting or IndexingState.ModelUnavailable)
            && SearchableFraction < 1;

        ShowBanner = IsPaused;
        BannerTitle = status.State switch
        {
            IndexingState.PausedSystemBusy => Strings.BannerPausedSystemBusy,
            IndexingState.PausedOnBattery => Strings.BannerPausedOnBattery,
            IndexingState.PausedByUser => Strings.StatePaused,
            _ => string.Empty,
        };
        BannerDetail = Loc.Format(Strings.BannerSearchable, DisplayFormat.Percent(SearchableFraction));
        SetUserPaused(IsUserPaused);
    }
}
