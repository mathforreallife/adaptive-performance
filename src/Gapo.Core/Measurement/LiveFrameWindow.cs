namespace Gapo.Core.Measurement;

public sealed class LiveFrameWindow
{
    public const int DefaultChartPoints = 150;

    private readonly double spanMs;
    private readonly Queue<MeasuredFrame> frames = new(1024);

    public LiveFrameWindow(TimeSpan span)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(span, TimeSpan.Zero);
        spanMs = span.TotalMilliseconds;
    }

    public int Count => frames.Count;

    public void Add(in MeasuredFrame frame)
    {
        frames.Enqueue(frame);
        while (frames.Count > 1 && frame.EndTimeMs - frames.Peek().EndTimeMs > spanMs)
        {
            frames.Dequeue();
        }
    }

    public void Clear() => frames.Clear();

    public FrameStatistics Summarise() => FrameStatistics.From(frames.ToArray());

    public IReadOnlyList<double> ChartPoints(int maximumPoints = DefaultChartPoints)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumPoints);
        var all = frames.ToArray();
        if (all.Length <= maximumPoints)
        {
            return all.Select(frame => frame.FrameMs).ToArray();
        }

        var points = new double[maximumPoints];
        for (var bucket = 0; bucket < maximumPoints; bucket++)
        {
            var from = (int)((long)bucket * all.Length / maximumPoints);
            var to = Math.Max(from + 1, (int)((long)(bucket + 1) * all.Length / maximumPoints));
            var slowest = 0.0;
            for (var index = from; index < to; index++)
            {
                slowest = Math.Max(slowest, all[index].FrameMs);
            }

            points[bucket] = slowest;
        }

        return points;
    }
}
