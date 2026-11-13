using Gapo.Core.Overhead;
using Xunit;

namespace Gapo.Core.Tests;

public sealed class OverheadBudgetTests
{
    private static OverheadSummary Summary(double runCpu = 0.1, double windowCpu = 0, double peakMemory = 20, double windowSeconds = 0) =>
        new(runCpu, windowCpu, PeakCpuPercent: 1, RollingCpuPercent: runCpu, PrivateMegabytes: peakMemory, PeakPrivateMegabytes: peakMemory,
            Observed: TimeSpan.FromSeconds(30), ObservedInsideMeasurementWindows: TimeSpan.FromSeconds(windowSeconds));

    [Theory]
    [InlineData(OptimiserState.AgentIdle, 0.1, 30)]
    [InlineData(OptimiserState.GameRunning, 0.3, 40)]
    [InlineData(OptimiserState.DashboardOpen, 0.5, 80)]
    [InlineData(OptimiserState.Optimising, 1.0, 100)]
    [InlineData(OptimiserState.MeasurementWindow, 0.3, 100)]
    public void BudgetsMatchTheSpecificationTable(OptimiserState state, double cpuPercent, double privateMegabytes)
    {
        var budget = OverheadBudget.For(state);

        Assert.Equal(cpuPercent, budget.MaximumCpuPercent);
        Assert.Equal(privateMegabytes, budget.MaximumPrivateMegabytes);
    }

    [Fact]
    public void AcceptsUsageWithinBudget()
    {
        var verdict = Summary(runCpu: 0.29, peakMemory: 39).Evaluate(OverheadBudget.For(OptimiserState.GameRunning));

        Assert.True(verdict.WithinBudget);
        Assert.Empty(verdict.Violations);
    }

    [Fact]
    public void RejectsCpuOverBudgetAndSaysByHowMuch()
    {
        var verdict = Summary(runCpu: 0.45).Evaluate(OverheadBudget.For(OptimiserState.GameRunning));

        Assert.False(verdict.WithinBudget);
        Assert.Contains("0.45%", Assert.Single(verdict.Violations));
    }

    [Fact]
    public void RejectsMemoryOverBudget()
    {
        var verdict = Summary(peakMemory: 41).Evaluate(OverheadBudget.For(OptimiserState.GameRunning));

        Assert.False(verdict.WithinBudget);
        Assert.Contains("41.00 MB", Assert.Single(verdict.Violations));
    }

    [Fact]
    public void RejectsCpuInsideMeasurementWindowsEvenWhenTheRunAverageIsFine()
    {
        var verdict = Summary(runCpu: 0.5, windowCpu: 0.4, windowSeconds: 9).Evaluate(OverheadBudget.For(OptimiserState.Optimising));

        Assert.False(verdict.WithinBudget);
        Assert.Contains("inside measurement windows", Assert.Single(verdict.Violations));
    }

    [Fact]
    public void IgnoresMeasurementWindowBudgetWhenNoWindowWasObserved()
    {
        var verdict = Summary(runCpu: 0.5, windowCpu: 0.4, windowSeconds: 0).Evaluate(OverheadBudget.For(OptimiserState.Optimising));

        Assert.True(verdict.WithinBudget);
    }
}
