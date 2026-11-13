namespace Gapo.Core.Telemetry;

public readonly record struct ProcessSample(
    int ProcessId,
    int ParentProcessId,
    int SessionId,
    string Name,
    long CreateTimeTicks,
    ulong CycleCount,
    long KernelTicks,
    long UserTicks,
    long PrivateBytes,
    long WorkingSetBytes,
    long ReadBytes,
    long WriteBytes,
    int ThreadCount)
{
    public long ProcessorTicks => KernelTicks + UserTicks;
}

public readonly record struct ThreadSample(int ThreadId, long KernelTicks, long UserTicks)
{
    public long ProcessorTicks => KernelTicks + UserTicks;
}

public sealed class ProcessSnapshot
{
    private readonly List<ProcessSample> processes = new(256);
    private readonly List<ThreadSample> detailedThreads = new(128);
    private readonly Dictionary<int, int> indexByProcessId = new(256);

    public TimeSpan CapturedAt { get; private set; }

    public int DetailedProcessId { get; private set; }

    public IReadOnlyList<ProcessSample> Processes => processes;

    public IReadOnlyList<ThreadSample> DetailedThreads => detailedThreads;

    public void Begin(TimeSpan capturedAt, int detailedProcessId)
    {
        processes.Clear();
        detailedThreads.Clear();
        indexByProcessId.Clear();
        CapturedAt = capturedAt;
        DetailedProcessId = detailedProcessId;
    }

    public void Add(ProcessSample process)
    {
        indexByProcessId[process.ProcessId] = processes.Count;
        processes.Add(process);
    }

    public void AddDetailedThread(ThreadSample thread) => detailedThreads.Add(thread);

    public bool TryFind(int processId, out ProcessSample process)
    {
        if (indexByProcessId.TryGetValue(processId, out var index))
        {
            process = processes[index];
            return true;
        }

        process = default;
        return false;
    }
}
