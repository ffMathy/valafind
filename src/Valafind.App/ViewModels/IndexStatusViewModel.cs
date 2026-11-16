using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using Valafind.Core.Abstractions;

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

/// <summary>UI projection of <see cref="IndexStatus"/>: texts, progress fractions and pressure meters.</summary>
public sealed partial class IndexStatusViewModel : ObservableObject
{
    /// <summary>Disk throughput that fills the disk meter when the OS cannot report busy time.</summary>
    internal const double DiskMeterFullScaleBytesPerSecond = 150d * 1024 * 1024;

    /// <summary>Creates a projection of <see cref="IndexStatus.Initial"/>.</summary>
    public IndexStatusViewModel() => Update(IndexStatus.Initial, 30, 25);

    /// <summary>Latest raw status.</summary>
    [ObservableProperty] public partial IndexStatus Raw { get; private set; } = IndexStatus.Initial;

    /// <summary>"Indexing paused — system busy", "Indexing while idle", "Up to date", …</summary>
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

    /// <summary>Paused by the user (tray / launcher toggle).</summary>
    [ObservableProperty] public partial bool IsUserPaused { get; private set; }

    /// <summary>All phases done.</summary>
    [ObservableProperty] public partial bool IsUpToDate { get; private set; }

    /// <summary>Phase 1 progress 0..1.</summary>
    [ObservableProperty] public partial double FrequentFraction { get; private set; }

    /// <summary>"6,214 / 6,214".</summary>
    [ObservableProperty] public partial string FrequentText { get; private set; } = string.Empty;

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

    /// <summary>CPU above the user's indexing threshold.</summary>
    [ObservableProperty] public partial bool IsCpuHigh { get; private set; }

    /// <summary>Disk meter 0..1 (busy time when known, otherwise throughput scaled).</summary>
    [ObservableProperty] public partial double DiskFraction { get; private set; }

    /// <summary>"92 MB/s" or "35%".</summary>
    [ObservableProperty] public partial string DiskText { get; private set; } = "—";

    /// <summary>Disk above the user's indexing threshold.</summary>
    [ObservableProperty] public partial bool IsDiskHigh { get; private set; }

    /// <summary>Compact one-liner for the macOS footer: "Indexing while idle 41%".</summary>
    [ObservableProperty] public partial string CompactText { get; private set; } = string.Empty;

    /// <summary>Whether the Linux "paused" banner is shown.</summary>
    [ObservableProperty] public partial bool ShowBanner { get; private set; }

    /// <summary>Banner title, e.g. "Indexing paused while the system is busy".</summary>
    [ObservableProperty] public partial string BannerTitle { get; private set; } = string.Empty;

    /// <summary>Banner detail, e.g. "41% of your files are searchable".</summary>
    [ObservableProperty] public partial string BannerDetail { get; private set; } = string.Empty;

    /// <summary>Banner/strip action caption: "Index Now" or "Resume".</summary>
    [ObservableProperty] public partial string ResumeActionText { get; private set; } = "Index Now";

    /// <summary>Explains the automatic throttling policy.</summary>
    [ObservableProperty] public partial string PolicyText { get; private set; } = string.Empty;

    /// <summary>Tray menu caption: "Pause indexing" / "Resume indexing".</summary>
    [ObservableProperty] public partial string PauseMenuText { get; private set; } = "Pause indexing";

