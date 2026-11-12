using System.Diagnostics;
using System.Globalization;
using System.Text.Json;

namespace Gapo.EndToEnd;

public sealed record FixtureResult(string Name, int ExitCode, double AverageFps, double OnePercentLowFps, double MedianWorkMs, int Frames, int WarmupFrames, string PresentPath, string Adapter);

public sealed record AgentResult(int ExitCode, bool ConnectedToGame, long FramesRead, double RunAverageCpuPercent, double PeakCpuPercent, double PeakPrivateMegabytes, bool WithinBudget, IReadOnlyList<string> Violations, FrameTimingResult FrameTiming, MonitorResult Monitor);

public sealed record MonitorResult(int Samples, double MeanGameProcessorsUsed, double MeanMachineLoadPercent, double MeanBusiestProcessorPercent, double MeanBackgroundPercent, double MedianBusiestGameThreadPercent, double PeakGameWriteMegabytesPerSecond, double GamePrivateMegabytes);

public sealed record FrameTimingResult(string Availability, long EventsSeen, long EventsLost, int PresentedFrames, double MedianFrameMs, double MedianCpuBusyMs, double MedianInPresentMs, int HookFrames, double HookMedianFrameMs)
{
    private const double PercentScale = 100.0;

    public double MedianDifferencePercent => HookMedianFrameMs <= 0 ? PercentScale : Math.Abs(MedianFrameMs - HookMedianFrameMs) / HookMedianFrameMs * PercentScale;

    public double ShareOfHookFramesSeen => HookFrames <= 0 ? 0 : (double)PresentedFrames / HookFrames;
}

public sealed class TestRig
{
    private static readonly TimeSpan StartupAllowance = TimeSpan.FromSeconds(150);

    private readonly string binariesRoot;
    private readonly string outputDirectory;
    private readonly string sandboxDataDirectory;
    private readonly double runSeconds;
    private readonly string? workload;

    public TestRig(string binariesRoot, string outputDirectory, double runSeconds, string? workload)
    {
        this.binariesRoot = binariesRoot;
        this.outputDirectory = outputDirectory;
        this.runSeconds = runSeconds;
        this.workload = workload;
        sandboxDataDirectory = Path.Combine(outputDirectory, "sandbox-data");
        Directory.CreateDirectory(outputDirectory);
    }

    private string SandboxExecutable => Path.Combine(binariesRoot, "Gapo.Sandbox", "release", "GAPO-Sandbox.exe");

    private string AgentExecutable => Path.Combine(binariesRoot, "Gapo.Agent", "release", "Gapo.Agent.exe");

    public FixtureResult RunFixtureAlone(string name)
    {
        using var fixture = StartFixture(name, MapNameFor(name));
        return Collect(name, fixture);
    }

    public (FixtureResult Fixture, AgentResult Agent) RunFixtureWithAgent(string name)
    {
        var mapName = MapNameFor(name);
        var agentReport = Path.Combine(outputDirectory, name + "-agent.json");
        using var fixture = StartFixture(name, mapName);
        using var agent = Start(AgentExecutable,
        [
            "--sandbox-map=" + mapName,
            "--stop-when-game-exits",
            "--frame-timing",
            "--seconds=" + Seconds(runSeconds + StartupAllowance.TotalSeconds),
            "--report=" + agentReport,
        ]);
        var fixtureResult = Collect(name, fixture);
        WaitForExit(agent, "agent");
        return (fixtureResult, ReadAgentResult(agentReport, agent.ExitCode));
    }

    private Process StartFixture(string name, string mapName)
    {
        var arguments = new List<string>
        {
            "--data-dir=" + sandboxDataDirectory,
            "--map-name=" + mapName,
            "--reset",
            "--autotest=" + Seconds(runSeconds),
            "--log=" + ResultPath(name),
            "--screenshot=" + Path.Combine(outputDirectory, name + ".png"),
        };
        if (workload is not null)
        {
            arguments.Add("--workload=" + workload);
        }

        return Start(SandboxExecutable, arguments);
    }

