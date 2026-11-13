using System.Diagnostics;
using Gapo.Adapters.Sandbox;
using Gapo.Agent.EngineHook;
using Gapo.Agent.FrameTiming;
using Gapo.Agent.Games;
using Gapo.Agent.Link;
using Gapo.Agent.Measurement;
using Gapo.Agent.Monitoring;
using Gapo.Agent.Optimisation;
using Gapo.Core.Diagnosis;
using Gapo.Core.FrameTiming;
using Gapo.Core.Games;
using Gapo.Core.Hardware;
using Gapo.Core.Optimisation;
using Gapo.Core.Measurement;
using Gapo.Core.Overhead;
using Gapo.Core.Protocol;
using Gapo.Core.Statistics;
using Gapo.Sandbox.Contract;
using Gapo.Windows.FrameTiming;
using Gapo.Windows.Games;
using Gapo.Windows.Hardware;
using Gapo.Windows.Processes;

namespace Gapo.Agent;

public sealed class AgentSession : IDisposable
{
    private static readonly TimeSpan TelemetryReadInterval = TimeSpan.FromMilliseconds(100);
    private const int ReadsPerOverheadSample = 10;
    private const int HighestDefaultTargetFps = 144;
    private const string LastReportFileName = "last-report.json";
    private static readonly TimeSpan LiveSpan = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan BudgetJudgementSpan = TimeSpan.FromSeconds(30);

    private readonly AgentOptions options;
    private readonly CancellationToken cancellation;
    private readonly SeriesSummary hookFrameMs = new();
    private readonly SeriesSummary hookWorkMs = new();
    private readonly SeriesSummary hookPresentMs = new();
    private readonly OwnProcessProbe probe = new();
    private readonly OverheadMeter meter = new(Environment.ProcessorCount);
    private readonly OverheadMeter budgetMeter = new(Environment.ProcessorCount, BudgetJudgementSpan);
    private readonly Stopwatch clock = Stopwatch.StartNew();
    private readonly SandboxLink link;
    private readonly SteamGameWatcher? steamGames;
    private FrameTimingProbe? frameTiming;
    private bool frameTimingIsOnlyForLiveData;
    private string? announcedGame;
    private ObservedGameDiagnosis? observedDiagnosis;
    private DiagnosisMessage? latestDiagnosis;
    private RecognisedGame? recognisedGame;
    private RestartOptimiser? restartOptimiser;
    private readonly AgentMonitor monitor = new();
    private readonly BaselineMeasurement? baseline;
    private readonly AgentPipeServer? server;
    private readonly ProcessLiveness? owner;
    private readonly LiveFrameWindow liveFrames = new(LiveSpan);
    private HardwareInventory hardware = new();
    private BackgroundModeResult background;
    private bool restoredInterruptedTrial;
    private bool stopRequested;
    private SandboxSession? optimisationSession;
    private bool collectHookFrameTimes;
    private bool wasInsideWindow;
    private int reads;

    public AgentSession(AgentOptions options, CancellationToken cancellation)
    {
        this.options = options;
        this.cancellation = cancellation;
        link = new SandboxLink(options.SandboxMapName);
        frameTiming = options.MeasureFrameTiming ? new FrameTimingProbe() : null;
        baseline = options.MeasureBaseline ? new BaselineMeasurement() : null;
        server = options.Serve ? new AgentPipeServer(options.PipeName) : null;
        steamGames = options.Serve ? new SteamGameWatcher(options.SteamRoot) : null;
        owner = options.ExitWithProcessId > 0 ? ProcessLiveness.TryWatch(options.ExitWithProcessId) : null;
    }

    public AgentReport Run()
    {
        restoredInterruptedTrial = FileChangeJournal.RestoreInterruptedTrial(options.JournalPath) | IniChangeJournal.RestoreInterruptedTrial(options.IniJournalPath);
        hardware = HardwareScanner.Scan();
        GC.Collect();
        background = BackgroundMode.Enter();
        meter.Record(clock.Elapsed, probe.Read(), insideMeasurementWindow: false);
        var optimisation = options.OptimisationGoal is { } goal ? Optimise(goal) : null;
        while (!ShouldStop())
        {
            Pump();
            optimisation = HandleCommands() ?? optimisation;
        }

        return BuildReport(optimisation);
    }

