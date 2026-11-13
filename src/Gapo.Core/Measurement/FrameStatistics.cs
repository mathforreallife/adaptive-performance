namespace Gapo.Core.Measurement;

[Flags]
public enum FrameMarks
{
    None = 0,
    Warmup = 1,
    StorageHitch = 2,
}

public readonly record struct MeasuredFrame(double EndTimeMs, double FrameMs, double BusyMs, int SettingsRevision, FrameMarks Marks)
{
    public bool IsWarmup => (Marks & FrameMarks.Warmup) != 0;

    public bool IsMarkedHitch => (Marks & (FrameMarks.Warmup | FrameMarks.StorageHitch)) != 0;
}

public sealed record FrameStatistics(
    int Count,
    double DurationMs,
    double AverageFps,
    double MedianFrameMs,
    double Percentile95FrameMs,
    double Percentile99FrameMs,
    double LongestFrameMs,
    double FrameTimeSpreadMs,
    double MedianBusyMs,
    double? OnePercentLowFps,
    double? PointOnePercentLowFps,
    int Stutters,
    double StuttersPerMinute,
    int StorageHitches,
    double LongestStorageHitchMs)
{
    public const int FramesNeededForOnePercentLow = 1000;
    public const int FramesNeededForPointOnePercentLow = 10_000;

    private const double StutterMedianFactor = 1.8;
    private const double StutterMarginMs = 20.0;
    private const double MarkedHitchMarginMs = 15.0;
    private const double MillisecondsPerSecond = 1000.0;
    private const double MillisecondsPerMinute = 60_000.0;

    public static FrameStatistics Empty { get; } = new(0, 0, 0, 0, 0, 0, 0, 0, 0, null, null, 0, 0, 0, 0);

    public static FrameStatistics From(ReadOnlySpan<MeasuredFrame> frames)
    {
        if (frames.IsEmpty)
        {
            return Empty;
        }

        var frameTimes = new double[frames.Length];
        var busyTimes = new double[frames.Length];
        var duration = 0.0;
        for (var index = 0; index < frames.Length; index++)
        {
            frameTimes[index] = frames[index].FrameMs;
            busyTimes[index] = frames[index].BusyMs;
            duration += frames[index].FrameMs;
        }

        Array.Sort(frameTimes);
        Array.Sort(busyTimes);
        var mean = duration / frames.Length;
        var median = Percentile(frameTimes, 0.5);
        var (stutters, storageHitches, longestStorageHitch) = CountHitches(frames, median);
        return new FrameStatistics(
            frames.Length,
            duration,
            mean > 0 ? MillisecondsPerSecond / mean : 0,
            median,
            Percentile(frameTimes, 0.95),
            Percentile(frameTimes, 0.99),
            frameTimes[^1],
            StandardDeviation(frameTimes, mean),
            Percentile(busyTimes, 0.5),
            frames.Length >= FramesNeededForOnePercentLow ? LowFps(frameTimes, 0.01) : null,
            frames.Length >= FramesNeededForPointOnePercentLow ? LowFps(frameTimes, 0.001) : null,
            stutters,
            duration > 0 ? stutters / (duration / MillisecondsPerMinute) : 0,
            storageHitches,
            longestStorageHitch);
    }

    public static double Percentile(double[] sortedAscending, double quantile)
    {
        if (sortedAscending.Length == 0)
        {
            return 0;
        }

        var position = quantile * (sortedAscending.Length - 1);
        var lower = (int)Math.Floor(position);
        var upper = Math.Min(sortedAscending.Length - 1, lower + 1);
        return sortedAscending[lower] + ((sortedAscending[upper] - sortedAscending[lower]) * (position - lower));
    }

    private static (int Stutters, int StorageHitches, double LongestStorageHitchMs) CountHitches(ReadOnlySpan<MeasuredFrame> frames, double medianFrameMs)
    {
        var unmarkedThreshold = Math.Max(medianFrameMs * StutterMedianFactor, medianFrameMs + StutterMarginMs);
        var markedThreshold = medianFrameMs + MarkedHitchMarginMs;
        var stutters = 0;
        var storageHitches = 0;
        var longestStorageHitch = 0.0;
        foreach (var frame in frames)
        {
            if (frame.FrameMs > unmarkedThreshold || (frame.IsMarkedHitch && frame.FrameMs > markedThreshold))
            {
                stutters++;
            }

            if ((frame.Marks & FrameMarks.StorageHitch) != 0)
            {
                storageHitches++;
                longestStorageHitch = Math.Max(longestStorageHitch, frame.FrameMs);
            }
        }

        return (stutters, storageHitches, longestStorageHitch);
    }

    private static double LowFps(double[] sortedFrameTimes, double worstShare)
    {
        var count = Math.Max(1, (int)Math.Ceiling(sortedFrameTimes.Length * worstShare));
        var total = 0.0;
        for (var index = sortedFrameTimes.Length - count; index < sortedFrameTimes.Length; index++)
        {
            total += sortedFrameTimes[index];
        }

        return total > 0 ? count * MillisecondsPerSecond / total : 0;
    }

    private static double StandardDeviation(double[] values, double mean)
    {
        var sumOfSquares = 0.0;
        foreach (var value in values)
        {
            sumOfSquares += (value - mean) * (value - mean);
        }

        return Math.Sqrt(sumOfSquares / values.Length);
    }
}
