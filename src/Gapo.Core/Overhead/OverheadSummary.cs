using System.Globalization;

namespace Gapo.Core.Overhead;

public sealed record OverheadSummary(
    double RunAverageCpuPercent,
    double MeasurementWindowCpuPercent,
    double PeakCpuPercent,
    double RollingCpuPercent,
    double PrivateMegabytes,
    double PeakPrivateMegabytes,
    TimeSpan Observed,
    TimeSpan ObservedInsideMeasurementWindows)
{
    public static OverheadSummary Empty { get; } = new(0, 0, 0, 0, 0, 0, TimeSpan.Zero, TimeSpan.Zero);

    public OverheadVerdict Evaluate(OverheadBudget runBudget)
    {
        var violations = new List<string>();
        if (RunAverageCpuPercent > runBudget.MaximumCpuPercent)
        {
            violations.Add(Describe("CPU averaged over the run", RunAverageCpuPercent, runBudget.MaximumCpuPercent, "%"));
        }

        if (PeakPrivateMegabytes > runBudget.MaximumPrivateMegabytes)
        {
            violations.Add(Describe("Private memory", PeakPrivateMegabytes, runBudget.MaximumPrivateMegabytes, " MB"));
        }

        var windowBudget = OverheadBudget.For(OptimiserState.MeasurementWindow);
        if (ObservedInsideMeasurementWindows > TimeSpan.Zero && MeasurementWindowCpuPercent > windowBudget.MaximumCpuPercent)
        {
            violations.Add(Describe("CPU inside measurement windows", MeasurementWindowCpuPercent, windowBudget.MaximumCpuPercent, "%"));
        }

        return new OverheadVerdict(violations.Count == 0, violations);
    }

    private static string Describe(string what, double measured, double budget, string unit) =>
        string.Create(CultureInfo.InvariantCulture, $"{what} was {measured:0.00}{unit}, over the budget of {budget:0.00}{unit}.");
}

public sealed record OverheadVerdict(bool WithinBudget, IReadOnlyList<string> Violations);
