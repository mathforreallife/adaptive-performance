using Gapo.Core.Measurement;
using Xunit;

namespace Gapo.Core.Tests;

public sealed class MeasurementTests
{
    private const int Precision = 6;
    private const int Revision = 3;

    private static MeasuredFrame Frame(double endMs, double frameMs = 20, double busyMs = 18, int revision = Revision, FrameMarks marks = FrameMarks.None) =>
        new(endMs, frameMs, busyMs, revision, marks);

    private static MeasuredFrame[] Steady(int count, double frameMs = 20, double busyMs = 18, double startMs = 0) =>
        Enumerable.Range(1, count).Select(index => Frame(startMs + (index * frameMs), frameMs, busyMs)).ToArray();

    [Fact]
    public void NoFramesGiveEmptyStatistics()
    {
        Assert.Equal(FrameStatistics.Empty, FrameStatistics.From([]));
    }

    [Fact]
    public void SteadyFramesGiveTheirRateAndNoStutter()
    {
        var statistics = FrameStatistics.From(Steady(100));

        Assert.Equal(100, statistics.Count);
        Assert.Equal(2000.0, statistics.DurationMs, Precision);
        Assert.Equal(50.0, statistics.AverageFps, Precision);
        Assert.Equal(20.0, statistics.MedianFrameMs, Precision);
        Assert.Equal(18.0, statistics.MedianBusyMs, Precision);
        Assert.Equal(0.0, statistics.FrameTimeSpreadMs, Precision);
        Assert.Equal(0, statistics.Stutters);
    }

    [Fact]
    public void MedianBusyTimeIgnoresAHitchThatTheAverageRateFeels()
    {
        var frames = Steady(99).Append(Frame(2100, frameMs: 120, busyMs: 118)).ToArray();

        var statistics = FrameStatistics.From(frames);

        Assert.Equal(18.0, statistics.MedianBusyMs, Precision);
        Assert.True(statistics.AverageFps < 50.0);
        Assert.Equal(1, statistics.Stutters);
        Assert.Equal(120.0, statistics.LongestFrameMs, Precision);
    }

    [Fact]
    public void AFrameIsAStutterOnlyWhenClearlySlowerThanTypical()
    {
        var frames = Steady(50).Append(Frame(1100, frameMs: 39)).Append(Frame(1200, frameMs: 41)).ToArray();

        Assert.Equal(1, FrameStatistics.From(frames).Stutters);
    }

    [Fact]
    public void MarkedHitchesCountAsStuttersAtALowerThreshold()
    {
        var frames = Steady(50).Append(Frame(1100, frameMs: 36, marks: FrameMarks.StorageHitch)).ToArray();

        var statistics = FrameStatistics.From(frames);

        Assert.Equal(1, statistics.Stutters);
        Assert.Equal(1, statistics.StorageHitches);
        Assert.Equal(36.0, statistics.LongestStorageHitchMs, Precision);
    }

    [Fact]
    public void StuttersPerMinuteScalesWithTheMeasuredDuration()
    {
        var frames = Steady(149).Append(Frame(3100, frameMs: 20 + 100)).ToArray();

        var statistics = FrameStatistics.From(frames);

        Assert.Equal(1 / (statistics.DurationMs / 60_000.0), statistics.StuttersPerMinute, Precision);
    }

    [Fact]
    public void LowsAreNotReportedFromTooFewFrames()
    {
        var statistics = FrameStatistics.From(Steady(150));

        Assert.Null(statistics.OnePercentLowFps);
        Assert.Null(statistics.PointOnePercentLowFps);
    }

    [Fact]
    public void OnePercentLowIsTheRateOfTheSlowestHundredthOfFrames()
    {
        var frames = Steady(990, frameMs: 10).Concat(Enumerable.Range(0, 10).Select(index => Frame(10_000 + index, frameMs: 40))).ToArray();

        var statistics = FrameStatistics.From(frames);

        Assert.Equal(25.0, statistics.OnePercentLowFps!.Value, Precision);
        Assert.Null(statistics.PointOnePercentLowFps);
    }

