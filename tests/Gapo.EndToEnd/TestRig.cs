using System.Diagnostics;
using System.Globalization;
using System.Text.Json;

namespace Gapo.EndToEnd;

public sealed record FixtureResult(string Name, int ExitCode, double AverageFps, double OnePercentLowFps, double MedianWorkMs, int Frames, int WarmupFrames, string PresentPath, string Adapter);

public sealed record AgentResult(int ExitCode, bool ConnectedToGame, long FramesRead, double RunAverageCpuPercent, double PeakCpuPercent, double PeakPrivateMegabytes, bool WithinBudget, IReadOnlyList<string> Violations, FrameTimingResult FrameTiming, MonitorResult Monitor, HardwareResult Hardware, BaselineResult? Baseline, double MeasurementWindowCpuPercent, DiagnosisResult? Diagnosis, OptimisationResult? Optimisation);

public sealed record ExperimentResult(string Title, string Kind, string Decision, string Reason, double GainPercent, double PredictedSavingMs, double MeasuredSavingMs);

public sealed record OptimisationResult(bool Completed, string Error, double FpsGainPercent, double WorkReductionFactor, int Kept, int RolledBack, double BaselineFps, double FinalFps, double BaselineWorkMs, double FinalWorkMs, double BaselineStuttersPerMinute, double FinalStuttersPerMinute, double NoiseFloorPercent, int MeasurementWindows, double DurationSeconds, string PacingNote, IReadOnlyList<ExperimentResult> Experiments, IReadOnlyList<string> NotTouched, IReadOnlyDictionary<string, string> SettingsAfter);

public sealed record UnknownGameCheck(FixtureResult Fixture, UnknownGameRun Served, AgentResult Agent, string GameName)
{
    public bool SettingsUntouched { get; init; }
}

public sealed record RestartOptimisationCheck(ServedRestartRun Served, OptimisationResult Report, string OriginalSettings, string OptimisedSettings, string RevertedSettings, int Restarts)
{
    public IReadOnlyList<double> SessionFps { get; init; } = [];
}

public sealed record InterfaceRun(FixtureResult Fixture, double CpuPercent, double TypicalCpuPercent, double PeakPrivateMegabytes, double MeasuredSeconds, IReadOnlyList<string> Screenshots, int AgentsStarted, bool AgentsExitedWithTheInterface);

public sealed record DiagnosisResult(string Primary, string Title, string Summary, string EngineLimitation, IReadOnlyList<string> Evidence, IReadOnlyList<string> SecondaryProblems);

public sealed record BaselineResult(bool Succeeded, string FailureReason, int Attempts, double WaitedForWarmupSeconds, double NoiseFloorPercent, bool NoiseFloorCanBeTrusted, int FirstWindowFrames, int SecondWindowFrames, double FirstWindowMedianBusyMs, double SecondWindowMedianBusyMs);

public sealed record HardwareResult(double ScanMilliseconds, string Class, string ProcessorName, int LogicalProcessors, int Cores, long MemoryMegabytes, string ChannelMode, IReadOnlyList<string> GraphicsAdapters, string PrimaryGraphics, int DisplayWidth, int EffectiveRefreshHz, int WindowsBuild, IReadOnlyList<string> CouldNotBeRead);

public sealed record MonitorResult(int Samples, double MeanGameProcessorsUsed, double MeanMachineLoadPercent, double MeanBusiestProcessorPercent, double MeanBackgroundPercent, double MedianBusiestGameThreadPercent, double PeakGameWriteMegabytesPerSecond, double GamePrivateMegabytes);