    /// <summary>Recomputes every projected value from <paramref name="status"/>.</summary>
    /// <param name="status">Raw status from the indexing service.</param>
    /// <param name="maxCpuPercent">User CPU threshold (for "high" colouring).</param>
    /// <param name="maxDiskBusyPercent">User disk threshold.</param>
    public void Update(IndexStatus status, double maxCpuPercent, double maxDiskBusyPercent)
    {
        ArgumentNullException.ThrowIfNull(status);
        Raw = status;
        var inv = CultureInfo.InvariantCulture;

        // Phase progress.
        IsFrequentComplete = status.FrequentTotal > 0 ? status.FrequentDone >= status.FrequentTotal : status.Phase > IndexPhase.Frequent;
        FrequentFraction = status.FrequentTotal > 0 ? Math.Clamp((double)status.FrequentDone / status.FrequentTotal, 0, 1) : (IsFrequentComplete ? 1 : 0);
        FrequentText = DisplayFormat.Count(status.FrequentDone) + " / " + DisplayFormat.Count(status.FrequentTotal);

        FrequentShortText = IsFrequentComplete ? "Frequent" : "Frequent " + DisplayFormat.Percent(FrequentFraction);

        AllFraction = status.AllTotal > 0 ? Math.Clamp((double)status.AllDone / status.AllTotal, 0, 1) : 0;
        if (status.State == IndexingState.UpToDate)
            AllFraction = 1;
        AllPercentText = DisplayFormat.Percent(AllFraction);
        AllCountText = DisplayFormat.Count(status.AllDone) + " of " + DisplayFormat.Count(status.AllTotal) + (status.DiscoveryComplete ? string.Empty : "+");
        AllSummaryText = AllPercentText + " · " + AllCountText;

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
        }
        else
        {
            CpuFraction = DiskFraction = 0;
            CpuText = DiskText = "—";
            IsCpuHigh = IsDiskHigh = false;
        }

        // State.
        IsUserPaused = status.State == IndexingState.PausedByUser;
        IsPaused = status.State is IndexingState.PausedByUser or IndexingState.PausedSystemBusy or IndexingState.PausedOnBattery;
        IsUpToDate = status.State == IndexingState.UpToDate;
        (StateText, ShortStateText, Tone) = status.State switch
        {
            IndexingState.Starting => ("Preparing index…", "Starting", StatusTone.Neutral),
            IndexingState.Running => ("Indexing while idle", "Indexing while idle", StatusTone.Good),
            IndexingState.PausedSystemBusy => ("Indexing paused — system busy", "Paused — system busy", StatusTone.Busy),
            IndexingState.PausedOnBattery => ("Indexing paused — on battery", "Paused — on battery", StatusTone.Busy),
            IndexingState.PausedByUser => ("Indexing paused", "Paused by you", StatusTone.Paused),
            IndexingState.UpToDate => ("Up to date", "Up to date", StatusTone.Good),
            IndexingState.ModelUnavailable => ("Model unavailable — search is off", "Model unavailable", StatusTone.Error),
            _ => (status.State.ToString(), status.State.ToString(), StatusTone.Neutral),
        };

        var phaseWord = status.State switch
        {
            IndexingState.UpToDate => "done",
            IndexingState.Running => status.Phase == IndexPhase.All ? "indexing" : "queued",
            IndexingState.Starting => "queued",
            IndexingState.ModelUnavailable => "stopped",
            _ => "paused",
        };
        AllCaptionText = AllCountText + " · " + phaseWord;

        CompactText = status.State switch
        {
            IndexingState.UpToDate => "Index up to date",
            IndexingState.Running => "Indexing while idle " + DisplayFormat.Percent(SearchableFraction),
            IndexingState.Starting => "Preparing index",
            _ => StateText + " · " + DisplayFormat.Percent(SearchableFraction),
        };

        ShowBanner = IsPaused;
        BannerTitle = status.State switch
        {
            IndexingState.PausedSystemBusy => "Indexing paused while the system is busy",
            IndexingState.PausedOnBattery => "Indexing paused while on battery",
            IndexingState.PausedByUser => "Indexing paused",
            _ => string.Empty,
        };
        BannerDetail = DisplayFormat.Percent(SearchableFraction) + " of your files are searchable";
        ResumeActionText = IsUserPaused ? "Resume" : "Index Now";
        PauseMenuText = IsUserPaused ? "Resume indexing" : "Pause indexing";
        PolicyText = status.State is IndexingState.PausedSystemBusy or IndexingState.PausedOnBattery
            ? "Resumes automatically when CPU and disk are idle"
            : string.Create(inv, $"Pauses automatically when CPU passes {maxCpuPercent:0}% or disk activity is high");
    }
}
