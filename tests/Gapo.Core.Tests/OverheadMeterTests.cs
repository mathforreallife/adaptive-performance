using Gapo.Core.Overhead;
using Xunit;

namespace Gapo.Core.Tests;

public sealed class OverheadMeterTests
{
    private const long Megabyte = 1024 * 1024;
    private const int Precision = 6;

    private static ProcessUsage Usage(double processorSeconds, long privateBytes = 20 * Megabyte) =>
        new(TimeSpan.FromSeconds(processorSeconds), privateBytes);

    private static TimeSpan At(double seconds) => TimeSpan.FromSeconds(seconds);

    [Fact]
    public void ReportsNothingBeforeAnySample()
    {
        var summary = new OverheadMeter(logicalProcessorCount: 4).Summarise();

        Assert.Equal(OverheadSummary.Empty, summary);
    }

    [Fact]
    public void ExpressesCpuAsShareOfTheWholeMachine()
    {
        var meter = new OverheadMeter(logicalProcessorCount: 4);
        meter.Record(At(0), Usage(0), insideMeasurementWindow: false);
        meter.Record(At(10), Usage(0.4), insideMeasurementWindow: false);

        Assert.Equal(1.0, meter.Summarise().RunAverageCpuPercent, Precision);
    }

    [Fact]
    public void RunAverageCoversTheWholeRunWhileRollingCoversTheRecentSpan()
    {
        var meter = new OverheadMeter(logicalProcessorCount: 1, rollingSpan: TimeSpan.FromSeconds(10));
        meter.Record(At(0), Usage(0), insideMeasurementWindow: false);
        meter.Record(At(10), Usage(5), insideMeasurementWindow: false);
        for (var second = 11; second <= 30; second++)
        {
            meter.Record(At(second), Usage(5), insideMeasurementWindow: false);
        }

        Assert.Equal(0.0, meter.RollingCpuPercent, Precision);
        Assert.Equal(100.0 * 5 / 30, meter.Summarise().RunAverageCpuPercent, Precision);
    }

    [Fact]
    public void SeparatesCpuSpentInsideMeasurementWindows()
    {
        var meter = new OverheadMeter(logicalProcessorCount: 2);
        meter.Record(At(0), Usage(0), insideMeasurementWindow: false);
        meter.Record(At(1), Usage(0.10), insideMeasurementWindow: false);
        meter.Record(At(2), Usage(0.11), insideMeasurementWindow: true);
        meter.Record(At(3), Usage(0.12), insideMeasurementWindow: true);

        var summary = meter.Summarise();

        Assert.Equal(0.5, summary.MeasurementWindowCpuPercent, Precision);
        Assert.Equal(TimeSpan.FromSeconds(2), summary.ObservedInsideMeasurementWindows);
        Assert.Equal(5.0, summary.PeakCpuPercent, Precision);
    }

    [Fact]
    public void TracksCurrentAndPeakPrivateMemory()
    {
        var meter = new OverheadMeter(logicalProcessorCount: 4);
        meter.Record(At(0), Usage(0, 30 * Megabyte), insideMeasurementWindow: false);
        meter.Record(At(1), Usage(0, 45 * Megabyte), insideMeasurementWindow: false);
        meter.Record(At(2), Usage(0, 35 * Megabyte), insideMeasurementWindow: false);

        var summary = meter.Summarise();

        Assert.Equal(35.0, summary.PrivateMegabytes, Precision);
        Assert.Equal(45.0, summary.PeakPrivateMegabytes, Precision);
    }

    [Fact]
    public void RejectsImpossibleConfiguration()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new OverheadMeter(logicalProcessorCount: 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new OverheadMeter(4, TimeSpan.Zero));
    }
}
