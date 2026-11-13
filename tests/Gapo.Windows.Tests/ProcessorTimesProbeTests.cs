using System.Diagnostics;
using Gapo.Core.Telemetry;
using Gapo.Windows.Telemetry;
using Xunit;

namespace Gapo.Windows.Tests;

public sealed class ProcessorTimesProbeTests
{
    [Fact]
    public void ReadsOneEntryPerLogicalProcessor()
    {
        var probe = new ProcessorTimesProbe();
        var times = new ProcessorTimes[probe.LogicalProcessorCount];

        var count = probe.Read(times);

        Assert.Equal(Environment.ProcessorCount, count);
        Assert.All(times, processor => Assert.True(processor.TotalTicks > 0));
        Assert.All(times, processor => Assert.True(processor.IdleTicks <= processor.KernelTicksIncludingIdle));
    }

    [Fact]
    public void BusyThreadShowsUpAsLoadBetweenTwoReadings()
    {
        var probe = new ProcessorTimesProbe();
        var earlier = new ProcessorTimes[probe.LogicalProcessorCount];
        var later = new ProcessorTimes[probe.LogicalProcessorCount];
        var loads = new double[probe.LogicalProcessorCount];

        probe.Read(earlier);
        var clock = Stopwatch.StartNew();
        while (clock.ElapsedMilliseconds < 400)
        {
            Thread.SpinWait(500);
        }

        probe.Read(later);
        ProcessorLoad.Between(earlier, later, loads);

        Assert.All(loads, load => Assert.InRange(load, 0.0, 100.0));
        Assert.True(ProcessorLoad.Average(loads) * loads.Length >= 40.0);
    }

    [Fact]
    public void RejectsADestinationThatIsTooSmall()
    {
        var probe = new ProcessorTimesProbe(logicalProcessorCount: 4);

        Assert.Throws<ArgumentException>(() => probe.Read(new ProcessorTimes[2]));
    }
}
