namespace Gapo.Core.Telemetry;

public readonly record struct ProcessCpuShare(int ProcessId, string Name, double MachinePercent, long PrivateBytes);

public readonly record struct ThreadLoad(int ThreadId, double PercentOfOneProcessor);

public static class ProcessActivity
{
    private const double PercentScale = 100.0;

    public static double MachinePercent(ProcessSample earlier, ProcessSample later, TimeSpan elapsed, int logicalProcessorCount, double? cyclesPerSecond)
    {
        if (elapsed <= TimeSpan.Zero || logicalProcessorCount <= 0 || !IsSameProcess(earlier, later))
        {
            return 0;
        }

        var processorSeconds = cyclesPerSecond is { } rate && later.CycleCount >= earlier.CycleCount && later.CycleCount > 0
            ? (later.CycleCount - earlier.CycleCount) / rate
            : TimeSpan.FromTicks(Math.Max(0, later.ProcessorTicks - earlier.ProcessorTicks)).TotalSeconds;
        return processorSeconds / (elapsed.TotalSeconds * logicalProcessorCount) * PercentScale;
    }

    public static void MachinePercentPerProcess(
        ProcessSnapshot earlier,
        ProcessSnapshot later,
        int logicalProcessorCount,
        double? cyclesPerSecond,
        List<ProcessCpuShare> destination)
    {
        destination.Clear();
        var elapsed = later.CapturedAt - earlier.CapturedAt;
        foreach (var process in later.Processes)
        {
            if (earlier.TryFind(process.ProcessId, out var before) && IsSameProcess(before, process))
            {
                var percent = MachinePercent(before, process, elapsed, logicalProcessorCount, cyclesPerSecond);
                destination.Add(new ProcessCpuShare(process.ProcessId, process.Name, percent, process.PrivateBytes));
            }
        }
    }

    public static ThreadLoad BusiestThread(ProcessSnapshot earlier, ProcessSnapshot later)
    {
        var elapsedTicks = (later.CapturedAt - earlier.CapturedAt).Ticks;
        if (elapsedTicks <= 0 || earlier.DetailedProcessId != later.DetailedProcessId)
        {
            return default;
        }

        var busiest = default(ThreadLoad);
        foreach (var thread in later.DetailedThreads)
        {
            var before = FindThread(earlier, thread.ThreadId);
            if (before is null)
            {
                continue;
            }

            var percent = Math.Max(0, thread.ProcessorTicks - before.Value.ProcessorTicks) * PercentScale / elapsedTicks;
            if (percent > busiest.PercentOfOneProcessor)
            {
                busiest = new ThreadLoad(thread.ThreadId, percent);
            }
        }

        return busiest;
    }

    private static bool IsSameProcess(ProcessSample earlier, ProcessSample later) =>
        earlier.ProcessId == later.ProcessId && earlier.CreateTimeTicks == later.CreateTimeTicks;

    private static ThreadSample? FindThread(ProcessSnapshot snapshot, int threadId)
    {
        foreach (var thread in snapshot.DetailedThreads)
        {
            if (thread.ThreadId == threadId)
            {
                return thread;
            }
        }

        return null;
    }
}