    [Fact]
    public void PercentilesInterpolate()
    {
        Assert.Equal(25.0, FrameStatistics.Percentile([10, 20, 30, 40], 0.5), Precision);
        Assert.Equal(38.5, FrameStatistics.Percentile([10, 20, 30, 40], 0.95), Precision);
        Assert.Equal(7.0, FrameStatistics.Percentile([7], 0.99), Precision);
    }

    [Fact]
    public void WindowDiscardsSettlingFramesAfterItsStart()
    {
        var window = new MeasurementWindow(new MeasurementWindowOptions(), Revision, startedAtGameTimeMs: 1000);
        window.Offer(Frame(1200));
        window.Offer(Frame(1649));
        window.Offer(Frame(1650));

        Assert.Single(window.Frames);
    }

    [Fact]
    public void WindowCompletesAfterEnoughTimeAndFrames()
    {
        var window = new MeasurementWindow(new MeasurementWindowOptions(), Revision, startedAtGameTimeMs: 0);
        foreach (var frame in Steady(200, frameMs: 20, startMs: 650))
        {
            window.Offer(frame);
        }

        Assert.Equal(MeasurementWindowState.Complete, window.State);
        Assert.False(window.WasCutShort);
        Assert.InRange(window.SpanMs, 2600, 2700);
        Assert.Equal(50.0, window.Summarise().AverageFps, Precision);
    }

    [Fact]
    public void SlowGameNeedsALongerWindowToReachTheMinimumFrameCount()
    {
        var window = new MeasurementWindow(new MeasurementWindowOptions(), Revision, startedAtGameTimeMs: 0);
        foreach (var frame in Steady(60, frameMs: 100, startMs: 650))
        {
            window.Offer(frame);
        }

        Assert.Equal(MeasurementWindowState.Complete, window.State);
        Assert.Equal(45, window.Frames.Count);
    }

    [Fact]
    public void WindowKeepsOnlyFramesOfTheExpectedSettingsRevision()
    {
        var window = new MeasurementWindow(new MeasurementWindowOptions(), Revision, startedAtGameTimeMs: 0);
        window.Offer(Frame(700, revision: Revision - 1));
        window.Offer(Frame(720));
        window.Offer(Frame(740, marks: FrameMarks.Warmup));

        Assert.Single(window.Frames);
        Assert.Equal(1, window.FramesFromOtherRevisions);
        Assert.Equal(1, window.WarmupFramesSkipped);
    }

    [Fact]
    public void WindowThatTimesOutWithUsableFramesIsCompleteButFlagged()
    {
        var window = new MeasurementWindow(new MeasurementWindowOptions(), Revision, startedAtGameTimeMs: 0);
        foreach (var frame in Steady(20, frameMs: 400, startMs: 650))
        {
            window.Offer(frame);
        }

        Assert.Equal(MeasurementWindowState.Complete, window.State);
        Assert.True(window.WasCutShort);
    }

    [Fact]
    public void WindowFailsWhenTheGameStopsProducingFrames()
    {
        var window = new MeasurementWindow(new MeasurementWindowOptions(), Revision, startedAtGameTimeMs: 0);
        window.Offer(Frame(700));
        window.Offer(Frame(720));

        window.Advance(gameTimeNowMs: 8000);

        Assert.Equal(MeasurementWindowState.Failed, window.State);
        Assert.Contains("Too few frames", window.FailureReason);
    }

    [Fact]
    public void NoiseFloorIsTwoAndAHalfTimesTheRepeatDifference()
    {
        var noise = NoiseFloor.FromRepeatedMeasurement(20.0, 20.4);

        Assert.Equal(0.4 / 20.2, noise.RawSpreadFraction, Precision);
        Assert.Equal(0.4 / 20.2 * 2.5, noise.FloorFraction, Precision);
        Assert.True(noise.CanBeTrusted);
    }

    [Fact]
    public void NoiseFloorNeverGoesBelowItsMinimum()
    {
        Assert.Equal(0.025, NoiseFloor.FromRepeatedMeasurement(20.0, 20.0).FloorFraction, Precision);
    }