public sealed record FrameTimingResult(string Availability, long EventsSeen, long EventsLost, int PresentedFrames, double MedianFrameMs, double MedianCpuBusyMs, double MedianInPresentMs, int HookFrames, double HookMedianFrameMs, bool OnlyTheGamesEvents = false, bool MeasuresGpuBusy = false, double GpuBusyMsPerFrame = 0)
{
    public double EventsPerPresentedFrame => PresentedFrames <= 0 ? 0 : (double)EventsSeen / PresentedFrames;

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
    private const double LongEnoughForABaselineSeconds = 45;
    private const double LongestOptimisationSeconds = 360;
    private const double LiveInterfaceRunSeconds = 84;
    private const double LiveInterfaceScreenshotAfterSeconds = 70;
    private const int InterfaceMeasureSlices = 3;
    private const double BytesPerMegabyte = 1024.0 * 1024.0;
    private const double PercentScale = 100.0;
    private const string AgentProcessName = "Gapo.Agent";

    private static readonly TimeSpan InterfaceSettleTime = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan InterfaceSliceTime = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan AgentExitAllowance = TimeSpan.FromSeconds(20);

    private double runSeconds;
    private string? fixtureScenario;
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

    private string AppExecutable => Path.Combine(binariesRoot, "Gapo.App", "release_win-x64", "GAPO.exe");

    private string AgentExecutable => Path.Combine(binariesRoot, "Gapo.Agent", "release", "Gapo.Agent.exe");

    public FixtureResult RunFixtureAlone(string name)
    {
        using var fixture = StartFixture(name, MapNameFor(name));
        return Collect(name, fixture);
    }

    public (FixtureResult Fixture, AgentResult Agent) RunFixtureWithBaselineMeasurement(string name, string? scenario = null)
    {
        fixtureScenario = scenario;
        var configuredSeconds = runSeconds;
        runSeconds = Math.Max(runSeconds, LongEnoughForABaselineSeconds);
        try
        {
            return RunFixtureWithAgent(name, "--measure-baseline");
        }
        finally
        {
            runSeconds = configuredSeconds;
            fixtureScenario = null;
        }
    }

    public string JournalPath => Path.Combine(outputDirectory, "change-journal.json");

    public string SandboxSettingsPath => Path.Combine(sandboxDataDirectory, "sandbox-settings.ini");

    public (SettingControlRun Served, OptimisationResult Report, string AntiAliasingAfter) RunSettingControl(string name)
    {
        var configuredSeconds = runSeconds;
        runSeconds = LongestOptimisationSeconds;
        var pipeName = "GAPO.Rig." + Guid.NewGuid().ToString("N");
        var journal = Path.Combine(outputDirectory, name + "-journal.json");
        SettingControlRun? served = null;
        Exception? clientFailure = null;
        string ReadSetting(string key) =>
            File.ReadAllLines(SandboxSettingsPath).Select(line => line.Split('=', 2)).Where(parts => parts.Length == 2 && parts[0].Trim() == key).Select(parts => parts[1].Split(';')[0].Trim()).FirstOrDefault() ?? string.Empty;
        var client = new Thread(() =>
        {
            try
            {
                using var link = InterfaceClient.Connect(pipeName);
                served = link.ExerciseSettingControl(ReadSetting);
            }
            catch (Exception exception) when (exception is TimeoutException or IOException or InvalidOperationException or InvalidCastException)
            {
                clientFailure = exception;
            }
        })
        { IsBackground = true, Name = "rig setting-control client" };
        try
        {
            client.Start();
            RunFixtureWithAgent(name, "--serve", "--pipe-name=" + pipeName, "--reports-dir=" + Path.Combine(outputDirectory, "reports-" + name), "--ask-game-to-close-when-done", "--journal=" + journal);
            client.Join(TimeSpan.FromSeconds(10));
            if (served is null)
            {
                throw new InvalidOperationException("The setting-control link did not complete: " + (clientFailure?.Message ?? "no report was announced"));
            }

            using var document = JsonDocument.Parse(File.ReadAllText(served.ReportPath));
            var report = ReadOptimisation(document.RootElement.GetProperty("optimisation"));
            var antiAliasing = ReadSetting("AntiAliasing");
            using var revert = Start(AgentExecutable, ["--revert-all", "--journal=" + journal]);
            WaitForExit(revert, "agent");
            return (served, report, antiAliasing);
        }
        finally
        {
            runSeconds = configuredSeconds;
        }
    }

