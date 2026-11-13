using System.Globalization;
using System.Text.Json;
using Gapo.Core.Overhead;
using Gapo.EndToEnd;

const int Passed = 0;
const int Failed = 1;
const double DefaultRunSeconds = 20;
const int MinimumFrames = 100;
const int MaximumAttempts = 3;
const double MinimumShareOfFramesSeenByAgent = 0.9;
const double MinimumShareOfFramesSeenByEventTracing = 0.85;
const double MaximumFrameTimeDisagreementPercent = 3.0;
const string FrameTimingAvailable = "Available";
const double MostEventsPerPresentedFrame = 2.5;
const int MinimumMonitorSamples = 8;
const double SlowestAcceptableHardwareScanMilliseconds = 1500;
const double LargestCpuInsideMeasurementWindowsPercent = 0.3;
const double SmallestExpectedFpsGainPercent = 25;
const int FewestExpectedKeptChanges = 2;
const int ExpectedScreenshots = 6;
const int FewestLiveSamples = 3;
const double LowestProcessorsASingleThreadedGameUses = 0.6;
const double HighestProcessorsASingleThreadedGameUses = 1.6;
const double LowestBusiestThreadShareOfASingleThreadedGame = 70.0;
const double LargestShareOfOneProcessorTheInterfaceMayUse = 0.25;
const double PercentScale = 100.0;
const double LargestLiveFpsDisagreementPercent = 10.0;
const double SmallestExpectedRestartGainPercent = 20.0;
const string SoftwareAdapterName = "Basic Render";

var outputDirectory = Path.GetFullPath(ValueOf("--output") ?? Path.Combine(Path.GetTempPath(), "gapo-end-to-end"));
var binariesRoot = Path.GetFullPath(ValueOf("--binaries") ?? Path.Combine(AppContext.BaseDirectory, "..", ".."));
var runSeconds = ValueOf("--seconds") is { } seconds ? double.Parse(seconds, CultureInfo.InvariantCulture) : DefaultRunSeconds;
var rig = new TestRig(binariesRoot, outputDirectory, runSeconds, ValueOf("--workload"));
var failures = new List<string>();
InterfaceRun? liveInterface = null;

try
{
    if (ValueOf("--only") == "interface")
    {
        rig.RunFixtureAlone("calibration");
        CheckLiveInterface();
    }
    else if (ValueOf("--only") == "restart")
    {
        rig.RunFixtureAlone("calibration");
        CheckRestartOptimisation();
    }
    else if (ValueOf("--only") == "unknown-game")
    {
        rig.RunFixtureAlone("calibration");
        CheckUnknownSteamGame();
        CheckGraphicsBoundUnknownGame();
    }
    else
    {
        CheckEverything();
    }
}
catch (Exception exception) when (exception is InvalidOperationException or TimeoutException or IOException or JsonException or KeyNotFoundException)
{
    failures.Add(exception.Message);
}

foreach (var failure in failures)
{
    Print($"FAILED: {failure}");
}

Print($"End-to-end run {(failures.Count == 0 ? "passed" : "failed")} with {failures.Count} problem(s).");
return failures.Count == 0 ? Passed : Failed;

