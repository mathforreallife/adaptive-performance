using Gapo.Core.Overhead;

namespace Gapo.Core.Telemetry;

public readonly record struct MonitorSample(
    TimeSpan At,
    double AverageLoadPercent,
    double BusiestProcessorPercent,
    double GameMachinePercent,
    double GameProcessorsUsed,
    double OwnMachinePercent,
    double BackgroundMachinePercent,
    long GamePrivateBytes,
    double GameReadMegabytesPerSecond,
    double GameWriteMegabytesPerSecond,
    int MemoryLoadPercent,
    long AvailablePhysicalBytes,
    ProcessPriority GamePriority);

public sealed class SystemMonitor
{
    public const int DefaultHistoryCapacity = 180;

    private const double PercentScale = 100.0;
    private const double BytesPerMegabyte = 1024.0 * 1024.0;
    private const int NoGame = 0;

    private readonly int logicalProcessorCount;
    private readonly ProcessorTimes[] previousProcessors;
    private readonly double[] loadPerProcessor;
    private readonly MonitorSample[] history;
    private TimeSpan previousAt;
    private TimeSpan previousOwnProcessorTime;
    private GameProcessReading previousGame;
    private int previousGameProcessId;
    private bool hasPrevious;
    private int historyStart;

    public SystemMonitor(int logicalProcessorCount)
        : this(logicalProcessorCount, DefaultHistoryCapacity)
    {
    }

    public SystemMonitor(int logicalProcessorCount, int historyCapacity)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(logicalProcessorCount);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(historyCapacity);
        this.logicalProcessorCount = logicalProcessorCount;
        previousProcessors = new ProcessorTimes[logicalProcessorCount];
        loadPerProcessor = new double[logicalProcessorCount];
        history = new MonitorSample[historyCapacity];
    }

    public int Count { get; private set; }

    public ReadOnlySpan<double> LoadPerProcessor => loadPerProcessor;

    public MonitorSample Latest => Count == 0 ? default : this[Count - 1];

    public MonitorSample this[int index]
    {
        get
        {
            ArgumentOutOfRangeException.ThrowIfNegative(index);
            ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(index, Count);
            return history[(historyStart + index) % history.Length];
        }
    }

    public bool Record(
        TimeSpan at,
        ReadOnlySpan<ProcessorTimes> processors,
        int gameProcessId,
        GameProcessReading game,
        ProcessUsage own,
        MemoryStatus memory)
    {
        if (processors.Length != logicalProcessorCount)
        {
            throw new ArgumentException("The processor readings do not match the logical processor count.", nameof(processors));
        }

        var elapsed = at - previousAt;
        var produced = hasPrevious && elapsed > TimeSpan.Zero;
        if (produced)
        {
            ProcessorLoad.Between(previousProcessors, processors, loadPerProcessor);
            Append(BuildSample(at, elapsed, gameProcessId, game, own, memory));
        }

        processors.CopyTo(previousProcessors);
        previousAt = at;
        previousOwnProcessorTime = own.ProcessorTime;
        previousGame = game;
        previousGameProcessId = gameProcessId;
        hasPrevious = true;
        return produced;
    }

    private MonitorSample BuildSample(TimeSpan at, TimeSpan elapsed, int gameProcessId, GameProcessReading game, ProcessUsage own, MemoryStatus memory)
    {
        var sameGame = gameProcessId != NoGame && gameProcessId == previousGameProcessId;
        var averageLoad = ProcessorLoad.Average(loadPerProcessor);
        var gamePercent = sameGame ? MachinePercent(game.ProcessorTime - previousGame.ProcessorTime, elapsed) : 0;
        var ownPercent = MachinePercent(own.ProcessorTime - previousOwnProcessorTime, elapsed);
        return new MonitorSample(
            at,
            averageLoad,
            ProcessorLoad.Busiest(loadPerProcessor),
            gamePercent,
            gamePercent * logicalProcessorCount / PercentScale,
            ownPercent,
            Math.Max(0, averageLoad - gamePercent - ownPercent),
            gameProcessId == NoGame ? 0 : game.PrivateBytes,
            sameGame ? MegabytesPerSecond(game.ReadBytes, previousGame.ReadBytes, elapsed) : 0,
            sameGame ? MegabytesPerSecond(game.WriteBytes, previousGame.WriteBytes, elapsed) : 0,
            memory.LoadPercent,
            memory.AvailablePhysicalBytes,
            gameProcessId == NoGame ? ProcessPriority.Unknown : game.Priority);
    }

    private void Append(MonitorSample sample)
    {
        if (Count < history.Length)
        {
            history[(historyStart + Count) % history.Length] = sample;
            Count++;
        }
        else
        {
            history[historyStart] = sample;
            historyStart = (historyStart + 1) % history.Length;
        }
    }

    private double MachinePercent(TimeSpan processorTime, TimeSpan elapsed) =>
        Math.Max(0, processorTime.TotalSeconds) / (elapsed.TotalSeconds * logicalProcessorCount) * PercentScale;

    private static double MegabytesPerSecond(ulong later, ulong earlier, TimeSpan elapsed) =>
        later < earlier ? 0 : (later - earlier) / BytesPerMegabyte / elapsed.TotalSeconds;
}
