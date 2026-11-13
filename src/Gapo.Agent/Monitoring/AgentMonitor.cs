using Gapo.Core.Diagnosis;
using Gapo.Core.Overhead;
using Gapo.Core.Statistics;
using Gapo.Core.Telemetry;
using Gapo.Windows.Processes;
using Gapo.Windows.Telemetry;

namespace Gapo.Agent.Monitoring;

public sealed class AgentMonitor : IDisposable
{
    private const int SamplesBetweenThreadSnapshots = 2;
    private const int SamplesPerThreadSnapshotCycle = 10;
    private const int NoGame = 0;

    private readonly ProcessorTimesProbe processorProbe = new();
    private readonly ProcessorTimes[] processorTimes;
    private readonly SystemMonitor monitor;
    private readonly ProcessSnapshotReader snapshotReader = new();
    private readonly SeriesSummary gameProcessorsUsed = new();
    private readonly SeriesSummary averageLoad = new();
    private readonly SeriesSummary busiestProcessor = new();
    private readonly SeriesSummary background = new();
    private readonly SeriesSummary busiestThread = new();
    private ProcessSnapshot earlierSnapshot = new();
    private ProcessSnapshot laterSnapshot = new();
    private GameProcessProbe? game;
    private int samplesWithGame;
    private double peakWriteMegabytesPerSecond;
    private double[] loadSumPerProcessor = [];
    private long lowestAvailableMemoryBytes = long.MaxValue;
    private int memoryLoadPercent;
    private long gamePrivateBytes;
    private ProcessPriority gamePriority = ProcessPriority.Unknown;

    public AgentMonitor()
    {
        processorTimes = new ProcessorTimes[processorProbe.LogicalProcessorCount];
        monitor = new SystemMonitor(processorProbe.LogicalProcessorCount);
    }

    public MonitorSample Latest => monitor.Latest;

    public void Reconcile(int gameProcessId)
    {
        if (game is not null && (game.ProcessId != gameProcessId || !game.IsRunning))
        {
            game.Dispose();
            game = null;
            samplesWithGame = 0;
        }

        if (game is null && gameProcessId != NoGame)
        {
            game = GameProcessProbe.TryOpen(gameProcessId);
        }
    }

    public void Sample(TimeSpan at, ProcessUsage own, bool allowProcessSnapshot = true)
    {
        processorProbe.Read(processorTimes);
        var reading = default(GameProcessReading);
        var gameProcessId = game is not null && game.TryRead(out reading) ? game.ProcessId : NoGame;
        if (monitor.Record(at, processorTimes, gameProcessId, reading, own, MemoryStatusProbe.Read()) && gameProcessId != NoGame)
        {
            Accumulate(monitor.Latest);
            if (allowProcessSnapshot)
            {
                ObserveGameThreads(at, gameProcessId);
            }
        }
    }

    public void ResetSummaries()
    {
        gameProcessorsUsed.Clear();
        averageLoad.Clear();
        busiestProcessor.Clear();
        background.Clear();
        busiestThread.Clear();
        peakWriteMegabytesPerSecond = 0;
        loadSumPerProcessor = [];
        lowestAvailableMemoryBytes = long.MaxValue;
    }

    public MonitorReport Summarise() =>
        new(
            gameProcessorsUsed.Count,
            gameProcessorsUsed.Mean,
            averageLoad.Mean,
            busiestProcessor.Mean,
            background.Mean,
            busiestThread.Count == 0 ? 0 : busiestThread.Median(),
            peakWriteMegabytesPerSecond,
            gamePrivateBytes,
            monitor.Latest.MemoryLoadPercent,
            gamePriority);

    public MonitorSummary SummariseForDiagnosis()
    {
        var samples = Math.Max(1, gameProcessorsUsed.Count);
        var loads = loadSumPerProcessor.Select(sum => sum / samples).ToArray();
        return new MonitorSummary
        {
            LoadPerProcessor = loads,
            AverageLoadPercent = averageLoad.Mean,
            BusiestProcessorPercent = loads.Length == 0 ? 0 : loads.Max(),
            GameProcessorsUsed = gameProcessorsUsed.Mean,
            BusiestGameThreadPercent = busiestThread.Count == 0 ? null : busiestThread.Median(),
            BackgroundPercent = background.Mean,
            MemoryLoadPercent = memoryLoadPercent,
            LowestAvailableMemoryBytes = lowestAvailableMemoryBytes == long.MaxValue ? 0 : lowestAvailableMemoryBytes,
        };
    }

    public void Dispose()
    {
        game?.Dispose();
        snapshotReader.Dispose();
    }

    private void Accumulate(MonitorSample sample)
    {
        gameProcessorsUsed.Add(sample.GameProcessorsUsed);
        averageLoad.Add(sample.AverageLoadPercent);
        busiestProcessor.Add(sample.BusiestProcessorPercent);
        background.Add(sample.BackgroundMachinePercent);
        peakWriteMegabytesPerSecond = Math.Max(peakWriteMegabytesPerSecond, sample.GameWriteMegabytesPerSecond);
        lowestAvailableMemoryBytes = Math.Min(lowestAvailableMemoryBytes, sample.AvailablePhysicalBytes);
        memoryLoadPercent = sample.MemoryLoadPercent;
        gamePrivateBytes = sample.GamePrivateBytes;
        gamePriority = sample.GamePriority;
        var loads = monitor.LoadPerProcessor;
        if (loadSumPerProcessor.Length != loads.Length)
        {
            loadSumPerProcessor = new double[loads.Length];
        }

        for (var processor = 0; processor < loads.Length; processor++)
        {
            loadSumPerProcessor[processor] += loads[processor];
        }
    }

    private void ObserveGameThreads(TimeSpan at, int gameProcessId)
    {
        var position = samplesWithGame++ % SamplesPerThreadSnapshotCycle;
        if (position == 0)
        {
            snapshotReader.Capture(earlierSnapshot, at, gameProcessId);
        }
        else if (position == SamplesBetweenThreadSnapshots)
        {
            snapshotReader.Capture(laterSnapshot, at, gameProcessId);
            var load = ProcessActivity.BusiestThread(earlierSnapshot, laterSnapshot);
            if (load.ThreadId != 0)
            {
                busiestThread.Add(load.PercentOfOneProcessor);
            }

            (earlierSnapshot, laterSnapshot) = (laterSnapshot, earlierSnapshot);
        }
    }
}