void CheckEverything()
{
    FixtureResult firstBaseline;
    FixtureResult attached;
    FixtureResult secondBaseline;
    AgentResult agent;
    OverheadComparison comparison;
    var attempt = 0;
    do
    {
        attempt++;
        var suffix = attempt == 1 ? string.Empty : "-attempt-" + attempt.ToString(CultureInfo.InvariantCulture);
        firstBaseline = rig.RunFixtureAlone("baseline-1" + suffix);
        (attached, agent) = rig.RunFixtureWithAgent("with-agent" + suffix);
        secondBaseline = rig.RunFixtureAlone("baseline-2" + suffix);
        comparison = OverheadComparison.Between(firstBaseline.AverageFps, secondBaseline.AverageFps, attached.AverageFps);
        if (!comparison.BaselinesAgree)
        {
            Print($"attempt {attempt}: the two runs without the agent differ by {comparison.BaselineSpreadPercent:0.0}% ({firstBaseline.AverageFps:0.0} and {secondBaseline.AverageFps:0.0} FPS); the machine is too busy to judge overhead");
        }
    }
    while (!comparison.BaselinesAgree && attempt < MaximumAttempts);

    Expect(comparison.BaselinesAgree, string.Create(CultureInfo.InvariantCulture, $"The machine stayed too busy to judge overhead in {attempt} attempts (runs without the agent differed by {comparison.BaselineSpreadPercent:0.0}%)."));

    foreach (var run in new[] { firstBaseline, attached, secondBaseline })
    {
        Expect(run.ExitCode == 0, $"Fixture run '{run.Name}' exited with code {run.ExitCode}.");
        Expect(run.Frames >= MinimumFrames, $"Fixture run '{run.Name}' measured only {run.Frames} frames.");
        Print($"{run.Name,-11} {run.AverageFps,6:0.0} FPS   1% low {run.OnePercentLowFps,5:0.0}   frame work {run.MedianWorkMs,5:0.0} ms   {run.Frames} frames   {run.PresentPath} on {run.Adapter}");
    }

    Expect(agent.ConnectedToGame, "The agent never connected to the fixture game.");
    Expect(agent.FramesRead >= attached.Frames * MinimumShareOfFramesSeenByAgent, $"The agent read {agent.FramesRead} frames of the {attached.Frames} the game measured.");
    Expect(agent.WithinBudget && agent.ExitCode == 0, "The agent exceeded its overhead budget: " + string.Join(" ", agent.Violations));
    Expect(comparison.AttachedRunIsWithinNoise, string.Create(CultureInfo.InvariantCulture, $"The game ran {comparison.SlowdownPercent:0.0}% slower with the agent attached; the noise floor is {comparison.NoiseFloorPercent:0.0}%."));

    var frameTiming = agent.FrameTiming;
    Expect(frameTiming.Availability == FrameTimingAvailable, $"Frame timing through event tracing was not available: {frameTiming.Availability}.");
    Expect(frameTiming.EventsLost == 0, $"Event tracing lost {frameTiming.EventsLost} events.");
    var rendersOnHardware = attached.Adapter.Length > 0 && !attached.Adapter.Contains(SoftwareAdapterName, StringComparison.OrdinalIgnoreCase);
    Expect(!rendersOnHardware || (frameTiming.MeasuresGpuBusy && frameTiming.GpuBusyMsPerFrame > 0), $"GPU busy time was not measured on {attached.Adapter} (measuring: {frameTiming.MeasuresGpuBusy}, {frameTiming.GpuBusyMsPerFrame:0.00} ms per frame).");
    Print($"gpu busy    {(frameTiming.MeasuresGpuBusy ? string.Create(CultureInfo.InvariantCulture, $"{frameTiming.GpuBusyMsPerFrame:0.00} ms per frame") : "not measured")} on {attached.Adapter}{(rendersOnHardware ? string.Empty : " (software rendering produces no GPU packets; not checked)")}");
    Expect(frameTiming.OnlyTheGamesEvents && frameTiming.EventsPerPresentedFrame <= MostEventsPerPresentedFrame, string.Create(CultureInfo.InvariantCulture, $"Event tracing delivered {frameTiming.EventsPerPresentedFrame:0.0} events per presented frame (process filter {(frameTiming.OnlyTheGamesEvents ? "on" : "off")}); only the game's own present events should reach the agent."));
    Expect(frameTiming.ShareOfHookFramesSeen >= MinimumShareOfFramesSeenByEventTracing, $"Event tracing saw {frameTiming.PresentedFrames} presented frames while the game reported {frameTiming.HookFrames}.");
    Expect(frameTiming.MedianDifferencePercent <= MaximumFrameTimeDisagreementPercent, string.Create(CultureInfo.InvariantCulture, $"Event tracing measured a median frame time of {frameTiming.MedianFrameMs:0.00} ms; the game itself reported {frameTiming.HookMedianFrameMs:0.00} ms."));
    Print($"frame time  event tracing {frameTiming.MedianFrameMs:0.00} ms median over {frameTiming.PresentedFrames} frames, game's own telemetry {frameTiming.HookMedianFrameMs:0.00} ms over {frameTiming.HookFrames} frames ({frameTiming.MedianDifferencePercent:0.0}% apart); CPU busy {frameTiming.MedianCpuBusyMs:0.00} ms, in present {frameTiming.MedianInPresentMs:0.00} ms; {frameTiming.EventsSeen} events ({frameTiming.EventsPerPresentedFrame:0.0} per frame, process filter {(frameTiming.OnlyTheGamesEvents ? "on" : "off")}), {frameTiming.EventsLost} lost");
    var monitor = agent.Monitor;
    Expect(monitor.Samples >= MinimumMonitorSamples, $"The monitor produced only {monitor.Samples} samples.");
    Expect(monitor.MeanGameProcessorsUsed is >= LowestProcessorsASingleThreadedGameUses and <= HighestProcessorsASingleThreadedGameUses, string.Create(CultureInfo.InvariantCulture, $"The single-threaded fixture should use about one processor; the monitor measured {monitor.MeanGameProcessorsUsed:0.00}."));
    Expect(monitor.MedianBusiestGameThreadPercent >= LowestBusiestThreadShareOfASingleThreadedGame, string.Create(CultureInfo.InvariantCulture, $"The fixture's main thread should be close to a full processor; the monitor measured {monitor.MedianBusiestGameThreadPercent:0}%."));
    Expect(monitor.PeakGameWriteMegabytesPerSecond > 0, "The monitor never saw the fixture's autosave disk writes.");
    Print($"monitor     game uses {monitor.MeanGameProcessorsUsed:0.00} processors, busiest game thread {monitor.MedianBusiestGameThreadPercent:0}% of one; machine load {monitor.MeanMachineLoadPercent:0}% (busiest processor {monitor.MeanBusiestProcessorPercent:0}%), background {monitor.MeanBackgroundPercent:0.0}%; game {monitor.GamePrivateMegabytes:0} MB, peak disk write {monitor.PeakGameWriteMegabytesPerSecond:0.0} MB/s");
    var hardware = agent.Hardware;
    Expect(hardware.LogicalProcessors == Environment.ProcessorCount, $"The inventory reports {hardware.LogicalProcessors} logical processors; this machine has {Environment.ProcessorCount}.");
    Expect(hardware.MemoryMegabytes > 0 && hardware.DisplayWidth > 0 && hardware.WindowsBuild > 0, "The inventory is missing memory, display or Windows facts.");
    Expect(hardware.GraphicsAdapters.Contains(attached.Adapter), $"The game rendered on '{attached.Adapter}', which the inventory does not list ({string.Join(", ", hardware.GraphicsAdapters)}).");
    Expect(hardware.ScanMilliseconds <= SlowestAcceptableHardwareScanMilliseconds, string.Create(CultureInfo.InvariantCulture, $"The hardware scan took {hardware.ScanMilliseconds:0} ms."));
    Print($"hardware    {hardware.Class} PC: {hardware.ProcessorName}, {hardware.Cores} cores / {hardware.LogicalProcessors} threads, {hardware.MemoryMegabytes} MB ({hardware.ChannelMode}), {hardware.PrimaryGraphics}, {hardware.EffectiveRefreshHz} Hz, Windows build {hardware.WindowsBuild}; scanned in {hardware.ScanMilliseconds:0} ms; {hardware.CouldNotBeRead.Count} item(s) could not be read");
    foreach (var note in hardware.CouldNotBeRead)
    {
        Print($"            not read: {note}");
    }

    Print($"agent       {agent.RunAverageCpuPercent:0.000}% CPU average (peak {agent.PeakCpuPercent:0.00}%), {agent.PeakPrivateMegabytes:0.0} MB private, {agent.FramesRead} frames read");
    Print($"A/B         baseline {comparison.BaselineFps:0.0} FPS, attached {comparison.AttachedFps:0.0} FPS, slowdown {comparison.SlowdownPercent:0.0}%, noise floor ±{comparison.NoiseFloorPercent:0.0}%");
    var (optimisedGame, optimisingAgent, served) = rig.RunFixtureWithOptimisation("optimise");
    Expect(served.LiveSamples >= FewestLiveSamples && served.ChartPoints > 0, $"The interface received {served.LiveSamples} live samples with at most {served.ChartPoints} chart points.");
    Expect(served.SawWatching && served.SawOptimising && served.SawFinished, "The interface did not see the watching, optimising and finished states: " + string.Join(" | ", served.StatusTitles));
    Expect(File.Exists(served.ReportPath), "The report announced to the interface does not exist: " + served.ReportPath);
    Print($"link        the interface received {served.LiveSamples} live samples (first {served.FirstLiveFps:0.0} FPS, {served.ChartPoints} chart points) and {served.StatusTitles.Count} status lines; report announced at {Path.GetFileName(served.ReportPath)}");
    Expect(optimisedGame.ExitCode == 0, $"The fixture exited with code {optimisedGame.ExitCode} during the optimisation.");
    Expect(optimisingAgent.WithinBudget && optimisingAgent.ExitCode == 0, "The agent exceeded its overhead budget while optimising: " + string.Join(" ", optimisingAgent.Violations));
    Expect(optimisingAgent.MeasurementWindowCpuPercent <= LargestCpuInsideMeasurementWindowsPercent, string.Create(CultureInfo.InvariantCulture, $"The agent used {optimisingAgent.MeasurementWindowCpuPercent:0.000}% CPU inside measurement windows."));
    ExpectDiagnosis(optimisingAgent.Diagnosis, "RenderSingleThread", "the default fixture");
    Expect(optimisingAgent.Diagnosis is null || optimisingAgent.Diagnosis.SecondaryProblems.Any(problem => problem.StartsWith("Storage hitch", StringComparison.Ordinal)), "The diagnosis did not mention the autosave freezes of the fixture.");
    if (optimisingAgent.Optimisation is { } optimised)
    {
        Expect(optimised.Completed, "The optimisation did not complete: " + optimised.Error);
        Expect(optimised.FpsGainPercent >= SmallestExpectedFpsGainPercent, string.Create(CultureInfo.InvariantCulture, $"The optimisation gained only {optimised.FpsGainPercent:0}% average FPS on the deliberately wasteful fixture."));
        Expect(optimised.Kept >= FewestExpectedKeptChanges, $"Only {optimised.Kept} change(s) were kept.");
        Expect(optimised.Experiments.Count > 0 && optimised.Experiments[0].Kind == "HitchFix" && optimised.Experiments[0].Decision == "Kept", "The autosave hitch was not fixed first.");
        Expect(optimised.Experiments.All(experiment => experiment.Reason.Length > 0), "An experiment has no stated reason.");
        Expect(optimised.SettingsAfter.TryGetValue("AutosaveSeconds", out var autosave) && autosave == "300", "The final settings do not contain the hitch fix.");
        Expect(File.ReadAllText(rig.SandboxSettingsPath).Contains("AutosaveSeconds=300", StringComparison.Ordinal), "The settings file of the game does not contain the kept changes.");
        Print($"optimise    {optimised.FpsGainPercent:+0;-0;0}% average FPS ({optimised.BaselineFps:0.0} → {optimised.FinalFps:0.0}); frame work {optimised.BaselineWorkMs:0.0} → {optimised.FinalWorkMs:0.0} ms ({optimised.WorkReductionFactor:0.0}× less); stutters/min {optimised.BaselineStuttersPerMinute:0.0} → {optimised.FinalStuttersPerMinute:0.0}; kept {optimised.Kept}, rolled back {optimised.RolledBack}; {optimised.MeasurementWindows} windows in {optimised.DurationSeconds:0} s; noise floor ±{optimised.NoiseFloorPercent:0.0}%");
        Print($"            {optimised.PacingNote}");
        foreach (var experiment in optimised.Experiments)
        {
            Print($"            [{experiment.Decision}] {experiment.Title}: predicted {experiment.PredictedSavingMs:0.0} ms, measured {experiment.MeasuredSavingMs:0.0} ms ({experiment.GainPercent:+0;-0;0}%)");
        }

        foreach (var note in optimised.NotTouched)
        {
            Print($"            not changed: {note}");
        }

        Print($"            agent while optimising: {optimisingAgent.RunAverageCpuPercent:0.000}% CPU average, {optimisingAgent.MeasurementWindowCpuPercent:0.000}% inside windows, {optimisingAgent.PeakPrivateMegabytes:0.0} MB private");
        foreach (var theme in new[] { "light", "dark" })
        {
            var screenshots = rig.CaptureUserInterface("optimise-agent.json", theme);
            Expect(screenshots.Count(file => file.EndsWith(".png", StringComparison.Ordinal)) == ExpectedScreenshots, $"The user interface produced {string.Join(", ", screenshots)} in the {theme} theme; expected the result view and five detail tabs.");
            Print($"interface   {theme} theme: {string.Join(", ", screenshots)}");
        }

        Expect(rig.RevertAll() == 0, "Revert all failed.");
        var reverted = File.ReadAllText(rig.SandboxSettingsPath);
        Expect(reverted.Contains("AutosaveSeconds=10 ", StringComparison.Ordinal) && reverted.Contains("RenderThreads=1 ", StringComparison.Ordinal) && reverted.Contains("AntiAliasing=SSAA4x", StringComparison.Ordinal), "Revert all did not restore the original settings file.");
        Print($"revert all  the settings file of the game is back to its original values");
    }
    else
    {
        Expect(false, "The agent reported no optimisation result.");
    }

    CheckLiveInterface();
    CheckUnknownSteamGame();
    CheckGraphicsBoundUnknownGame();
    CheckRestartOptimisation();
    var (_, simulationAgent) = rig.RunFixtureWithBaselineMeasurement("simulation-bound", scenario: "simulation-bound");
    ExpectDiagnosis(simulationAgent.Diagnosis, "CpuMainThread", "the simulation-bound fixture");
    Expect(simulationAgent.Diagnosis is null || simulationAgent.Diagnosis.EngineLimitation.Length > 0, "The simulation-bound diagnosis did not explain the engine limitation.");

    WriteSummary(firstBaseline, attached, secondBaseline, agent, comparison);
}