    [Fact]
    public void ARepeatThatDisagreesTooMuchIsNotTrusted()
    {
        var noise = NoiseFloor.FromRepeatedMeasurement(20.0, 26.0);

        Assert.Equal(0.10, noise.FloorFraction, Precision);
        Assert.False(noise.CanBeTrusted);
    }

    [Fact]
    public void GainInsideTheNoiseFloorProvesNothing()
    {
        var noise = NoiseFloor.FromRepeatedMeasurement(20.0, 20.8);
        var gain = ChangeComparison.RelativeGain(referenceMedianBusyMs: 20.0, candidateMedianBusyMs: 18.5);

        Assert.Equal(ChangeEffect.WithinNoise, ChangeComparison.Judge(gain, noise));
        Assert.False(noise.IsBeatenBy(gain));
    }

    [Fact]
    public void LargeGainIsAnImprovementAndLargeLossARegression()
    {
        var noise = NoiseFloor.FromRepeatedMeasurement(20.0, 20.0);

        Assert.Equal(ChangeEffect.Improvement, ChangeComparison.Judge(ChangeComparison.RelativeGain(20, 12), noise));
        Assert.Equal(ChangeEffect.Regression, ChangeComparison.Judge(ChangeComparison.RelativeGain(20, 26), noise));
    }

    [Fact]
    public void BorderlineGainAsksForConfirmationInsteadOfAVerdict()
    {
        var noise = NoiseFloor.FromRepeatedMeasurement(20.0, 20.0);

        Assert.Equal(ChangeEffect.NeedsConfirmation, ChangeComparison.Judge(0.05, noise));
    }

    [Fact]
    public void InterleavedPairsMustAgreeInDirection()
    {
        var noise = NoiseFloor.FromRepeatedMeasurement(20.0, 20.0);

        Assert.Equal(ChangeEffect.Improvement, ChangeComparison.JudgeInterleaved([0.05, 0.06], noise));
        Assert.Equal(ChangeEffect.WithinNoise, ChangeComparison.JudgeInterleaved([0.09, -0.01], noise));
        Assert.Equal(ChangeEffect.Regression, ChangeComparison.JudgeInterleaved([-0.05, -0.04], noise));
        Assert.Equal(ChangeEffect.WithinNoise, ChangeComparison.JudgeInterleaved([0.01, 0.02], noise));
    }

    [Fact]
    public void GameIsNotWarmUntilEnoughCleanFramesWereSeen()
    {
        var detector = new WarmupDetector();
        foreach (var frame in Steady(29))
        {
            detector.Offer(frame);
        }

        Assert.False(detector.IsWarm);
        detector.Offer(Frame(600));
        Assert.True(detector.IsWarm);
    }

    [Fact]
    public void AFrameMarkedAsWarmupKeepsTheGameColdUntilItLeavesTheRecentFrames()
    {
        var detector = new WarmupDetector();
        detector.Offer(Frame(0, marks: FrameMarks.Warmup));
        foreach (var frame in Steady(WarmupDetector.FramesExamined - 1))
        {
            detector.Offer(frame);
        }

        Assert.False(detector.IsWarm);
        detector.Offer(Frame(5000));
        Assert.True(detector.IsWarm);
    }

    [Fact]
    public void UnmarkedSpikesFromStreamingAlsoKeepTheGameCold()
    {
        var detector = new WarmupDetector();
        for (var index = 0; index < 60; index++)
        {
            detector.Offer(Frame(index * 20, frameMs: index % 6 == 0 ? 70 : 20));
        }

        Assert.False(detector.IsWarm);
    }

    [Fact]
    public void OneOccasionalHitchDoesNotCountAsWarmingUp()
    {
        var detector = new WarmupDetector();
        foreach (var frame in Steady(60))
        {
            detector.Offer(frame);
        }

        detector.Offer(Frame(1300, frameMs: 95));

        Assert.True(detector.IsWarm);
    }
}
