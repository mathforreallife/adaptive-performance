namespace Gapo.Core.Measurement;

public sealed class WarmupDetector
{
    public const int FramesExamined = 90;
    public const int FewestFramesToJudge = 30;
    public static readonly TimeSpan LongestWait = TimeSpan.FromSeconds(45);

    private const double SpikeMedianFactor = 1.8;
    private const double SpikeMarginMs = 20.0;
    private const int MostSpikesWhenSettled = 2;

    private readonly MeasuredFrame[] recent = new MeasuredFrame[FramesExamined];
    private readonly double[] sortedFrameTimes = new double[FramesExamined];
    private int count;
    private int next;

    public void Offer(in MeasuredFrame frame)
    {
        recent[next] = frame;
        next = (next + 1) % recent.Length;
        count = Math.Min(count + 1, recent.Length);
    }

    public void Reset()
    {
        count = 0;
        next = 0;
    }

    public bool IsWarm => count >= FewestFramesToJudge && !AnyMarkedWarmup() && SpikeCount() <= MostSpikesWhenSettled;

    private bool AnyMarkedWarmup()
    {
        for (var index = 0; index < count; index++)
        {
            if (recent[index].IsWarmup)
            {
                return true;
            }
        }

        return false;
    }

    private int SpikeCount()
    {
        for (var index = 0; index < count; index++)
        {
            sortedFrameTimes[index] = recent[index].FrameMs;
        }

        Array.Sort(sortedFrameTimes, 0, count);
        var median = sortedFrameTimes[count / 2];
        var threshold = Math.Max(median * SpikeMedianFactor, median + SpikeMarginMs);
        var spikes = 0;
        for (var index = 0; index < count; index++)
        {
            if (recent[index].FrameMs > threshold)
            {
                spikes++;
            }
        }

        return spikes;
    }
}