void CheckGraphicsBoundUnknownGame()
{
    var check = rig.RunUnknownSteamGame("unknown-gpu-game", ["--gpu-load=900", "--workload=160,300,0.45"]);
    var rendersOnHardware = check.Fixture.Adapter.Length > 0 && !check.Fixture.Adapter.Contains(SoftwareAdapterName, StringComparison.OrdinalIgnoreCase);
    var diagnosis = check.Served.Diagnosis;
    Expect(!rendersOnHardware || diagnosis is { Bottleneck: "GraphicsCard" }, $"The hook-less fixture with heavy graphics load should be diagnosed as limited by the graphics card on {check.Fixture.Adapter}; the agent said: {diagnosis?.Title ?? "nothing"}. {diagnosis?.Summary}");
    Print($"unknown gpu {check.Fixture.AverageFps:0.0} FPS on {check.Fixture.Adapter}; diagnosis: {diagnosis?.Title ?? "none"}. {diagnosis?.Summary}{(rendersOnHardware ? string.Empty : " (software rendering: not checked)")}");
}

void CheckUnknownSteamGame()
{
    var check = rig.RunUnknownSteamGame("unknown-game");
    var served = check.Served;
    var budget = OverheadBudget.For(OptimiserState.DashboardOpen);
    var disagreement = check.Fixture.AverageFps <= 0 ? PercentScale : Math.Abs(served.MedianLiveFps - check.Fixture.AverageFps) / check.Fixture.AverageFps * PercentScale;
    Expect(check.Fixture.ExitCode == 0, $"The fixture run as an unknown Steam game exited with code {check.Fixture.ExitCode}.");
    Expect(served.LiveSamplesWithFrames >= FewestLiveSamples, $"The interface received only {served.LiveSamplesWithFrames} live samples with frame times for the unknown game.");
    Expect(disagreement <= LargestLiveFpsDisagreementPercent, string.Create(CultureInfo.InvariantCulture, $"Event tracing showed {served.MedianLiveFps:0.0} FPS for the unknown game; the game itself measured {check.Fixture.AverageFps:0.0} FPS."));
    Expect(!served.EverOfferedToOptimise, "The interface was offered Optimise for a game GAPO has no settings knowledge of.");
    Expect(served.RefusedToOptimise, "The agent did not refuse to optimise a game it does not know.");
    Expect(served.WatchingDetail.Contains("Unreal Engine game protected by Easy Anti-Cheat", StringComparison.Ordinal) && served.WatchingDetail.EndsWith("Nothing was changed.", StringComparison.Ordinal), "The interface was not told that the game is protected by anti-cheat and that nothing was changed: " + served.WatchingDetail);
    Expect(check.SettingsUntouched, "The settings file of a game protected by anti-cheat was changed.");
    Expect(served.SawTheGameLeave, "The agent did not notice that the unknown game exited.");
    Expect(served.Diagnosis is { Bottleneck: "CpuOneThreadLimit" }, $"The unknown single-threaded fixture should be diagnosed as limited by one CPU thread; the agent said: {served.Diagnosis?.Title ?? "nothing"}. {served.Diagnosis?.Summary}");
    Expect(!check.Agent.ConnectedToGame, "The agent connected to the fixture's engine hook although the fixture ran without it.");
    Expect(check.Agent.RunAverageCpuPercent <= budget.MaximumCpuPercent && check.Agent.PeakPrivateMegabytes <= budget.MaximumPrivateMegabytes, string.Create(CultureInfo.InvariantCulture, $"The agent used {check.Agent.RunAverageCpuPercent:0.000}% CPU and {check.Agent.PeakPrivateMegabytes:0.0} MB while watching an unknown game; the budget with the interface open is {budget.MaximumCpuPercent:0.0}% / {budget.MaximumPrivateMegabytes:0} MB."));
    Print($"unknown     '{check.GameName}' found through the Steam library and the process list, measured through event tracing only: {served.MedianLiveFps:0.0} FPS live vs {check.Fixture.AverageFps:0.0} FPS from the game ({disagreement:0.0}% apart) over {served.LiveSamplesWithFrames} samples; Optimise {(served.RefusedToOptimise ? "refused with a reason" : "NOT refused")}; exit {(served.SawTheGameLeave ? "noticed" : "missed")}; diagnosis: {served.Diagnosis?.Title ?? "none"}. {served.Diagnosis?.Summary}; agent {check.Agent.RunAverageCpuPercent:0.000}% CPU, {check.Agent.PeakPrivateMegabytes:0.0} MB");
}