    public (FixtureResult Fixture, AgentResult Agent, ServedRun Served) RunFixtureWithOptimisation(string name)
    {
        var configuredSeconds = runSeconds;
        runSeconds = LongestOptimisationSeconds;
        var pipeName = "GAPO.Rig." + Guid.NewGuid().ToString("N");
        ServedRun? served = null;
        Exception? clientFailure = null;
        var client = new Thread(() =>
        {
            try
            {
                using var link = InterfaceClient.Connect(pipeName);
                served = link.OptimiseAndWaitForTheReport(Gapo.Core.Optimisation.Goal.Balanced);
            }
            catch (Exception exception) when (exception is TimeoutException or IOException or InvalidOperationException)
            {
                clientFailure = exception;
            }
        })
        { IsBackground = true, Name = "rig interface client" };
        try
        {
            client.Start();
            var (fixture, agent) = RunFixtureWithAgent(
                name,
                "--serve",
                "--pipe-name=" + pipeName,
                "--reports-dir=" + Path.Combine(outputDirectory, "reports"),
                "--ask-game-to-close-when-done",
                "--journal=" + JournalPath);
            client.Join(TimeSpan.FromSeconds(10));
            if (served is null)
            {
                throw new InvalidOperationException("The interface link did not complete: " + (clientFailure?.Message ?? "no report was announced"));
            }

            return (fixture, agent, served);
        }
        finally
        {
            runSeconds = configuredSeconds;
        }
    }

    public IReadOnlyList<string> CaptureUserInterface(string agentReportName, string theme)
    {
        var directory = Path.Combine(outputDirectory, "ui-" + theme);
        using var app = Start(AppExecutable, ["--report=" + Path.Combine(outputDirectory, agentReportName), "--screenshots=" + directory, "--theme=" + theme]);
        WaitForExit(app, "user interface");
        return Directory.Exists(directory) ? Directory.GetFiles(directory).Select(file => Path.GetFileName(file)).Order(StringComparer.Ordinal).ToList() : [];
    }

    public const string UnrealGameName = "Fixture Without Its Hook";
    public const string OriginalUnrealSettings =
        "[/Script/Engine.GameUserSettings]\r\n" +
        "FrameRateLimit=0.000000\r\n" +
        "bUseVSync=False\r\n" +
        "\r\n" +
        "[ScalabilityGroups]\r\n" +
        "sg.ResolutionQuality=100.000000\r\n" +
        "sg.ViewDistanceQuality=3\r\n" +
        "sg.AntiAliasingQuality=3\r\n" +
        "sg.ShadowQuality=3\r\n" +
        "sg.PostProcessQuality=3\r\n" +
        "sg.EffectsQuality=3\r\n" +
        "sg.FoliageQuality=3\r\n" +
        "; written by the game\r\n" +
        "\r\n" +
        "[Internationalization]\r\n" +
        "Culture=en\r\n";

    private const double FirstLaunchSeconds = 8;

    private sealed record UnrealInstall(string SteamRoot, string LocalAppData, string Documents, string ShippingExecutable, string SettingsPath);

    private UnrealInstall BuildUnrealInstall(string name, bool withAntiCheat)
    {
        const string GameFolder = "Fixture Without Its Hook";
        const string ProjectName = "FixtureGame";
        const int AppId = 3_000_001;
        var root = Path.Combine(outputDirectory, "steam-" + name);
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }

