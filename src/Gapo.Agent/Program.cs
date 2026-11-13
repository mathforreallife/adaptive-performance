using System.Globalization;
using Gapo.Agent;
using Gapo.Agent.Games;
using Gapo.Agent.Optimisation;
using Gapo.Core.Games;
using Gapo.Windows.Games;

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

if (options.ListGames)
{
    var steamRoot = options.SteamRoot ?? SteamLocator.FindSteamRoot();
    if (steamRoot is null)
    {
        Console.WriteLine("Steam was not found on this PC.");
        return Success;
    }

    var scan = SteamLibraryIndex.Scan(steamRoot, new DiskGameFileSource());
    Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"Steam at {steamRoot}: {scan.Games.Count} game(s) in {scan.Libraries.Count} library folder(s)."));
    foreach (var game in scan.Games)
    {
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"  {game.AppId,8}  {game.Name}  ({(game.FullyInstalled ? "installed" : "not fully installed")}; {game.InstallDirectory})"));
        if (GameRecognition.Recognise(game, options.LocalAppDataFolder, options.DocumentsFolder) is { } recognised)
        {
            Console.WriteLine($"            {recognised.Engine}{(recognised.AntiCheat is null ? string.Empty : ", protected by " + recognised.AntiCheat)}; settings: {recognised.SettingsPath ?? "no settings file yet"}");
            if (recognised.Settings.Count > 0)
            {
                Console.WriteLine("            " + string.Join(", ", recognised.Settings));
            }
        }
    }

    foreach (var note in scan.CouldNotBeRead)
    {
        Console.WriteLine("  not read: " + note);
    }

    using var processes = new Gapo.Windows.Telemetry.ProcessSnapshotReader();
    var snapshot = new Gapo.Core.Telemetry.ProcessSnapshot();
    processes.Capture(snapshot, TimeSpan.Zero, detailedProcessId: 0);
    var running = new RunningGameFinder().Find(snapshot, scan.Games);
    Console.WriteLine(running.Count == 0 ? "No Steam game is running." : "Running now:");
    foreach (var game in running)
    {
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"  {game.Game.Name}  (process {game.ProcessId}, {Path.GetFileName(game.ImagePath)})"));
    }

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