void CheckRestartOptimisation()
{
    var check = rig.RunRestartOptimisation("restart");
    var report = check.Report;
    Expect(check.Served.OfferedToOptimise, "The interface was not offered Optimise for an Unreal Engine game with a readable settings file.");
    Expect(report.Completed, "The restart-based optimisation did not complete: " + report.Error);
    Expect(check.Served.RestartsAsked >= 2 && check.Restarts == check.Served.RestartsAsked, $"The agent asked for {check.Served.RestartsAsked} restarts and the game was restarted {check.Restarts} times.");
    Expect(report.Kept >= 1, "No change was kept.");
    Expect(report.FpsGainPercent >= SmallestExpectedRestartGainPercent, string.Create(CultureInfo.InvariantCulture, $"The restart-based optimisation gained only {report.FpsGainPercent:0}% average FPS."));
    var originalLines = check.OriginalSettings.Split("\r\n");
    var optimisedLines = check.OptimisedSettings.Split("\r\n");
    var changedLines = originalLines.Zip(optimisedLines).Count(pair => pair.First != pair.Second);
    Expect(originalLines.Length == optimisedLines.Length && changedLines == report.Kept, $"The settings file should differ from the original in exactly the {report.Kept} kept line(s); {changedLines} line(s) differ and the line count went from {originalLines.Length} to {optimisedLines.Length}.");
    Expect(check.RevertedSettings == check.OriginalSettings, "Revert all did not restore the settings file byte for byte.");
    Print($"restart     {report.FpsGainPercent:+0;-0;0}% average FPS ({report.BaselineFps:0.0} → {report.FinalFps:0.0}); frame work {report.BaselineWorkMs:0.0} → {report.FinalWorkMs:0.0} ms; kept {report.Kept}, rolled back {report.RolledBack}; {check.Restarts} game restarts in {report.DurationSeconds:0} s; noise floor ±{report.NoiseFloorPercent:0.0}%");
    foreach (var experiment in report.Experiments)
    {
        Print($"            [{experiment.Decision}] {experiment.Title}: {experiment.GainPercent:+0;-0;0}%");
    }

    Print($"            settings file: {changedLines} line(s) changed; revert all restored it byte for byte: {check.RevertedSettings == check.OriginalSettings}");
}

