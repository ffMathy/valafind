using Valafind.App.ViewModels;
using Valafind.Core.Abstractions;
using Xunit;

namespace Valafind.App.Tests.ViewModels;

public sealed class IndexStatusViewModelTests
{
    private static readonly DateTimeOffset At = new(2026, 11, 16, 14, 32, 0, TimeSpan.Zero);

    [Fact]
    public void BusyStatusMatchesTheMockupTexts()
    {
        var vm = new IndexStatusViewModel();
        vm.Update(new IndexStatus(IndexingState.PausedSystemBusy, IndexPhase.All, 6214, 6214, 182_930, 446_120, true,
            new PressureSample(At, 78, 70, 92d * 1024 * 1024, false), null), 30, 25);

        Assert.Equal("Indexing paused — system busy", vm.StateText);
        Assert.Equal("Paused — system busy", vm.ShortStateText);
        Assert.Equal(StatusTone.Busy, vm.Tone);
        Assert.True(vm.IsPaused);
        Assert.False(vm.IsActive);
        Assert.Equal("6,214 / 6,214", vm.FrequentText);
        Assert.True(vm.IsFrequentComplete);
        Assert.Equal("Frequent", vm.FrequentShortText);
        Assert.Equal("41%", vm.AllPercentText);
        Assert.Equal("41% · 182,930 of 446,120", vm.AllSummaryText);
        Assert.Equal("182,930 of 446,120 · paused", vm.AllCaptionText);
        Assert.Equal("78%", vm.CpuText);
        Assert.Equal(0.78, vm.CpuFraction, 3);
        Assert.True(vm.IsCpuHigh);
        Assert.Equal("92 MB/s", vm.DiskText);
        Assert.Equal(0.70, vm.DiskFraction, 3);
        Assert.True(vm.IsDiskHigh);
        Assert.True(vm.ShowBanner);
        Assert.Equal("Indexing paused while the system is busy", vm.BannerTitle);
        Assert.Equal("41% of your files are searchable", vm.BannerDetail);
        Assert.Equal("Index Now", vm.ResumeActionText);
        Assert.Equal("Resumes automatically when CPU and disk are idle", vm.PolicyText);
        Assert.Equal("Pause indexing", vm.PauseMenuText);
    }

    [Fact]
    public void RunningStatusIsCompactForTheMacFooter()
    {
        var vm = new IndexStatusViewModel();
        vm.Update(new IndexStatus(IndexingState.Running, IndexPhase.All, 6214, 6214, 182_930, 446_120, false,
            new PressureSample(At, 14, null, 4d * 1024 * 1024, false), "/x"), 30, 25);

        Assert.Equal("Indexing while idle", vm.StateText);
        Assert.Equal(StatusTone.Good, vm.Tone);
        Assert.False(vm.ShowBanner);
        Assert.Equal("Indexing while idle 41%", vm.CompactText);
        Assert.Equal("182,930 of 446,120+", vm.AllCountText);
        Assert.False(vm.IsCpuHigh);
        Assert.False(vm.IsDiskHigh);
        Assert.Equal("4 MB/s", vm.DiskText);
        Assert.True(vm.DiskFraction is > 0 and < 0.1);
        Assert.Equal("Pauses automatically when CPU passes 30% or disk activity is high", vm.PolicyText);
    }

    [Fact]
    public void UserPauseOffersResume()
    {
        // The user-pause flag is the persisted setting pushed by the host (single source of truth for tray + launcher).
        var vm = new IndexStatusViewModel();
        vm.SetUserPaused(true);
        vm.Update(IndexStatus.Initial with { State = IndexingState.PausedByUser }, 30, 25);
        Assert.True(vm.IsUserPaused);
        Assert.Equal("Resume", vm.ResumeActionText);
        Assert.Equal("Resume indexing", vm.PauseMenuText);
        Assert.Equal(StatusTone.Paused, vm.Tone);
    }

    [Fact]
    public void IncompletePhaseOneShowsPercent()
    {
        var vm = new IndexStatusViewModel();
        vm.Update(IndexStatus.Initial with { State = IndexingState.Running, FrequentDone = 52, FrequentTotal = 100 }, 30, 25);
        Assert.False(vm.IsFrequentComplete);
        Assert.Equal("Frequent 52%", vm.FrequentShortText);
        Assert.Equal("—", vm.CpuText);
        Assert.False(vm.HasPressure);
    }

    [Fact]
    public void UpToDateIsFull()
    {
        var vm = new IndexStatusViewModel();
        vm.Update(new IndexStatus(IndexingState.UpToDate, IndexPhase.Incremental, 10, 10, 90, 90, true, null, null), 30, 25);
        Assert.Equal(1, vm.AllFraction);
        Assert.Equal(1, vm.SearchableFraction);
        Assert.Equal("Up to date", vm.StateText);
        Assert.Equal("Index up to date", vm.CompactText);
        Assert.True(vm.IsUpToDate);
    }

    [Fact]
    public void ModelUnavailableIsAnError()
    {
        var vm = new IndexStatusViewModel();
        vm.Update(IndexStatus.Initial with { State = IndexingState.ModelUnavailable }, 30, 25);
        Assert.Equal(StatusTone.Error, vm.Tone);
        Assert.True(vm.IsToneError);
        Assert.False(vm.IsActive);
    }
}
