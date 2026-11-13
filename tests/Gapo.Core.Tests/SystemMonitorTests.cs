using Gapo.Core.Overhead;
using Gapo.Core.Telemetry;
using Xunit;

namespace Gapo.Core.Tests;

public sealed class SystemMonitorTests
{
    private const int Precision = 6;
    private const int Game = 77;
    private const long Megabyte = 1024 * 1024;
    private static readonly long TicksPerSecond = TimeSpan.TicksPerSecond;

    private static readonly MemoryStatus Memory = new(16_000 * Megabyte, 9_000 * Megabyte, 20_000 * Megabyte, 8_000 * Megabyte, LoadPercent: 44);

    private static long Ticks(double seconds) => (long)Math.Round(seconds * TicksPerSecond);

    private static ProcessorTimes[] Processors(params double[] busySecondsPerProcessor)
    {
        return busySecondsPerProcessor
            .Select(busy => new ProcessorTimes(0, 0, Ticks(busy)))
            .ToArray();
    }

    private static ProcessorTimes[] ProcessorsAt(double wallSeconds, params double[] busySecondsPerProcessor)
    {
        return busySecondsPerProcessor
            .Select(busy => new ProcessorTimes(
                IdleTicks: Ticks(wallSeconds - busy),
                KernelTicksIncludingIdle: Ticks(wallSeconds - busy),
                UserTicks: Ticks(busy)))
            .ToArray();
    }

    private static GameProcessReading GameAt(double processorSeconds, long privateMegabytes = 500, ulong writtenMegabytes = 0) =>
        new(TimeSpan.FromSeconds(processorSeconds), privateMegabytes * Megabyte, privateMegabytes * Megabyte, 0, writtenMegabytes * (ulong)Megabyte, ProcessPriority.Normal);

    private static ProcessUsage OwnAt(double processorSeconds) => new(TimeSpan.FromSeconds(processorSeconds), 8 * Megabyte);

    private static TimeSpan At(double seconds) => TimeSpan.FromSeconds(seconds);

    [Fact]
    public void FirstReadingOnlyEstablishesTheBaseline()
    {
        var monitor = new SystemMonitor(logicalProcessorCount: 2);

        var produced = monitor.Record(At(0), ProcessorsAt(0, 0, 0), Game, GameAt(0), OwnAt(0), Memory);

        Assert.False(produced);
        Assert.Equal(0, monitor.Count);
    }

    [Fact]
    public void SplitsMachineLoadIntoGameOptimiserAndBackground()
    {
        var monitor = new SystemMonitor(logicalProcessorCount: 4);
        monitor.Record(At(0), ProcessorsAt(0, 0, 0, 0, 0), Game, GameAt(0), OwnAt(0), Memory);

        monitor.Record(At(1), ProcessorsAt(1, 1.0, 0.4, 0.2, 0.2), Game, GameAt(1.2), OwnAt(0.02), Memory);

        var sample = monitor.Latest;
        Assert.Equal(45.0, sample.AverageLoadPercent, Precision);
        Assert.Equal(100.0, sample.BusiestProcessorPercent, Precision);
        Assert.Equal(30.0, sample.GameMachinePercent, Precision);
        Assert.Equal(1.2, sample.GameProcessorsUsed, Precision);
        Assert.Equal(0.5, sample.OwnMachinePercent, Precision);
        Assert.Equal(14.5, sample.BackgroundMachinePercent, Precision);
        Assert.Equal(44, sample.MemoryLoadPercent);
    }

    [Fact]
    public void ReportsGameDiskWritesAsARate()
    {
        var monitor = new SystemMonitor(logicalProcessorCount: 1);
        monitor.Record(At(0), ProcessorsAt(0, 0), Game, GameAt(0, writtenMegabytes: 10), OwnAt(0), Memory);

        monitor.Record(At(2), ProcessorsAt(2, 1), Game, GameAt(1, writtenMegabytes: 18), OwnAt(0), Memory);

        Assert.Equal(4.0, monitor.Latest.GameWriteMegabytesPerSecond, Precision);
    }

    [Fact]
    public void ANewGameProcessIsNotComparedWithThePreviousOne()
    {
        var monitor = new SystemMonitor(logicalProcessorCount: 2);
        monitor.Record(At(0), ProcessorsAt(0, 0, 0), Game, GameAt(900, writtenMegabytes: 5000), OwnAt(0), Memory);

        monitor.Record(At(1), ProcessorsAt(1, 0.5, 0.5), gameProcessId: Game + 1, GameAt(0.1), OwnAt(0), Memory);

        Assert.Equal(0.0, monitor.Latest.GameMachinePercent, Precision);
        Assert.Equal(0.0, monitor.Latest.GameWriteMegabytesPerSecond, Precision);
    }

    [Fact]
    public void WithoutAGameEverythingIsBackground()
    {
        var monitor = new SystemMonitor(logicalProcessorCount: 2);
        monitor.Record(At(0), ProcessorsAt(0, 0, 0), gameProcessId: 0, default, OwnAt(0), Memory);

        monitor.Record(At(1), ProcessorsAt(1, 0.2, 0.4), gameProcessId: 0, default, OwnAt(0), Memory);

        Assert.Equal(30.0, monitor.Latest.BackgroundMachinePercent, Precision);
        Assert.Equal(0, monitor.Latest.GamePrivateBytes);
        Assert.Equal(ProcessPriority.Unknown, monitor.Latest.GamePriority);
    }

    [Fact]
    public void KeepsOnlyTheMostRecentSamples()
    {
        var monitor = new SystemMonitor(logicalProcessorCount: 1, historyCapacity: 3);
        for (var second = 0; second <= 5; second++)
        {
            monitor.Record(At(second), ProcessorsAt(second, second * 0.5), Game, GameAt(0), OwnAt(0), Memory);
        }

        Assert.Equal(3, monitor.Count);
        Assert.Equal(At(3), monitor[0].At);
        Assert.Equal(At(5), monitor.Latest.At);
    }

    [Fact]
    public void ExposesLoadPerProcessorForTheLiveView()
    {
        var monitor = new SystemMonitor(logicalProcessorCount: 2);
        monitor.Record(At(0), ProcessorsAt(0, 0, 0), Game, GameAt(0), OwnAt(0), Memory);
        monitor.Record(At(1), ProcessorsAt(1, 0.9, 0.1), Game, GameAt(0), OwnAt(0), Memory);

        Assert.Equal(90.0, monitor.LoadPerProcessor[0], Precision);
        Assert.Equal(10.0, monitor.LoadPerProcessor[1], Precision);
    }

    [Fact]
    public void RejectsReadingsForADifferentProcessorCount()
    {
        var monitor = new SystemMonitor(logicalProcessorCount: 4);

        Assert.Throws<ArgumentException>(() => monitor.Record(At(0), Processors(0, 0), Game, GameAt(0), OwnAt(0), Memory));
    }
}
