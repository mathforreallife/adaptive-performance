using System.Diagnostics;
using Gapo.Windows.Processes;
using Xunit;

namespace Gapo.Windows.Tests;

public sealed class OwnProcessProbeTests
{
    private const long Megabyte = 1024 * 1024;

    [Fact]
    public void ReadsPlausiblePrivateMemory()
    {
        var usage = new OwnProcessProbe().Read();

        Assert.InRange(usage.PrivateBytes, 1 * Megabyte, 4096 * Megabyte);
    }

    [Fact]
    public void ProcessorTimeNeverGoesBackwards()
    {
        var probe = new OwnProcessProbe();

        var before = probe.Read().ProcessorTime;
        var after = probe.Read().ProcessorTime;

        Assert.True(after >= before);
    }

    [Fact]
    public void BusyWorkIsChargedAtRoughlyItsWallClockDuration()
    {
        var probe = new OwnProcessProbe();
        var busyFor = TimeSpan.FromMilliseconds(300);

        var before = probe.Read().ProcessorTime;
        var clock = Stopwatch.StartNew();
        while (clock.Elapsed < busyFor)
        {
            Thread.SpinWait(200);
        }

        var charged = probe.Read().ProcessorTime - before;

        Assert.InRange(charged.TotalMilliseconds, busyFor.TotalMilliseconds * 0.05, busyFor.TotalMilliseconds * 8);
    }

    [Fact]
    public void ShortWakeUpsAreNotLostWhenCyclesAreCounted()
    {
        var probe = new OwnProcessProbe();
        if (!probe.UsesCycleCounting)
        {
            return;
        }

        var before = probe.Read().ProcessorTime;
        for (var wake = 0; wake < 20; wake++)
        {
            Thread.SpinWait(2000);
            Thread.Sleep(1);
        }

        Assert.True(probe.Read().ProcessorTime > before);
    }

    [Fact]
    public void NominalCycleRateIsAPlausibleProcessorFrequency()
    {
        var rate = ProcessorCycleRate.ReadNominalCyclesPerSecond();

        Assert.NotNull(rate);
        Assert.InRange(rate.Value, 500e6, 10e9);
    }

    [Fact]
    public void WatchedProcessIsRunningUntilItExits()
    {
        using var self = ProcessLiveness.TryWatch(Environment.ProcessId);

        Assert.NotNull(self);
        Assert.True(self.IsRunning);
        Assert.Equal(Environment.ProcessId, self.ProcessId);
    }
}