    private AgentReport BuildReport(OptimisationReport? optimisation)
    {
        frameTiming?.Collect();
        meter.Record(clock.Elapsed, probe.Read(), insideMeasurementWindow: false);
        var budget = OverheadBudget.For(optimisation is null ? OptimiserState.GameRunning : OptimiserState.Optimising);
        var overhead = meter.Summarise();
        return new AgentReport(
            link.HasEverConnected,
            link.FramesRead,
            background.EfficiencyModeEnabled,
            background.PriorityLowered,
            probe.UsesCycleCounting,
            budget,
            overhead,
            overhead.Evaluate(budget),
            frameTiming is null || frameTimingIsOnlyForLiveData ? null : FrameTimingReport.From(frameTiming, hookFrameMs, hookWorkMs, hookPresentMs),
            monitor.Summarise(),
            hardware,
            baseline,
            Diagnose(),
            optimisation,
            restoredInterruptedTrial);
    }

    public void Dispose()
    {
        server?.Dispose();
        owner?.Dispose();
        steamGames?.Dispose();
        frameTiming?.Dispose();
        monitor.Dispose();
        link.Dispose();
    }

    private OptimisationReport? HandleCommands()
    {
        OptimisationReport? finished = null;
        while (server is not null && server.TryTakeCommand(out var command))
        {
            switch (command)
            {
                case OptimiseCommand optimise when link.IsConnected:
                    finished = Optimise(optimise.Goal);
                    PublishReport(finished);
                    break;
                case OptimiseCommand optimise when restartOptimiser is null && SteamGame is { } supported && recognisedGame is { CanBeOptimised: true, SettingsPath: { } settingsPath }:
                    StartRestartOptimisation(optimise.Goal, supported, settingsPath);
                    break;
                case OptimiseCommand when restartOptimiser is not null:
                    break;
                case StopCommand when restartOptimiser is not null:
                    var stopped = restartOptimiser.Stop(clock.Elapsed);
                    restartOptimiser = null;
                    PublishReport(stopped);
                    finished = stopped;
                    break;
                case OptimiseCommand when SteamGame is { } unknown:
                    server.Send(new StatusMessage(AgentState.Watching, unknown.Game.Name + " cannot be optimised yet", GameRecognition.Explain(recognisedGame, NotSupportedYet)));
                    break;
                case OptimiseCommand:
                    server.Send(new StatusMessage(AgentState.NoGame, "No game is running", "Start a game first; GAPO measures the game while it runs."));
                    break;
                case RevertAllCommand:
                    if (restartOptimiser is not null)
                    {
                        restartOptimiser.Stop(clock.Elapsed);
                        restartOptimiser = null;
                    }

                    var reverted = FileChangeJournal.RevertAll(options.JournalPath) | IniChangeJournal.RevertAll(options.IniJournalPath);
                    server.Send(new StatusMessage(
                        link.IsConnected || SteamGame is not null ? AgentState.Watching : AgentState.NoGame,
                        reverted ? "Every change was reverted" : "Nothing to revert",
                        reverted ? "The game is back to its original settings." : "GAPO has no recorded changes for this game."));
                    break;
            }
        }

        return finished;
    }

