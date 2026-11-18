#pragma warning disable CA1707 // xunit test names use underscores for readability
#pragma warning disable CA1416 // FrameTolerance is pure arithmetic; the backend's platform attribute does not apply to it

using Valafind.Core.Extraction.Media;
using Xunit;

namespace Valafind.Core.Tests.Extraction;

public sealed class FrameToleranceTests
{
    private static TimeSpan S(double seconds) => TimeSpan.FromSeconds(seconds);

    [Fact]
    public void Frames_one_second_apart_may_be_half_a_second_early()
    {
        TimeSpan[] times = [S(0), S(1), S(2), S(3)];
        Assert.All(Enumerable.Range(0, times.Length), i => Assert.Equal(S(0.5), MediaFoundationBackend.FrameTolerance(times, i)));
    }

    [Fact]
    public void Frames_far_apart_are_capped_and_the_nearest_neighbour_decides()
    {
        TimeSpan[] times = [S(0), S(1), S(300), S(600)];
        Assert.Equal(S(0.5), MediaFoundationBackend.FrameTolerance(times, 0));
        Assert.Equal(S(0.5), MediaFoundationBackend.FrameTolerance(times, 1));
        Assert.Equal(MediaFoundationBackend.MaxFrameTolerance, MediaFoundationBackend.FrameTolerance(times, 2));
        Assert.Equal(MediaFoundationBackend.MaxFrameTolerance, MediaFoundationBackend.FrameTolerance(times, 3));
    }

    [Fact]
    public void A_single_frame_gets_the_maximum_and_duplicates_the_minimum()
    {
        Assert.Equal(MediaFoundationBackend.MaxFrameTolerance, MediaFoundationBackend.FrameTolerance([S(0)], 0));
        Assert.Equal(TimeSpan.FromMilliseconds(20), MediaFoundationBackend.FrameTolerance([S(4), S(4)], 1));
    }
}