    private FixtureResult Collect(string name, Process fixture)
    {
        WaitForExit(fixture, "fixture game");
        var resultPath = ResultPath(name);
        if (!File.Exists(resultPath))
        {
            var errorPath = resultPath + ".error.txt";
            var detail = File.Exists(errorPath) ? File.ReadAllText(errorPath) : "no result file was written";
            throw new InvalidOperationException($"Fixture run '{name}' produced no result (exit code {fixture.ExitCode}): {detail}");
        }

        using var document = JsonDocument.Parse(File.ReadAllText(resultPath));
        var root = document.RootElement;
        return new FixtureResult(
            name,
            fixture.ExitCode,
            root.GetProperty("averageFps").GetDouble(),
            root.GetProperty("onePercentLowFps").GetDouble(),
            root.GetProperty("medianWorkMs").GetDouble(),
            root.GetProperty("frames").GetInt32(),
            root.GetProperty("warmupFrames").GetInt32(),
            root.GetProperty("presentPath").GetString() ?? string.Empty,
            root.GetProperty("adapter").GetString() ?? string.Empty);
    }

    private static AgentResult ReadAgentResult(string reportPath, int exitCode)
    {
        if (!File.Exists(reportPath))
        {
            throw new InvalidOperationException($"The agent wrote no report (exit code {exitCode}).");
        }

        using var document = JsonDocument.Parse(File.ReadAllText(reportPath));
        var root = document.RootElement;
        return new AgentResult(
            exitCode,
            root.GetProperty("connectedToGame").GetBoolean(),
            root.GetProperty("framesRead").GetInt64(),
            root.GetProperty("runAverageCpuPercent").GetDouble(),
            root.GetProperty("peakCpuPercent").GetDouble(),
            root.GetProperty("peakPrivateMegabytes").GetDouble(),
            root.GetProperty("withinBudget").GetBoolean(),
            root.GetProperty("violations").EnumerateArray().Select(violation => violation.GetString() ?? string.Empty).ToList(),
            ReadFrameTiming(root.GetProperty("frameTiming")),
            ReadMonitor(root.GetProperty("monitor")));
    }

    private static MonitorResult ReadMonitor(JsonElement monitor) =>
        new(
            monitor.GetProperty("samples").GetInt32(),
            monitor.GetProperty("meanGameProcessorsUsed").GetDouble(),
            monitor.GetProperty("meanMachineLoadPercent").GetDouble(),
            monitor.GetProperty("meanBusiestProcessorPercent").GetDouble(),
            monitor.GetProperty("meanBackgroundPercent").GetDouble(),
            monitor.GetProperty("medianBusiestGameThreadPercent").GetDouble(),
            monitor.GetProperty("peakGameWriteMegabytesPerSecond").GetDouble(),
            monitor.GetProperty("gamePrivateMegabytes").GetDouble());

    private static FrameTimingResult ReadFrameTiming(JsonElement frameTiming) =>
        new(
            frameTiming.GetProperty("availability").GetString() ?? string.Empty,
            frameTiming.GetProperty("eventsSeen").GetInt64(),
            frameTiming.GetProperty("eventsLost").GetInt64(),
            frameTiming.GetProperty("presentedFrames").GetInt32(),
            frameTiming.GetProperty("medianFrameMs").GetDouble(),
            frameTiming.GetProperty("medianCpuBusyMs").GetDouble(),
            frameTiming.GetProperty("medianInPresentMs").GetDouble(),
            frameTiming.GetProperty("hookFrames").GetInt32(),
            frameTiming.GetProperty("hookMedianFrameMs").GetDouble());

    private void WaitForExit(Process process, string description)
    {
        var limit = TimeSpan.FromSeconds(runSeconds) + StartupAllowance;
        if (!process.WaitForExit(limit))
        {
            process.Kill(entireProcessTree: true);
            throw new TimeoutException($"The {description} did not exit within {limit.TotalSeconds:0} s.");
        }
    }

    private static Process Start(string executable, IEnumerable<string> arguments)
    {
        if (!File.Exists(executable))
        {
            throw new FileNotFoundException("Build the solution in Release first.", executable);
        }

        var startInfo = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = false };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        return Process.Start(startInfo) ?? throw new InvalidOperationException($"Could not start {executable}.");
    }

    private string ResultPath(string name) => Path.Combine(outputDirectory, name + ".json");

    private static string MapNameFor(string name) => $"GAPO_EndToEnd_{name}_{Guid.NewGuid():N}";

    private static string Seconds(double seconds) => seconds.ToString("0.###", CultureInfo.InvariantCulture);
}
