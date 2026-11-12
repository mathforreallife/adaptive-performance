using Gapo.Core.FrameTiming;
using Gapo.Core.Statistics;
using Xunit;

namespace Gapo.Core.Tests;

public sealed class SeriesSummaryTests
{
    private const int Precision = 6;

    [Fact]
    public void EmptySeriesReportsZeroes()
    {
        var series = new SeriesSummary();

        Assert.Equal(0, series.Count);
        Assert.Equal(0.0, series.Mean, Precision);
        Assert.Equal(0.0, series.Median(), Precision);
    }

    [Fact]
    public void MedianIsRobustAgainstOneHitchWhileTheMeanIsNot()
    {
        var series = new SeriesSummary();
        foreach (var frameMs in new[] { 16.0, 16.0, 17.0, 16.0, 135.0 })
        {
            series.Add(frameMs);
        }

        Assert.Equal(16.0, series.Median(), Precision);
        Assert.Equal(40.0, series.Mean, Precision);
    }

    [Fact]
    public void PercentileInterpolatesBetweenNeighbours()
    {
        var series = new SeriesSummary();
        foreach (var value in new[] { 10.0, 20.0, 30.0, 40.0 })
        {
            series.Add(value);
        }

        Assert.Equal(25.0, series.Median(), Precision);
        Assert.Equal(10.0, series.Percentile(0), Precision);
        Assert.Equal(40.0, series.Percentile(1), Precision);
        Assert.Equal(38.5, series.Percentile(0.95), Precision);
    }

    [Fact]
    public void StopsStoringAtCapacityAndCountsWhatItDropped()
    {
        var series = new SeriesSummary(capacity: 2);
        series.Add(1);
        series.Add(3);
        series.Add(100);

        Assert.Equal(2, series.Count);
        Assert.Equal(1, series.Overflowed);
        Assert.Equal(2.0, series.Mean, Precision);
    }

    [Fact]
    public void RejectsQuantilesOutsideZeroToOne()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new SeriesSummary().Percentile(1.5));
    }

    [Fact]
    public void FrameQueueHandsFramesOverInOrder()
    {
        var queue = new PresentedFrameQueue(capacity: 4);
        queue.Enqueue(new PresentedFrame(1, 0, 10, 9, 1, 0));
        queue.Enqueue(new PresentedFrame(1, 10, 11, 9, 2, 0));
        var drained = new PresentedFrame[4];

        var count = queue.Drain(drained);

        Assert.Equal(2, count);
        Assert.Equal(10.0, drained[0].FrameMs, Precision);
        Assert.Equal(11.0, drained[1].FrameMs, Precision);
        Assert.Equal(0, queue.Drain(drained));
    }

    [Fact]
    public void FullFrameQueueDropsTheOldestFrameAndCountsIt()
    {
        var queue = new PresentedFrameQueue(capacity: 2);
        for (var frame = 1; frame <= 3; frame++)
        {
            queue.Enqueue(new PresentedFrame(1, frame, frame, 0, 0, 0));
        }

        var drained = new PresentedFrame[2];
        queue.Drain(drained);

        Assert.Equal(1, queue.Dropped);
        Assert.Equal(2.0, drained[0].FrameMs, Precision);
        Assert.Equal(3.0, drained[1].FrameMs, Precision);
    }
}
