using Gapo.Core.Optimisation;
using Xunit;

namespace Gapo.Core.Tests;

public sealed class OptimisationEstimateTests
{
    [Fact]
    public void AWindowAtLowFrameRatesLastsLongerThanItsMinimumSpan()
    {
        Assert.Equal(2.6 + 0.65 + 1.2, OptimisationEstimate.WindowSeconds(60), 2);
        Assert.Equal((45 / 10.0) + 0.65 + 1.2, OptimisationEstimate.WindowSeconds(10), 2);
        Assert.Equal(7.0 + 0.65 + 1.2, OptimisationEstimate.WindowSeconds(0), 2);
    }

    [Fact]
    public void TheLiveChangeEstimateCoversWhatTheRigMeasures()
    {
        var estimate = OptimisationEstimate.ForLiveChanges(fps: 25, candidates: 7, gameHasSettled: true, frameCapAllowed: true);

        Assert.InRange(46, estimate.Low.TotalSeconds, estimate.High.TotalSeconds);
        Assert.InRange(51, estimate.Low.TotalSeconds, estimate.High.TotalSeconds);
    }

    [Fact]
    public void AGameThatHasNotSettledYetAddsWarmUpTime()
    {
        var settled = OptimisationEstimate.ForLiveChanges(25, 7, gameHasSettled: true, frameCapAllowed: true);
        var fresh = OptimisationEstimate.ForLiveChanges(25, 7, gameHasSettled: false, frameCapAllowed: true);

        Assert.True(fresh.High > settled.High);
    }

    [Fact]
    public void TheRestartEstimateCoversWhatTheRigMeasuresAndHonoursTheRestartLimit()
    {
        var six = OptimisationEstimate.ForRestarts(candidates: 8, mostRestarts: 6);
        var three = OptimisationEstimate.ForRestarts(candidates: 8, mostRestarts: 3);

        Assert.InRange(172, six.Low.TotalSeconds, six.High.TotalSeconds);
        Assert.InRange(187, six.Low.TotalSeconds, six.High.TotalSeconds);
        Assert.True(three.High < six.High);
    }

    [Fact]
    public void RemainingTimeFollowsTheMeasuredPace()
    {
        var remaining = OptimisationEstimate.Remaining(stepsDone: 4, stepsExpected: 10, elapsed: TimeSpan.FromSeconds(20));

        Assert.Equal(22.5, remaining.Low.TotalSeconds, 1);
        Assert.Equal(37.5, remaining.High.TotalSeconds, 1);
        Assert.Equal(TimeSpan.Zero, OptimisationEstimate.Remaining(0, 10, TimeSpan.FromSeconds(5)).High);
    }

    [Theory]
    [InlineData(40, 70, "about 40–70 seconds")]
    [InlineData(172, 540, "about 2–9 minutes")]
    [InlineData(2, 4, "about 5 seconds")]
    [InlineData(28, 112, "about 30 seconds to 2 minutes")]
    [InlineData(61, 100, "about 1–2 minutes")]
    public void RangesAreDescribedInPlainWords(double low, double high, string expected) =>
        Assert.Equal(expected, new TimeRange(TimeSpan.FromSeconds(low), TimeSpan.FromSeconds(high)).Describe());
}