void CheckLiveInterface()
{
    var run = rig.RunLiveInterface("live-interface");
    liveInterface = run;
    var budget = OverheadBudget.For(OptimiserState.DashboardOpen);
    var rendersOnHardware = !run.Fixture.Adapter.Contains(SoftwareAdapterName, StringComparison.OrdinalIgnoreCase) && run.Fixture.Adapter.Length > 0;
    Expect(run.Fixture.ExitCode == 0, $"The fixture exited with code {run.Fixture.ExitCode} while the interface was open.");
    Expect(run.Screenshots.Contains("home.png"), "The interface never showed live data from the game: " + string.Join(", ", run.Screenshots));
    Expect(run.AgentsStarted == 1, $"The interface should start exactly one agent; {run.AgentsStarted} appeared.");
    Expect(run.AgentsExitedWithTheInterface, "The agent the interface started was still running after the interface closed.");
    var processorsUsed = run.CpuPercent * Environment.ProcessorCount / PercentScale;
    Expect(processorsUsed <= LargestShareOfOneProcessorTheInterfaceMayUse, string.Create(CultureInfo.InvariantCulture, $"The interface used {processorsUsed:0.00} of a logical processor ({run.CpuPercent:0.00}% of the PC) next to a running game; something in it is spinning."));
    Expect(!rendersOnHardware || run.TypicalCpuPercent <= budget.MaximumCpuPercent, string.Create(CultureInfo.InvariantCulture, $"The interface typically used {run.TypicalCpuPercent:0.00}% of the processor next to a running game ({run.CpuPercent:0.00}% over the whole {run.MeasuredSeconds:0} s); its budget is {budget.MaximumCpuPercent:0.0}%."));
    Expect(run.PeakPrivateMegabytes <= budget.MaximumPrivateMegabytes, string.Create(CultureInfo.InvariantCulture, $"The interface used {run.PeakPrivateMegabytes:0.0} MB of private memory; its budget is {budget.MaximumPrivateMegabytes:0} MB."));
    Print($"interface   live next to the game for {run.MeasuredSeconds:0} s: {run.TypicalCpuPercent:0.00}% CPU in the median 15 s slice, {run.CpuPercent:0.00}% overall, {run.PeakPrivateMegabytes:0.0} MB private (budget {budget.MaximumCpuPercent:0.0}% / {budget.MaximumPrivateMegabytes:0} MB{(rendersOnHardware ? string.Empty : "; CPU budget not enforced with software rendering")}); it started {run.AgentsStarted} agent, which {(run.AgentsExitedWithTheInterface ? "exited with it" : "kept running")}; game {run.Fixture.AverageFps:0.0} FPS");
}

