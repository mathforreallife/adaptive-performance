using System.Globalization;
using Gapo.Agent;
using Gapo.Agent.Optimisation;

const int Success = 0;
const int UsageError = 2;
const int OverBudget = 4;

Console.OutputEncoding = System.Text.Encoding.UTF8;
AgentOptions options;
try
{
    options = AgentOptions.Parse(args);
}
catch (ArgumentException exception)
{
    Console.Error.WriteLine(exception.Message);
    return UsageError;
}

if (options.RevertAll)
{
    var reverted = FileChangeJournal.RevertAll(options.JournalPath);
    Console.WriteLine(reverted ? "GAPO put every setting back to its original value." : "GAPO has no recorded changes to revert.");
    return Success;
}

using var cancellation = new CancellationTokenSource();
Console.CancelKeyPress += (_, pressed) =>
{
    pressed.Cancel = true;
    cancellation.Cancel();
};

using var session = new AgentSession(options, cancellation.Token);
var report = session.Run();
if (options.ReportPath is not null)
{
    report.WriteTo(options.ReportPath);
}

Console.WriteLine(string.Create(
    CultureInfo.InvariantCulture,
    $"GAPO agent: {report.FramesRead} frames read, {report.Overhead.RunAverageCpuPercent:0.00}% CPU, {report.Overhead.PeakPrivateMegabytes:0.0} MB private."));
if (report.Optimisation is { } optimisation)
{
    Console.WriteLine(optimisation.Completed
        ? string.Create(CultureInfo.InvariantCulture, $"GAPO result: {optimisation.FpsGainPercent:+0;-0;0}% average FPS ({optimisation.Baseline.AverageFps:0.0} to {optimisation.Final.AverageFps:0.0}), frame work {optimisation.Baseline.MedianBusyMs:0.0} to {optimisation.Final.MedianBusyMs:0.0} ms, kept {optimisation.Kept}, rolled back {optimisation.RolledBack}.")
        : "GAPO stopped: " + optimisation.Error);
}

foreach (var violation in report.Verdict.Violations)
{
    Console.Error.WriteLine("Over budget: " + violation);
}

return report.Verdict.WithinBudget ? Success : OverBudget;