        var steamRoot = Path.Combine(root, "Steam");
        var apps = Path.Combine(steamRoot, "steamapps");
        var installed = Path.Combine(apps, "common", GameFolder);
        var binaries = Path.Combine(installed, ProjectName, "Binaries", "Win64");
        var localAppData = Path.Combine(root, "LocalAppData");
        var settingsFolder = Path.Combine(localAppData, ProjectName, "Saved", "Config", "Windows");
        Directory.CreateDirectory(Path.Combine(installed, "Engine", "Binaries", "ThirdParty"));
        Directory.CreateDirectory(settingsFolder);
        if (withAntiCheat)
        {
            Directory.CreateDirectory(Path.Combine(installed, "EasyAntiCheat"));
        }

        var sandboxFolder = Path.GetDirectoryName(SandboxExecutable)!;
        foreach (var file in Directory.GetFiles(sandboxFolder, "*", SearchOption.AllDirectories))
        {
            var target = Path.Combine(binaries, Path.GetRelativePath(sandboxFolder, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target);
        }

        var shippingExecutable = Path.Combine(binaries, ProjectName + "-Win64-Shipping.exe");
        File.Move(Path.Combine(binaries, Path.GetFileName(SandboxExecutable)), shippingExecutable);
        var settingsPath = Path.Combine(settingsFolder, "GameUserSettings.ini");
        File.WriteAllText(settingsPath, OriginalUnrealSettings);
        using (var firstLaunch = Start(shippingExecutable, ["--data-dir=" + Path.Combine(root, "first-launch"), "--no-telemetry", "--autotest=" + Seconds(FirstLaunchSeconds), "--unreal-settings=" + settingsPath]))
        {
            WaitForExit(firstLaunch, "first launch of the copied game");
        }

        var escapedRoot = steamRoot.Replace(@"\", @"\\", StringComparison.Ordinal);
        File.WriteAllText(Path.Combine(apps, "libraryfolders.vdf"), $"\"libraryfolders\"\n{{\n\t\"0\"\n\t{{\n\t\t\"path\"\t\t\"{escapedRoot}\"\n\t}}\n}}\n");
        File.WriteAllText(Path.Combine(apps, $"appmanifest_{AppId}.acf"), $"\"AppState\"\n{{\n\t\"appid\"\t\t\"{AppId}\"\n\t\"name\"\t\t\"{UnrealGameName}\"\n\t\"StateFlags\"\t\t\"4\"\n\t\"installdir\"\t\t\"{GameFolder}\"\n}}\n");
        return new UnrealInstall(steamRoot, localAppData, Path.Combine(root, "Documents"), shippingExecutable, settingsPath);
    }

    private Process StartServingAgent(string name, UnrealInstall install, string pipeName, double seconds, string? journal = null)
    {
        var arguments = new List<string>
        {
            "--serve",
            "--pipe-name=" + pipeName,
            "--steam-root=" + install.SteamRoot,
            "--local-app-data=" + install.LocalAppData,
            "--documents=" + install.Documents,
            "--sandbox-map=" + MapNameFor(name),
            "--seconds=" + Seconds(seconds),
            "--report=" + Path.Combine(outputDirectory, name + "-agent.json"),
            "--reports-dir=" + Path.Combine(outputDirectory, "reports-" + name),
        };
        if (journal is not null)
        {
            arguments.Add("--journal=" + journal);
        }

        return Start(AgentExecutable, arguments);
    }

    private Process StartUnrealFixture(string name, UnrealInstall install, double seconds, int session, IEnumerable<string>? extraArguments = null) =>
        Start(install.ShippingExecutable,
        [
            "--data-dir=" + Path.Combine(outputDirectory, name + "-data"),
            "--no-telemetry",
            "--autotest=" + Seconds(seconds),
            "--log=" + ResultPath(name + "-session-" + session.ToString(CultureInfo.InvariantCulture)),
            "--unreal-settings=" + install.SettingsPath,
            .. extraArguments ?? [],
        ]);

    public UnknownGameCheck RunUnknownSteamGame(string name, IEnumerable<string>? extraGameArguments = null)
    {
        const double GameSeconds = 85;
        var configuredSeconds = runSeconds;
        runSeconds = GameSeconds;
        try
        {
            var install = BuildUnrealInstall(name, withAntiCheat: true);
            var pipeName = "GAPO.Rig." + Guid.NewGuid().ToString("N");
            using var agent = StartServingAgent(name, install, pipeName, GameSeconds + 45);
            using var client = InterfaceClient.Connect(pipeName);
            using var game = StartUnrealFixture(name, install, GameSeconds, 1, extraGameArguments);
            var served = client.WatchAnUnknownGame(UnrealGameName, TimeSpan.FromSeconds(45), TimeSpan.FromSeconds(GameSeconds + 20));
            var fixture = Collect(name + "-session-1", game);
            WaitForExit(agent, "agent");
            var untouched = File.ReadAllText(install.SettingsPath) == OriginalUnrealSettings;
            return new UnknownGameCheck(fixture, served, ReadAgentResult(Path.Combine(outputDirectory, name + "-agent.json"), agent.ExitCode), UnrealGameName) { SettingsUntouched = untouched };
        }
        finally
        {
            runSeconds = configuredSeconds;
        }
    }

    public RestartOptimisationCheck RunRestartOptimisation(string name)
    {
        const double SessionSeconds = 240;
        const double AgentSeconds = 900;
        var configuredSeconds = runSeconds;
        runSeconds = SessionSeconds;
        try
        {
            var install = BuildUnrealInstall(name, withAntiCheat: false);
            var journal = Path.Combine(outputDirectory, name + "-journal.json");
            var pipeName = "GAPO.Rig." + Guid.NewGuid().ToString("N");
            using var agent = StartServingAgent(name, install, pipeName, AgentSeconds, journal);
            using var client = InterfaceClient.Connect(pipeName);
            var session = 1;
            var game = StartUnrealFixture(name, install, SessionSeconds, session);
            var fixtureFps = new List<double>();
            void RestartTheGame()
            {
                game.CloseMainWindow();
                if (!game.WaitForExit(TimeSpan.FromSeconds(20)))
                {
                    game.Kill();
                    game.WaitForExit();
                }

                fixtureFps.Add(ReadFixtureFps(name + "-session-" + session.ToString(CultureInfo.InvariantCulture)));
                game.Dispose();
                session++;
                game = StartUnrealFixture(name, install, SessionSeconds, session);
            }

            ServedRestartRun served;
            try
            {
                served = client.OptimiseThroughRestarts(UnrealGameName, Gapo.Core.Optimisation.Goal.Balanced, RestartTheGame);
            }
            finally
            {
                game.CloseMainWindow();
                if (!game.WaitForExit(TimeSpan.FromSeconds(20)))
                {
                    game.Kill();
                }

                game.Dispose();
            }

            agent.Kill();
            agent.WaitForExit();
            var optimised = File.ReadAllText(install.SettingsPath);
            using var revert = Start(AgentExecutable, ["--revert-all", "--journal=" + journal]);
            WaitForExit(revert, "agent");
            var reverted = File.ReadAllText(install.SettingsPath);
            using var document = JsonDocument.Parse(File.ReadAllText(served.ReportPath));
            var report = ReadOptimisation(document.RootElement.GetProperty("optimisation"));
            return new RestartOptimisationCheck(served, report, OriginalUnrealSettings, optimised, reverted, session - 1) { SessionFps = fixtureFps };
        }
        finally
        {
            runSeconds = configuredSeconds;
        }
    }

    private double ReadFixtureFps(string name)
    {
        var path = ResultPath(name);
        if (!File.Exists(path))
        {
            path += ".partial.json";
        }

        if (!File.Exists(path))
        {
            return 0;
        }

        using var document = JsonDocument.Parse(File.ReadAllText(path));
        return document.RootElement.GetProperty("averageFps").GetDouble();
    }

    public InterfaceRun RunLiveInterface(string name)
    {
        var configuredSeconds = runSeconds;
        runSeconds = LiveInterfaceRunSeconds;
        try
        {
            var directory = Path.Combine(outputDirectory, "ui-live");
            var agentsBefore = AgentProcessIds();
            using var fixture = StartFixture(name, mapName: null);
            using var app = Start(AppExecutable,
            [
                "--pipe-name=GAPO.Rig." + Guid.NewGuid().ToString("N"),
                "--agent=" + AgentExecutable,
                "--screenshots=" + directory,
                "--theme=light",
                "--screenshot-after=" + Seconds(LiveInterfaceScreenshotAfterSeconds),
            ]);
            Thread.Sleep(InterfaceSettleTime);
            app.Refresh();
            if (app.HasExited)
            {
                throw new InvalidOperationException("The user interface closed before its overhead could be measured.");
            }

            var processorTimeAtStart = app.TotalProcessorTime;
            var peakPrivateBytes = app.PrivateMemorySize64;
            var clock = Stopwatch.StartNew();
            var timeline = new List<string> { "seconds,processorMilliseconds" };
            var sliceCpuPercents = new List<double>();
            var sliceStartedAt = clock.Elapsed;
            var sliceStartedWith = processorTimeAtStart;
            while (sliceCpuPercents.Count < InterfaceMeasureSlices)
            {
                Thread.Sleep(TimeSpan.FromSeconds(1));
                app.Refresh();
                if (app.HasExited)
                {
                    throw new InvalidOperationException("The user interface closed while its overhead was being measured.");
                }

                peakPrivateBytes = Math.Max(peakPrivateBytes, app.PrivateMemorySize64);
                var now = clock.Elapsed;
                var processorTimeNow = app.TotalProcessorTime;
                timeline.Add(string.Create(CultureInfo.InvariantCulture, $"{now.TotalSeconds:0.0},{(processorTimeNow - processorTimeAtStart).TotalMilliseconds:0}"));
                if (now - sliceStartedAt >= InterfaceSliceTime)
                {
                    sliceCpuPercents.Add((processorTimeNow - sliceStartedWith).TotalSeconds / (now - sliceStartedAt).TotalSeconds / Environment.ProcessorCount * PercentScale);
                    sliceStartedAt = now;
                    sliceStartedWith = processorTimeNow;
                }
            }

            File.WriteAllLines(Path.Combine(outputDirectory, name + "-processor-time.csv"), timeline);

            var measured = clock.Elapsed;
            var processorTime = app.TotalProcessorTime - processorTimeAtStart;
            var agentsStarted = AgentProcessIds().Except(agentsBefore).ToList();
            WaitForExit(app, "user interface");
            var agentsExited = WaitUntilGone(agentsStarted, AgentExitAllowance);
            var fixtureResult = Collect(name, fixture);
            var screenshots = Directory.Exists(directory) ? Directory.GetFiles(directory).Select(file => Path.GetFileName(file)).Order(StringComparer.Ordinal).ToList() : [];
            return new InterfaceRun(
                fixtureResult,
                processorTime.TotalSeconds / measured.TotalSeconds / Environment.ProcessorCount * PercentScale,
                sliceCpuPercents.Order().ElementAt(sliceCpuPercents.Count / 2),
                peakPrivateBytes / BytesPerMegabyte,
                measured.TotalSeconds,
                screenshots,
                agentsStarted.Count,
                agentsExited);
        }
        finally
        {
            runSeconds = configuredSeconds;
        }
    }

    private static List<int> AgentProcessIds()
    {
        var ids = new List<int>();
        foreach (var process in Process.GetProcessesByName(AgentProcessName))
        {
            ids.Add(process.Id);
            process.Dispose();
        }

        return ids;
    }

    private static bool WaitUntilGone(IReadOnlyCollection<int> processIds, TimeSpan allowance)
    {
        var clock = Stopwatch.StartNew();
        while (AgentProcessIds().Intersect(processIds).Any())
        {
            if (clock.Elapsed > allowance)
            {
                return false;
            }

            Thread.Sleep(250);
        }

        return true;
    }

    public int RevertAll()
    {
        using var agent = Start(AgentExecutable, ["--revert-all", "--journal=" + JournalPath]);
        WaitForExit(agent, "agent");
        return agent.ExitCode;
    }

    public (FixtureResult Fixture, AgentResult Agent) RunFixtureWithAgent(string name, params string[] extraAgentArguments)
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
            .. extraAgentArguments,
        ]);
        var fixtureResult = Collect(name, fixture);
        WaitForExit(agent, "agent");
        return (fixtureResult, ReadAgentResult(agentReport, agent.ExitCode));
    }

    private Process StartFixture(string name, string? mapName)
    {
        var arguments = new List<string>
        {
            "--data-dir=" + sandboxDataDirectory,
            "--reset",
            "--autotest=" + Seconds(runSeconds),
            "--log=" + ResultPath(name),
            "--screenshot=" + Path.Combine(outputDirectory, name + ".png"),
        };
        if (mapName is not null)
        {
            arguments.Add("--map-name=" + mapName);
        }

        if (workload is not null)
        {
            arguments.Add("--workload=" + workload);
        }

        if (fixtureScenario is not null)
        {
            arguments.Add("--scenario=" + fixtureScenario);
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
            root.TryGetProperty("frameTiming", out var frameTiming) ? ReadFrameTiming(frameTiming) : new FrameTimingResult("NotRequested", 0, 0, 0, 0, 0, 0, 0, 0),
            ReadMonitor(root.GetProperty("monitor")),
            ReadHardware(root.GetProperty("hardware")),
            root.TryGetProperty("baseline", out var baseline) ? ReadBaseline(baseline) : null,
            root.GetProperty("measurementWindowCpuPercent").GetDouble(),
            root.TryGetProperty("diagnosis", out var diagnosis) ? ReadDiagnosis(diagnosis) : null,
            root.TryGetProperty("optimisation", out var optimisation) ? ReadOptimisation(optimisation) : null);
    }

    private static OptimisationResult ReadOptimisation(JsonElement optimisation) =>
        new(
            optimisation.GetProperty("completed").GetBoolean(),
            optimisation.GetProperty("error").GetString() ?? string.Empty,
            optimisation.GetProperty("fpsGainPercent").GetDouble(),
            optimisation.GetProperty("workReductionFactor").GetDouble(),
            optimisation.GetProperty("kept").GetInt32(),
            optimisation.GetProperty("rolledBack").GetInt32(),
            optimisation.GetProperty("baseline").GetProperty("averageFps").GetDouble(),
            optimisation.GetProperty("final").GetProperty("averageFps").GetDouble(),
            optimisation.GetProperty("baseline").GetProperty("medianBusyMs").GetDouble(),
            optimisation.GetProperty("final").GetProperty("medianBusyMs").GetDouble(),
            optimisation.GetProperty("baseline").GetProperty("stuttersPerMinute").GetDouble(),
            optimisation.GetProperty("final").GetProperty("stuttersPerMinute").GetDouble(),
            optimisation.GetProperty("noiseFloorPercent").GetDouble(),
            optimisation.GetProperty("measurementWindows").GetInt32(),
            optimisation.GetProperty("durationSeconds").GetDouble(),
            optimisation.GetProperty("pacingNote").GetString() ?? string.Empty,
            optimisation.GetProperty("experiments").EnumerateArray().Select(experiment => new ExperimentResult(
                experiment.GetProperty("title").GetString() ?? string.Empty,
                experiment.GetProperty("kind").GetString() ?? string.Empty,
                experiment.GetProperty("decision").GetString() ?? string.Empty,
                experiment.GetProperty("reason").GetString() ?? string.Empty,
                experiment.GetProperty("gainPercent").GetDouble(),
                experiment.GetProperty("predictedSavingMs").GetDouble(),
                experiment.GetProperty("measuredSavingMs").GetDouble())).ToList(),
            optimisation.GetProperty("notTouched").EnumerateArray().Select(note => note.GetString() ?? string.Empty).ToList(),
            optimisation.GetProperty("settingsAfter").EnumerateObject().ToDictionary(setting => setting.Name, setting => setting.Value.GetString() ?? string.Empty));

    private static DiagnosisResult ReadDiagnosis(JsonElement diagnosis) =>
        new(
            diagnosis.GetProperty("primary").GetString() ?? string.Empty,
            diagnosis.GetProperty("title").GetString() ?? string.Empty,
            diagnosis.GetProperty("summary").GetString() ?? string.Empty,
            diagnosis.GetProperty("engineLimitation").GetString() ?? string.Empty,
            diagnosis.GetProperty("evidence").EnumerateArray().Select(sentence => sentence.GetString() ?? string.Empty).ToList(),
            diagnosis.GetProperty("secondaryProblems").EnumerateArray().Select(sentence => sentence.GetString() ?? string.Empty).ToList());

    private static BaselineResult ReadBaseline(JsonElement baseline)
    {
        var succeeded = baseline.GetProperty("succeeded").GetBoolean();
        return new BaselineResult(
            succeeded,
            baseline.GetProperty("failureReason").GetString() ?? string.Empty,
            baseline.GetProperty("attempts").GetInt32(),
            baseline.GetProperty("waitedForWarmupSeconds").GetDouble(),
            succeeded ? baseline.GetProperty("noiseFloorPercent").GetDouble() : 0,
            succeeded && baseline.GetProperty("noiseFloorCanBeTrusted").GetBoolean(),
            succeeded ? baseline.GetProperty("firstWindow").GetProperty("frames").GetInt32() : 0,
            succeeded ? baseline.GetProperty("secondWindow").GetProperty("frames").GetInt32() : 0,
            succeeded ? baseline.GetProperty("firstWindow").GetProperty("medianBusyMs").GetDouble() : 0,
            succeeded ? baseline.GetProperty("secondWindow").GetProperty("medianBusyMs").GetDouble() : 0);
    }

    private static HardwareResult ReadHardware(JsonElement hardware) =>
        new(
            hardware.GetProperty("scanMilliseconds").GetDouble(),
            hardware.GetProperty("class").GetString() ?? string.Empty,
            hardware.GetProperty("processor").GetProperty("name").GetString() ?? string.Empty,
            hardware.GetProperty("processor").GetProperty("logicalProcessors").GetInt32(),
            hardware.GetProperty("processor").GetProperty("cores").GetInt32(),
            hardware.GetProperty("memory").GetProperty("totalMegabytes").GetInt64(),
            hardware.GetProperty("memory").GetProperty("channelMode").GetString() ?? string.Empty,
            hardware.GetProperty("graphics").EnumerateArray().Select(gpu => gpu.GetProperty("name").GetString() ?? string.Empty).ToList(),
            hardware.GetProperty("primaryGraphics").GetString() ?? string.Empty,
            hardware.GetProperty("display").GetProperty("width").GetInt32(),
            hardware.GetProperty("display").GetProperty("effectiveRefreshHz").GetInt32(),
            hardware.GetProperty("windows").GetProperty("build").GetInt32(),
            hardware.GetProperty("couldNotBeRead").EnumerateArray().Select(note => note.GetString() ?? string.Empty).ToList());

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
            frameTiming.GetProperty("hookMedianFrameMs").GetDouble(),
            frameTiming.TryGetProperty("onlyTheGamesEventsWereDelivered", out var focused) && focused.GetBoolean(),
            frameTiming.TryGetProperty("measuresGpuBusy", out var measuresGpu) && measuresGpu.GetBoolean(),
            frameTiming.TryGetProperty("gpuBusyMsPerFrame", out var gpuBusy) ? gpuBusy.GetDouble() : 0);

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
