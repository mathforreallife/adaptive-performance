namespace Gapo.Core.Telemetry;

public enum ProcessPriority
{
    Unknown,
    Idle,
    BelowNormal,
    Normal,
    AboveNormal,
    High,
    Realtime,
}

public readonly record struct GameProcessReading(
    TimeSpan ProcessorTime,
    long PrivateBytes,
    long WorkingSetBytes,
    ulong ReadBytes,
    ulong WriteBytes,
    ProcessPriority Priority);

public readonly record struct MemoryStatus(
    long TotalPhysicalBytes,
    long AvailablePhysicalBytes,
    long CommitLimitBytes,
    long CommittedBytes,
    int LoadPercent)
{
    private const double PercentScale = 100.0;

    public double CommitPercent => CommitLimitBytes <= 0 ? 0 : CommittedBytes * PercentScale / CommitLimitBytes;
}
