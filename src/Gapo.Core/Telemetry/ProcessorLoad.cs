namespace Gapo.Core.Telemetry;

public readonly record struct ProcessorTimes(long IdleTicks, long KernelTicksIncludingIdle, long UserTicks)
{
    public long TotalTicks => KernelTicksIncludingIdle + UserTicks;
}

public static class ProcessorLoad
{
    private const double PercentScale = 100.0;

    public static void Between(ReadOnlySpan<ProcessorTimes> earlier, ReadOnlySpan<ProcessorTimes> later, Span<double> loadPercentPerProcessor)
    {
        if (earlier.Length != later.Length || later.Length != loadPercentPerProcessor.Length)
        {
            throw new ArgumentException("All spans must describe the same number of logical processors.");
        }

        for (var processor = 0; processor < later.Length; processor++)
        {
            loadPercentPerProcessor[processor] = Between(earlier[processor], later[processor]);
        }
    }

    public static double Between(ProcessorTimes earlier, ProcessorTimes later)
    {
        var total = later.TotalTicks - earlier.TotalTicks;
        if (total <= 0)
        {
            return 0;
        }

        var idle = later.IdleTicks - earlier.IdleTicks;
        return Math.Clamp((total - idle) * PercentScale / total, 0, PercentScale);
    }

    public static double Average(ReadOnlySpan<double> loadPercentPerProcessor)
    {
        if (loadPercentPerProcessor.IsEmpty)
        {
            return 0;
        }

        var sum = 0.0;
        foreach (var load in loadPercentPerProcessor)
        {
            sum += load;
        }

        return sum / loadPercentPerProcessor.Length;
    }

    public static double Busiest(ReadOnlySpan<double> loadPercentPerProcessor)
    {
        var busiest = 0.0;
        foreach (var load in loadPercentPerProcessor)
        {
            busiest = Math.Max(busiest, load);
        }

        return busiest;
    }
}
