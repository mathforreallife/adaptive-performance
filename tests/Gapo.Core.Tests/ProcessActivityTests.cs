using Gapo.Core.Telemetry;
using Xunit;

namespace Gapo.Core.Tests;

public sealed class ProcessActivityTests
{
    private const int Precision = 6;
    private const double CyclesPerSecond = 2_000_000_000;

    private static ProcessSample Process(int processId, ulong cycles = 0, double processorSeconds = 0, long createTime = 1, string name = "game.exe") =>
        new(processId, ParentProcessId: 1, SessionId: 1, name, createTime, cycles, KernelTicks: 0,
            UserTicks: TimeSpan.FromSeconds(processorSeconds).Ticks, PrivateBytes: 100, WorkingSetBytes: 100, ReadBytes: 0, WriteBytes: 0, ThreadCount: 1);

    private static ProcessSnapshot Snapshot(double atSeconds, int detailedProcessId, IEnumerable<ProcessSample> processes, params ThreadSample[] threads)
    {
        var snapshot = new ProcessSnapshot();
        snapshot.Begin(TimeSpan.FromSeconds(atSeconds), detailedProcessId);
        foreach (var process in processes)
        {
            snapshot.Add(process);
        }

        foreach (var thread in threads)
        {
            snapshot.AddDetailedThread(thread);
        }

        return snapshot;
    }

    private static ThreadSample Thread(int threadId, double processorSeconds) =>
        new(threadId, KernelTicks: 0, UserTicks: TimeSpan.FromSeconds(processorSeconds).Ticks);

    [Fact]
    public void UsesCycleCountsWhenTheCycleRateIsKnown()
    {
        var earlier = Process(7, cycles: 0, processorSeconds: 0);
        var later = Process(7, cycles: 4_000_000_000, processorSeconds: 0);

        var percent = ProcessActivity.MachinePercent(earlier, later, TimeSpan.FromSeconds(2), logicalProcessorCount: 4, CyclesPerSecond);

        Assert.Equal(25.0, percent, Precision);
    }

    [Fact]
    public void FallsBackToProcessTimesWithoutACycleRate()
    {
        var earlier = Process(7, processorSeconds: 1);
        var later = Process(7, processorSeconds: 2);

        var percent = ProcessActivity.MachinePercent(earlier, later, TimeSpan.FromSeconds(2), logicalProcessorCount: 2, cyclesPerSecond: null);

        Assert.Equal(25.0, percent, Precision);
    }

    [Fact]
    public void ReusedProcessIdIsNotMistakenForTheSameProcess()
    {
        var earlier = Process(7, cycles: 9_000_000_000, createTime: 100);
        var later = Process(7, cycles: 1_000_000, createTime: 200);

        var percent = ProcessActivity.MachinePercent(earlier, later, TimeSpan.FromSeconds(2), 4, CyclesPerSecond);

        Assert.Equal(0.0, percent, Precision);
    }

    [Fact]
    public void ListsOnlyProcessesPresentInBothSnapshots()
    {
        var earlier = Snapshot(0, 0, [Process(1, cycles: 0, name: "a.exe"), Process(2, cycles: 0, name: "gone.exe")]);
        var later = Snapshot(1, 0, [Process(1, cycles: 1_000_000_000, name: "a.exe"), Process(3, cycles: 5, name: "new.exe")]);
        var shares = new List<ProcessCpuShare>();

        ProcessActivity.MachinePercentPerProcess(earlier, later, logicalProcessorCount: 2, CyclesPerSecond, shares);

        var share = Assert.Single(shares);
        Assert.Equal("a.exe", share.Name);
        Assert.Equal(25.0, share.MachinePercent, Precision);
    }

    [Fact]
    public void BusiestThreadRevealsAOneThreadLimit()
    {
        var earlier = Snapshot(0, 42, [Process(42)], Thread(1, 0), Thread(2, 0), Thread(3, 0));
        var later = Snapshot(2, 42, [Process(42)], Thread(1, 1.94), Thread(2, 0.3), Thread(3, 0.1));

        var busiest = ProcessActivity.BusiestThread(earlier, later);

        Assert.Equal(1, busiest.ThreadId);
        Assert.Equal(97.0, busiest.PercentOfOneProcessor, Precision);
    }

    [Fact]
    public void ThreadsThatAppearedBetweenSnapshotsAreIgnored()
    {
        var earlier = Snapshot(0, 42, [Process(42)], Thread(1, 0));
        var later = Snapshot(1, 42, [Process(42)], Thread(1, 0.2), Thread(9, 0.9));

        Assert.Equal(1, ProcessActivity.BusiestThread(earlier, later).ThreadId);
    }

    [Fact]
    public void SnapshotsOfDifferentProcessesGiveNoThreadLoad()
    {
        var earlier = Snapshot(0, 42, [Process(42)], Thread(1, 0));
        var later = Snapshot(1, 43, [Process(43)], Thread(1, 0.9));

        Assert.Equal(default, ProcessActivity.BusiestThread(earlier, later));
    }
}
