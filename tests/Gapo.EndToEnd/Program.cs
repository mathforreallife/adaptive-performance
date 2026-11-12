using System.Globalization;
using System.Text.Json;
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
const int MinimumMonitorSamples = 8;
const double SlowestAcceptableHardwareScanMilliseconds = 1500;
const int FewestFramesInAMeasurementWindow = 45;
const double LargestCpuInsideMeasurementWindowsPercent = 0.3;
const double LowestProcessorsASingleThreadedGameUses = 0.6;
const double HighestProcessorsASingleThreadedGameUses = 1.6;
const double LowestBusiestThreadShareOfASingleThreadedGame = 70.0;

var outputDirectory = Path.GetFullPath(ValueOf("--output") ?? Path.Combine(Path.GetTempPath(), "gapo-end-to-end"));
var binariesRoot = Path.GetFullPath(ValueOf("--binaries") ?? Path.Combine(AppContext.BaseDirectory, "..", ".."));
var runSeconds = ValueOf("--seconds") is { } seconds ? double.Parse(seconds, CultureInfo.InvariantCulture) : DefaultRunSeconds;
var rig = new TestRig(binariesRoot, outputDirectory, runSeconds, ValueOf("--workload"));
var failures = new List<string>();

try
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
    Expect(frameTiming.ShareOfHookFramesSeen >= MinimumShareOfFramesSeenByEventTracing, $"Event tracing saw {frameTiming.PresentedFrames} presented frames while the game reported {frameTiming.HookFrames}.");
    Expect(frameTiming.MedianDifferencePercent <= MaximumFrameTimeDisagreementPercent, string.Create(CultureInfo.InvariantCulture, $"Event tracing measured a median frame time of {frameTiming.MedianFrameMs:0.00} ms; the game itself reported {frameTiming.HookMedianFrameMs:0.00} ms."));
    Print($"frame time  event tracing {frameTiming.MedianFrameMs:0.00} ms median over {frameTiming.PresentedFrames} frames, game's own telemetry {frameTiming.HookMedianFrameMs:0.00} ms over {frameTiming.HookFrames} frames ({frameTiming.MedianDifferencePercent:0.0}% apart); CPU busy {frameTiming.MedianCpuBusyMs:0.00} ms, in present {frameTiming.MedianInPresentMs:0.00} ms; {frameTiming.EventsSeen} events, {frameTiming.EventsLost} lost");
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
    var (measuredGame, measuringAgent) = rig.RunFixtureWithBaselineMeasurement("baseline-measurement");
    Expect(measuredGame.ExitCode == 0, $"The fixture exited with code {measuredGame.ExitCode} during the baseline measurement.");
    if (measuringAgent.Baseline is { } measured)
    {
        Expect(measured.Succeeded, "The baseline measurement failed: " + measured.FailureReason);
        Expect(measured.NoiseFloorCanBeTrusted, string.Create(CultureInfo.InvariantCulture, $"The two baseline windows disagreed too much after {measured.Attempts} attempt(s) (noise floor {measured.NoiseFloorPercent:0.0}%)."));
        Expect(measured.FirstWindowFrames >= FewestFramesInAMeasurementWindow && measured.SecondWindowFrames >= FewestFramesInAMeasurementWindow, $"The baseline windows held {measured.FirstWindowFrames} and {measured.SecondWindowFrames} frames.");
        Expect(measured.WaitedForWarmupSeconds > 0, "The agent did not wait for the game to finish warming up.");
        Expect(measuringAgent.MeasurementWindowCpuPercent <= LargestCpuInsideMeasurementWindowsPercent, string.Create(CultureInfo.InvariantCulture, $"The agent used {measuringAgent.MeasurementWindowCpuPercent:0.000}% CPU inside measurement windows."));
        Print($"baseline    waited {measured.WaitedForWarmupSeconds:0.0} s for warm-up; windows {measured.FirstWindowMedianBusyMs:0.00} and {measured.SecondWindowMedianBusyMs:0.00} ms frame work ({measured.FirstWindowFrames} and {measured.SecondWindowFrames} frames); noise floor ±{measured.NoiseFloorPercent:0.0}% after {measured.Attempts} attempt(s); agent {measuringAgent.MeasurementWindowCpuPercent:0.000}% CPU inside windows");
    }
    else
    {
        Expect(false, "The agent reported no baseline measurement.");
    }

    ExpectDiagnosis(measuringAgent.Diagnosis, "RenderSingleThread", "the default fixture");
    Expect(measuringAgent.Diagnosis is null || measuringAgent.Diagnosis.SecondaryProblems.Any(problem => problem.StartsWith("Storage hitch", StringComparison.Ordinal)), "The diagnosis did not mention the fixture's autosave freezes.");
    var (_, simulationAgent) = rig.RunFixtureWithBaselineMeasurement("simulation-bound", scenario: "simulation-bound");
    ExpectDiagnosis(simulationAgent.Diagnosis, "CpuMainThread", "the simulation-bound fixture");
    Expect(simulationAgent.Diagnosis is null || simulationAgent.Diagnosis.EngineLimitation.Length > 0, "The simulation-bound diagnosis did not explain the engine limitation.");

    WriteSummary(firstBaseline, attached, secondBaseline, agent, comparison);
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
    json.WriteBoolean("passed", failures.Count == 0);
    json.WriteEndObject();
}
