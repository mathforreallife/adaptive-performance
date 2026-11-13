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

    public void Sample(TimeSpan at, ProcessUsage own)
    {
        processorProbe.Read(processorTimes);
        var reading = default(GameProcessReading);
        var gameProcessId = game is not null && game.TryRead(out reading) ? game.ProcessId : NoGame;
        if (monitor.Record(at, processorTimes, gameProcessId, reading, own, MemoryStatusProbe.Read()) && gameProcessId != NoGame)
        {
            Accumulate(monitor.Latest);
            ObserveGameThreads(at, gameProcessId);
        }
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
            monitor.Latest.GamePrivateBytes,
            monitor.Latest.MemoryLoadPercent,
            monitor.Latest.GamePriority);

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