    private void PublishReport(OptimisationReport optimisation)
    {
        var path = Path.Combine(options.ReportsDirectory, LastReportFileName);
        BuildReport(optimisation).WriteTo(path);
        server?.Send(new StatusMessage(
            optimisation.Completed ? AgentState.Finished : AgentState.Failed,
            optimisation.Completed ? "Done" : "Optimisation stopped",
            optimisation.Completed ? string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{optimisation.FpsGainPercent:+0;-0;0} % average FPS") : optimisation.Error ?? string.Empty));
        server?.Send(new ReportReadyMessage(path));
    }

    private void ReportProgress(string title, string detail)
    {
        Console.WriteLine($"{title} | {detail}");
        server?.Send(new StatusMessage(AgentState.Optimising, title, detail));
    }

    private void PublishLiveSample(bool insideWindow)
    {
        if (server is null || !server.HasClient)
        {
            announcedGame = null;
            return;
        }

        var steam = SteamGame;
        var gameName = link.IsConnected ? SandboxKnowledge.GameName : steam?.Game.Name ?? string.Empty;
        var gameKey = link.IsConnected ? "fixture" : steam is null ? string.Empty : "steam:" + steam.Game.AppId.ToString(System.Globalization.CultureInfo.InvariantCulture);
        if (gameKey != announcedGame && optimisationSession is null && restartOptimiser is null)
        {
            announcedGame = gameKey;
            server.Send(gameKey.Length == 0
                ? new StatusMessage(AgentState.NoGame, "No game is running", "Start a game and GAPO will find out what limits it on this PC.")
                : link.IsConnected
                    ? new StatusMessage(AgentState.Watching, "Watching " + gameName, "GAPO is measuring quietly. Choose a goal and optimise when you are ready.")
                    : new StatusMessage(AgentState.Watching, "Watching " + gameName, frameTiming is { Availability: FrameTimingAvailability.Available }
                        ? GameRecognition.Explain(recognisedGame, NotSupportedYet)
                        : GameRecognition.Explain(recognisedGame, NotSupportedYet) + " Frame times are not available either: reading them needs administrator rights or membership of the Performance Log Users group."));
            if (latestDiagnosis is { } diagnosis && diagnosis.GameName == gameName && !link.IsConnected)
            {
                server.Send(diagnosis);
            }
        }

        if (insideWindow)
        {
            return;
        }

        var frames = liveFrames.Summarise();
        var latest = monitor.Latest;
        var ownBudget = OverheadBudget.For(optimisationSession is null ? OptimiserState.DashboardOpen : OptimiserState.Optimising);
        var judgedCpu = clock.Elapsed >= BudgetJudgementSpan ? budgetMeter.RollingCpuPercent : 0;
        var gameRunning = gameKey.Length > 0;
        server.Send(new LiveSampleMessage(
            gameRunning,
            gameName,
            frames.AverageFps,
            frames.MedianFrameMs,
            frames.MedianBusyMs,
            frames.StuttersPerMinute,
            latest.GameProcessorsUsed,
            latest.AverageLoadPercent,
            meter.RollingCpuPercent,
            meter.PrivateMegabytes,
            judgedCpu > ownBudget.MaximumCpuPercent || meter.PrivateMegabytes > ownBudget.MaximumPrivateMegabytes,
            gameRunning ? liveFrames.ChartPoints() : [],
            CanOptimise: link.IsConnected || (steam is not null && recognisedGame is { CanBeOptimised: true })));
    }

    private OptimisationReport Optimise(Goal goal)
    {
        stopRequested = false;
        optimisationSession = new SandboxSession(link, monitor, Pump);
        var journal = new FileChangeJournal(options.JournalPath, () => link.SettingsPath);
        var engine = new OptimisationEngine(optimisationSession, new SandboxKnowledge(), journal, hardware, ReportProgress);
        var report = engine.Run(goal);
        optimisationSession = null;
        if (options.AskGameToCloseWhenDone)
        {
            link.TrySendCommand(SandboxCommand.Quit, 0);
        }

        return report;
    }

    private bool Pump()
    {
        link.Reconcile();
        ReconcileFrameTiming();
        collectHookFrameTimes = frameTiming is { IsTracking: true };
        var insideWindow = baseline is { IsInsideWindow: true } || optimisationSession is { IsInsideWindow: true };
        if (insideWindow != wasInsideWindow)
        {
            meter.Record(clock.Elapsed, probe.Read(), wasInsideWindow);
            wasInsideWindow = insideWindow;
        }

        if (!link.IsConnected && SteamGame is null)
        {
            liveFrames.Clear();
        }

        link.ReadNewFrames(collectHookFrameTimes || baseline is not null || optimisationSession is not null || server is not null ? ObserveHookFrame : null);
        baseline?.Advance(link, clock.Elapsed);
        frameTiming?.Collect();
        if (++reads % ReadsPerOverheadSample == 0)
        {
            var own = probe.Read();
            meter.Record(clock.Elapsed, own, insideWindow);
            budgetMeter.Record(clock.Elapsed, own, insideWindow);
            WatchForSteamGames();
            monitor.Reconcile(link.IsConnected ? link.GameProcessId : SteamGame?.ProcessId ?? 0);
            monitor.Sample(clock.Elapsed, own, allowProcessSnapshot: !insideWindow);
            AdvanceObservedDiagnosis();
            AdvanceRestartOptimisation();
            PublishLiveSample(insideWindow);
        }

        if (optimisationSession is not null && server is not null)
        {
            while (server.TryTakeCommand(out var command))
            {
                stopRequested |= command is StopCommand;
            }
        }

        cancellation.WaitHandle.WaitOne(TelemetryReadInterval);
        return !cancellation.IsCancellationRequested && !stopRequested;
    }

    private BottleneckDiagnosis? Diagnose()
    {
        if (baseline?.Result is not { Succeeded: true } measured)
        {
            return null;
        }

        return Diagnostician.Diagnose(new DiagnosisInput
        {
            Frames = measured.Combined,
            TargetFps = Math.Min(hardware.Display.EffectiveRefreshHz, HighestDefaultTargetFps),
            Monitor = monitor.SummariseForDiagnosis(),
            Processor = new ProcessorLimits(hardware.Cpu.LogicalProcessors, hardware.Cpu.MaximumMegahertz, hardware.Cpu.LimitMegahertz),
            Stages = measured.Stages,
            Presents = frameTiming is { FrameMs.Count: > 0 }
                ? new PresentTiming(frameTiming.FrameMs.Median(), frameTiming.CpuBusyMs.Median(), frameTiming.InPresentMs.Median(), frameTiming.MeasuresGpuBusy ? frameTiming.GpuBusyMsPerFrame : null)
                : null,
            HitchObservation = baseline.ObservedSinceWarm(),
        });
    }

    private RunningGame? SteamGame => link.IsConnected ? null : steamGames?.Current;

    private const string NotSupportedYet = "GAPO measures this game while it runs, but it does not know this game's settings yet, so it cannot change anything. " + GameRecognition.NothingChanged;

    private void WatchForSteamGames()
    {
        if (steamGames is null || optimisationSession is not null)
        {
            return;
        }

        if (link.IsConnected)
        {
            steamGames.Forget();
            return;
        }

        if (steamGames.Poll(clock.Elapsed))
        {
            liveFrames.Clear();
            latestDiagnosis = null;
            observedDiagnosis = steamGames.Current is null ? null : new ObservedGameDiagnosis(clock.Elapsed, GpuBusyTicksSoFar);
            recognisedGame = steamGames.Current is { } found ? GameRecognition.Recognise(found.Game, options.LocalAppDataFolder, options.DocumentsFolder) : null;
            ReconcileFrameTiming();
            if (observedDiagnosis is not null)
            {
                frameTiming?.StartGpuTiming();
            }
        }
    }

    private void AdvanceObservedDiagnosis()
    {
        if (observedDiagnosis is not { IsFinished: false } observing || SteamGame is not { } game || !observing.Advance(clock.Elapsed, monitor, hardware))
        {
            return;
        }

        if (observing.Result is not { } result)
        {
            Console.WriteLine($"No diagnosis for {game.Game.Name}: {observing.FailureReason}");
            return;
        }

        latestDiagnosis = new DiagnosisMessage(
            game.Game.Name,
            result.Primary.ToString(),
            result.Title,
            result.Summary,
            result.SecondaryProblems,
            string.Create(System.Globalization.CultureInfo.InvariantCulture, $"Observed for {ObservedGameDiagnosis.ObservationLength.TotalSeconds:0} s while you played, from frame times and processor use, without changing anything. GAPO could not repeat the same scene, so this describes those {ObservedGameDiagnosis.ObservationLength.TotalSeconds:0} s."));
        Console.WriteLine($"Diagnosis for {game.Game.Name}: {result.Title}. {result.Summary}");
        server?.Send(latestDiagnosis);
    }

    private void ReconcileFrameTiming()
    {
        var target = link.IsConnected ? link.GameProcessId : SteamGame?.ProcessId ?? 0;
        if (frameTiming is null && target != 0 && !link.IsConnected && server is not null)
        {
            frameTiming = new FrameTimingProbe(keepsSummaries: false) { FrameObserver = ObservePresentedFrame };
            frameTimingIsOnlyForLiveData = true;
        }

        if (frameTiming is null)
        {
            return;
        }

        if (target != 0 && frameTiming.TrackedProcessId != target)
        {
            if (link.IsConnected)
            {
                link.SkipBacklog();
            }

            frameTiming.Track(target);
        }
        else if (target == 0 && frameTiming.TrackedProcessId != 0)
        {
            frameTiming.StopTracking();
            if (frameTimingIsOnlyForLiveData)
            {
                frameTiming.Dispose();
                frameTiming = null;
                frameTimingIsOnlyForLiveData = false;
            }
        }
    }

    private void StartRestartOptimisation(Goal goal, RunningGame game, string settingsPath)
    {
        if (IniSettingsFile.Read(settingsPath) is not var (document, _))
        {
            server?.Send(new StatusMessage(AgentState.Failed, "The settings file could not be read", settingsPath));
            return;
        }

        frameTiming?.StartGpuTiming();
        restartOptimiser = new RestartOptimiser(
            game.Game.Name,
            settingsPath,
            Gapo.Core.Games.Unreal.UnrealSettings.Read(document),
            new IniChangeJournal(options.IniJournalPath),
            goal,
            monitor,
            hardware,
            (state, title, detail) => ReportProgressAs(state, title, detail),
            clock.Elapsed,
            game.ProcessId,
            GpuBusyTicksSoFar);
    }

    private long GpuBusyTicksSoFar() => frameTiming?.GpuBusyTicksOfTrackedProcess ?? 0;

    private void AdvanceRestartOptimisation()
    {
        if (restartOptimiser is null)
        {
            return;
        }

        if (restartOptimiser.Advance(clock.Elapsed, SteamGame?.ProcessId ?? 0) is { } finished)
        {
            restartOptimiser = null;
            announcedGame = null;
            PublishReport(finished);
        }
    }

    private void ReportProgressAs(AgentState state, string title, string detail)
    {
        Console.WriteLine($"{title} | {detail}");
        server?.Send(new StatusMessage(state, title, detail));
    }

    private void ObservePresentedFrame(PresentedFrame frame)
    {
        restartOptimiser?.OnFrame(frame);
        if (!link.IsConnected)
        {
            liveFrames.Add(new MeasuredFrame(frame.PresentStartMs, frame.FrameMs, frame.CpuBusyMs, 0, FrameMarks.None));
            observedDiagnosis?.OnFrame(frame);
        }
    }

    private void ObserveHookFrame(in FrameRecord frame)
    {
        baseline?.OnFrame(frame);
        optimisationSession?.OnFrame(frame);
        if (server is not null)
        {
            liveFrames.Add(new MeasuredFrame(frame.TimeMs, frame.FrameMs, frame.WorkMs, frame.Revision, FrameMarks.None));
        }
        if (collectHookFrameTimes)
        {
            hookFrameMs.Add(frame.FrameMs);
            hookWorkMs.Add(frame.WorkMs);
            hookPresentMs.Add(frame.PresentMs);
        }
    }

    private bool ShouldStop()
    {
        if (cancellation.IsCancellationRequested)
        {
            return true;
        }

        if (options.MaximumRunTime != Timeout.InfiniteTimeSpan && clock.Elapsed >= options.MaximumRunTime)
        {
            return true;
        }

        if (options.StopWhenGameExits && link.HasEverConnected && !link.IsConnected)
        {
            return true;
        }

        if (options.ExitWithProcessId > 0 && owner is not { IsRunning: true })
        {
            return true;
        }

        return !options.StopWhenGameExits && !options.Serve && (baseline is { IsFinished: true } || options.OptimisationGoal is not null);
    }
}
