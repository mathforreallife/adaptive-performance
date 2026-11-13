namespace Gapo.Core.Overhead;

public readonly record struct ProcessUsage(TimeSpan ProcessorTime, long PrivateBytes);

public sealed class OverheadMeter
{
    public static readonly TimeSpan DefaultRollingSpan = TimeSpan.FromSeconds(10);

    private const double BytesPerMegabyte = 1024.0 * 1024.0;
    private const double PercentScale = 100.0;

    private readonly int logicalProcessorCount;
    private readonly TimeSpan rollingSpan;
    private readonly Queue<Sample> rollingSamples = new();
    private Sample? first;
    private Sample last;
    private TimeSpan windowWallTime;
    private TimeSpan windowProcessorTime;
    private double peakCpuPercent;
    private long peakPrivateBytes;

    public OverheadMeter(int logicalProcessorCount)
        : this(logicalProcessorCount, DefaultRollingSpan)
    {
    }

    public OverheadMeter(int logicalProcessorCount, TimeSpan rollingSpan)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(logicalProcessorCount);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(rollingSpan, TimeSpan.Zero);
        this.logicalProcessorCount = logicalProcessorCount;
        this.rollingSpan = rollingSpan;
    }

    public void Record(TimeSpan wallClock, ProcessUsage usage, bool insideMeasurementWindow)
    {
        var sample = new Sample(wallClock, usage.ProcessorTime);
        if (first is null)
        {
            first = sample;
        }
        else
        {
            var wallTime = sample.WallClock - last.WallClock;
            var processorTime = sample.ProcessorTime - last.ProcessorTime;
            peakCpuPercent = Math.Max(peakCpuPercent, CpuPercent(processorTime, wallTime));
            if (insideMeasurementWindow)
            {
                windowWallTime += wallTime;
                windowProcessorTime += processorTime;
            }
        }

        last = sample;
        peakPrivateBytes = Math.Max(peakPrivateBytes, usage.PrivateBytes);
        PrivateMegabytes = usage.PrivateBytes / BytesPerMegabyte;
        rollingSamples.Enqueue(sample);
        while (rollingSamples.Count > 2 && wallClock - rollingSamples.Peek().WallClock > rollingSpan)
        {
            rollingSamples.Dequeue();
        }
    }

    public double PrivateMegabytes { get; private set; }

    public double RollingCpuPercent
    {
        get
        {
            if (rollingSamples.Count < 2)
            {
                return 0;
            }

            var oldest = rollingSamples.Peek();
            return CpuPercent(last.ProcessorTime - oldest.ProcessorTime, last.WallClock - oldest.WallClock);
        }
    }

    public OverheadSummary Summarise()
    {
        if (first is not { } start)
        {
            return OverheadSummary.Empty;
        }

        var observed = last.WallClock - start.WallClock;
        return new OverheadSummary(
            RunAverageCpuPercent: CpuPercent(last.ProcessorTime - start.ProcessorTime, observed),
            MeasurementWindowCpuPercent: CpuPercent(windowProcessorTime, windowWallTime),
            PeakCpuPercent: peakCpuPercent,
            RollingCpuPercent: RollingCpuPercent,
            PrivateMegabytes: PrivateMegabytes,
            PeakPrivateMegabytes: peakPrivateBytes / BytesPerMegabyte,
            Observed: observed,
            ObservedInsideMeasurementWindows: windowWallTime);
    }

    private double CpuPercent(TimeSpan processorTime, TimeSpan wallTime) =>
        wallTime <= TimeSpan.Zero
            ? 0
            : processorTime.TotalSeconds / (wallTime.TotalSeconds * logicalProcessorCount) * PercentScale;

    private readonly record struct Sample(TimeSpan WallClock, TimeSpan ProcessorTime);
}
