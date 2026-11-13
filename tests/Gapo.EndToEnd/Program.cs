using System.Globalization;
using System.Text.Json;
using Gapo.EndToEnd;

const int Passed = 0;
const int Failed = 1;
const double DefaultRunSeconds = 20;
const int MinimumFrames = 100;
const double MinimumShareOfFramesSeenByAgent = 0.9;

var outputDirectory = Path.GetFullPath(ValueOf("--output") ?? Path.Combine(Path.GetTempPath(), "gapo-end-to-end"));
var binariesRoot = Path.GetFullPath(ValueOf("--binaries") ?? Path.Combine(AppContext.BaseDirectory, "..", ".."));
var runSeconds = ValueOf("--seconds") is { } seconds ? double.Parse(seconds, CultureInfo.InvariantCulture) : DefaultRunSeconds;
var rig = new TestRig(binariesRoot, outputDirectory, runSeconds, ValueOf("--workload"));
var failures = new List<string>();

try
{
    var firstBaseline = rig.RunFixtureAlone("baseline-1");
    var (attached, agent) = rig.RunFixtureWithAgent("with-agent");
    var secondBaseline = rig.RunFixtureAlone("baseline-2");
    var comparison = OverheadComparison.Between(firstBaseline.AverageFps, secondBaseline.AverageFps, attached.AverageFps);

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

    Print($"agent       {agent.RunAverageCpuPercent:0.000}% CPU average (peak {agent.PeakCpuPercent:0.00}%), {agent.PeakPrivateMegabytes:0.0} MB private, {agent.FramesRead} frames read");
    Print($"A/B         baseline {comparison.BaselineFps:0.0} FPS, attached {comparison.AttachedFps:0.0} FPS, slowdown {comparison.SlowdownPercent:0.0}%, noise floor ±{comparison.NoiseFloorPercent:0.0}%");
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
    json.WriteNumber("agentRunAverageCpuPercent", agent.RunAverageCpuPercent);
    json.WriteNumber("agentPeakCpuPercent", agent.PeakCpuPercent);
    json.WriteNumber("agentPeakPrivateMegabytes", agent.PeakPrivateMegabytes);
    json.WriteNumber("agentFramesRead", agent.FramesRead);
    json.WriteString("presentPath", attached.PresentPath);
    json.WriteString("adapter", attached.Adapter);
    json.WriteBoolean("passed", failures.Count == 0);
    json.WriteEndObject();
}
