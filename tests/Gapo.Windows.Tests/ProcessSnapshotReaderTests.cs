using System.Diagnostics;
using Gapo.Core.Telemetry;
using Gapo.Windows.Processes;
using Gapo.Windows.Telemetry;
using Xunit;

namespace Gapo.Windows.Tests;

public sealed class ProcessSnapshotReaderTests
{
    private const long Megabyte = 1024 * 1024;
    private const double LowestShareABusyThreadGetsOnALoadedMachine = 20.0;

    private static ProcessSnapshot Capture(ProcessSnapshotReader reader, Stopwatch clock, int detailedProcessId)
    {
        var snapshot = new ProcessSnapshot();
        reader.Capture(snapshot, clock.Elapsed, detailedProcessId);
        return snapshot;
    }

    [Fact]
    public void FindsThisProcessWithPlausibleFacts()
    {
        using var reader = new ProcessSnapshotReader();
        using var self = Process.GetCurrentProcess();

        var snapshot = Capture(reader, Stopwatch.StartNew(), detailedProcessId: 0);

        Assert.True(snapshot.Processes.Count > 10);
        Assert.True(snapshot.TryFind(Environment.ProcessId, out var own));
        Assert.Equal(self.ProcessName + ".exe", own.Name, ignoreCase: true);
        Assert.Equal(self.SessionId, own.SessionId);
        Assert.InRange(own.PrivateBytes, 1 * Megabyte, 8192 * Megabyte);
        Assert.True(own.ThreadCount >= 1);
        Assert.True(own.CycleCount > 0);
    }

    [Fact]
    public void ListsSystemProcessesWithoutOpeningThem()
    {
        using var reader = new ProcessSnapshotReader();

        var snapshot = Capture(reader, Stopwatch.StartNew(), detailedProcessId: 0);

        Assert.Contains(snapshot.Processes, process => process.ProcessId == 0 && process.Name == "Idle");
        Assert.Contains(snapshot.Processes, process => process.ProcessId == 4 && process.Name == "System");
    }

    [Fact]
    public void ReturnsThreadsOnlyForTheDetailedProcess()
    {
        using var reader = new ProcessSnapshotReader();

        var withDetail = Capture(reader, Stopwatch.StartNew(), Environment.ProcessId);
        var withoutDetail = Capture(reader, Stopwatch.StartNew(), detailedProcessId: 0);

        Assert.NotEmpty(withDetail.DetailedThreads);
        Assert.Contains(withDetail.DetailedThreads, thread => thread.ThreadId == Environment.CurrentManagedThreadId || thread.ThreadId > 0);
        Assert.Empty(withoutDetail.DetailedThreads);
    }

    [Fact]
    public void BusyThreadIsReportedNearOneFullProcessor()
    {
        using var reader = new ProcessSnapshotReader();
        var clock = Stopwatch.StartNew();

        var earlier = Capture(reader, clock, Environment.ProcessId);
        var busyUntil = clock.Elapsed + TimeSpan.FromMilliseconds(1200);
        while (clock.Elapsed < busyUntil)
        {
            Thread.SpinWait(500);
        }

        var later = Capture(reader, clock, Environment.ProcessId);
        var busiest = ProcessActivity.BusiestThread(earlier, later);

        Assert.InRange(busiest.PercentOfOneProcessor, LowestShareABusyThreadGetsOnALoadedMachine, 125.0);
    }

    [Fact]
    public void ProcessCpuShareOfTheMachineMatchesOneBusyThread()
    {
        using var reader = new ProcessSnapshotReader();
        var clock = Stopwatch.StartNew();
        var cyclesPerSecond = ProcessorCycleRate.ReadNominalCyclesPerSecond();

        var earlier = Capture(reader, clock, detailedProcessId: 0);
        var busyUntil = clock.Elapsed + TimeSpan.FromMilliseconds(600);
        while (clock.Elapsed < busyUntil)
        {
            Thread.SpinWait(500);
        }

        var later = Capture(reader, clock, detailedProcessId: 0);
        Assert.True(earlier.TryFind(Environment.ProcessId, out var before));
        Assert.True(later.TryFind(Environment.ProcessId, out var after));
        var percent = ProcessActivity.MachinePercent(before, after, later.CapturedAt - earlier.CapturedAt, Environment.ProcessorCount, cyclesPerSecond);
        var oneProcessorShare = 100.0 / Environment.ProcessorCount;

        Assert.InRange(percent, oneProcessorShare * 0.4, oneProcessorShare * 4.0);
    }

    [Fact]
    public void RepeatedCapturesReuseTheSameBuffers()
    {
        using var reader = new ProcessSnapshotReader();
        var snapshot = new ProcessSnapshot();
        var clock = Stopwatch.StartNew();
        reader.Capture(snapshot, clock.Elapsed, Environment.ProcessId);
        reader.Capture(snapshot, clock.Elapsed, Environment.ProcessId);

        var allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        for (var capture = 0; capture < 5; capture++)
        {
            reader.Capture(snapshot, clock.Elapsed, Environment.ProcessId);
        }

        var allocatedPerCapture = (GC.GetAllocatedBytesForCurrentThread() - allocatedBefore) / 5;

        Assert.InRange(allocatedPerCapture, 0, 16 * 1024);
    }
}
