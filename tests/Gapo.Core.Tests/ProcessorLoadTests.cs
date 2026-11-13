using Gapo.Core.Telemetry;
using Xunit;

namespace Gapo.Core.Tests;

public sealed class ProcessorLoadTests
{
    private const int Precision = 6;

    [Fact]
    public void IdleProcessorHasNoLoad()
    {
        var earlier = new ProcessorTimes(IdleTicks: 100, KernelTicksIncludingIdle: 100, UserTicks: 0);
        var later = new ProcessorTimes(IdleTicks: 200, KernelTicksIncludingIdle: 200, UserTicks: 0);

        Assert.Equal(0.0, ProcessorLoad.Between(earlier, later), Precision);
    }

    [Fact]
    public void LoadIsTheShareOfTimeNotSpentIdle()
    {
        var earlier = new ProcessorTimes(IdleTicks: 0, KernelTicksIncludingIdle: 0, UserTicks: 0);
        var later = new ProcessorTimes(IdleTicks: 250, KernelTicksIncludingIdle: 400, UserTicks: 600);

        Assert.Equal(75.0, ProcessorLoad.Between(earlier, later), Precision);
    }

    [Fact]
    public void NoElapsedTimeMeansNoLoadRatherThanADivisionError()
    {
        var sample = new ProcessorTimes(10, 20, 30);

        Assert.Equal(0.0, ProcessorLoad.Between(sample, sample), Precision);
    }

    [Fact]
    public void CountersThatRunBackwardsAreClampedToAValidPercentage()
    {
        var earlier = new ProcessorTimes(IdleTicks: 500, KernelTicksIncludingIdle: 500, UserTicks: 0);
        var later = new ProcessorTimes(IdleTicks: 400, KernelTicksIncludingIdle: 600, UserTicks: 0);

        Assert.InRange(ProcessorLoad.Between(earlier, later), 0.0, 100.0);
    }

    [Fact]
    public void OneBusyThreadIsVisiblePerProcessorEvenWhenTheAverageLooksModerate()
    {
        ProcessorTimes[] earlier = [new(0, 0, 0), new(0, 0, 0), new(0, 0, 0), new(0, 0, 0)];
        ProcessorTimes[] later = [new(10, 10, 990), new(900, 950, 50), new(880, 900, 100), new(830, 900, 100)];
        var loads = new double[4];

        ProcessorLoad.Between(earlier, later, loads);

        Assert.Equal(99.0, ProcessorLoad.Busiest(loads), Precision);
        Assert.Equal((99.0 + 10.0 + 12.0 + 17.0) / 4, ProcessorLoad.Average(loads), Precision);
    }

    [Fact]
    public void RejectsSnapshotsOfDifferentProcessorCounts()
    {
        Assert.Throws<ArgumentException>(() => ProcessorLoad.Between(new ProcessorTimes[2], new ProcessorTimes[4], new double[4]));
    }
}