void Expect(bool condition, string failure)
{
    if (!condition)
    {
        failures.Add(failure);
    }
}

void ExpectDiagnosis(DiagnosisResult? diagnosis, string expectedPrimary, string what)
{
    if (diagnosis is null)
    {
        failures.Add($"No diagnosis was produced for {what}.");
        return;
    }

    Expect(diagnosis.Primary == expectedPrimary, $"The diagnosis for {what} was {diagnosis.Primary}; expected {expectedPrimary}.");
    Print($"diagnosis   {what}: {diagnosis.Title}. {diagnosis.Summary}");
    foreach (var problem in diagnosis.SecondaryProblems)
    {
        Print($"            also: {problem}");
    }
}

void Print(FormattableString message) => Console.WriteLine(message.ToString(CultureInfo.InvariantCulture));

string? ValueOf(string name)
{
    var prefix = name + "=";
    return args.FirstOrDefault(argument => argument.StartsWith(prefix, StringComparison.Ordinal))?[prefix.Length..];
}

void WriteSummary(FixtureResult firstBaseline, FixtureResult attached, FixtureResult secondBaseline, AgentResult agent, OverheadComparison comparison)
{
    using var stream = File.Create(Path.Combine(outputDirectory, "summary.json"));
    using var json = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true });
    json.WriteStartObject();
    json.WriteNumber("baselineFirstFps", firstBaseline.AverageFps);
    json.WriteNumber("baselineSecondFps", secondBaseline.AverageFps);
    json.WriteNumber("attachedFps", attached.AverageFps);
    json.WriteNumber("slowdownPercent", Math.Round(comparison.SlowdownPercent, 3));
    json.WriteNumber("noiseFloorPercent", Math.Round(comparison.NoiseFloorPercent, 3));
    json.WriteNumber("baselineSpreadPercent", Math.Round(comparison.BaselineSpreadPercent, 3));
    json.WriteNumber("agentRunAverageCpuPercent", agent.RunAverageCpuPercent);
    json.WriteNumber("agentPeakCpuPercent", agent.PeakCpuPercent);
    json.WriteNumber("agentPeakPrivateMegabytes", agent.PeakPrivateMegabytes);
    json.WriteNumber("agentFramesRead", agent.FramesRead);
    json.WriteNumber("eventTracingMedianFrameMs", agent.FrameTiming.MedianFrameMs);
    json.WriteNumber("gameTelemetryMedianFrameMs", agent.FrameTiming.HookMedianFrameMs);
    json.WriteNumber("eventTracingFrames", agent.FrameTiming.PresentedFrames);
    json.WriteString("presentPath", attached.PresentPath);
    json.WriteString("adapter", attached.Adapter);
    if (liveInterface is { } live)
    {
        json.WriteNumber("interfaceCpuPercent", Math.Round(live.CpuPercent, 3));
        json.WriteNumber("interfaceTypicalCpuPercent", Math.Round(live.TypicalCpuPercent, 3));
        json.WriteNumber("interfacePeakPrivateMegabytes", Math.Round(live.PeakPrivateMegabytes, 1));
    }

    json.WriteBoolean("passed", failures.Count == 0);
    json.WriteEndObject();
}
