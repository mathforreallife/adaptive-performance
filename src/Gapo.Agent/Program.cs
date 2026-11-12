using System.Globalization;
using Gapo.Agent;

const int Success = 0;
const int UsageError = 2;
const int OverBudget = 4;

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

using var cancellation = new CancellationTokenSource();
Console.CancelKeyPress += (_, pressed) =>
{
    pressed.Cancel = true;
    cancellation.Cancel();
};

var report = new AgentSession(options).Run(cancellation.Token);
if (options.ReportPath is not null)
{
    report.WriteTo(options.ReportPath);
}

Console.WriteLine(string.Create(
    CultureInfo.InvariantCulture,
    $"GAPO agent: {report.FramesRead} frames read, {report.Overhead.RunAverageCpuPercent:0.00}% CPU, {report.Overhead.PeakPrivateMegabytes:0.0} MB private."));
foreach (var violation in report.Verdict.Violations)
{
    Console.Error.WriteLine("Over budget: " + violation);
}

return report.Verdict.WithinBudget ? Success : OverBudget;
