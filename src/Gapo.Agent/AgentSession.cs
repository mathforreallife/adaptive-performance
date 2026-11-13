using System.Diagnostics;
using Gapo.Adapters.Sandbox;
using Gapo.Agent.EngineHook;
using Gapo.Agent.FrameTiming;
using Gapo.Agent.Link;
using Gapo.Agent.Measurement;
using Gapo.Agent.Monitoring;
using Gapo.Agent.Optimisation;
using Gapo.Core.Diagnosis;
using Gapo.Core.Hardware;
using Gapo.Core.Optimisation;
using Gapo.Core.Measurement;
using Gapo.Core.Overhead;
using Gapo.Core.Protocol;
using Gapo.Core.Statistics;
using Gapo.Sandbox.Contract;
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

    private readonly AgentOptions options;
    private readonly CancellationToken cancellation;
    private readonly SeriesSummary hookFrameMs = new();
    private readonly SeriesSummary hookWorkMs = new();
    private readonly SeriesSummary hookPresentMs = new();
    private readonly OwnProcessProbe probe = new();
    private readonly OverheadMeter meter = new(Environment.ProcessorCount);
    private readonly Stopwatch clock = Stopwatch.StartNew();
    private readonly SandboxLink link;
    private readonly FrameTimingProbe? frameTiming;
    private readonly AgentMonitor monitor = new();
    private readonly BaselineMeasurement? baseline;
    private readonly AgentPipeServer? server;
    private readonly LiveFrameWindow liveFrames = new(LiveSpan);
    private HardwareInventory hardware = new();
    private BackgroundModeResult background;
    private bool restoredInterruptedTrial;
    private bool stopRequested;
    private bool wasConnected;
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
    }

    public AgentReport Run()
    {
        restoredInterruptedTrial = FileChangeJournal.RestoreInterruptedTrial(options.JournalPath);
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
            frameTiming is null ? null : FrameTimingReport.From(frameTiming, hookFrameMs, hookWorkMs, hookPresentMs),
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
                case OptimiseCommand:
                    server.Send(new StatusMessage(AgentState.NoGame, "No game is running", "Start a game first; GAPO measures the game while it runs."));
                    break;
                case RevertAllCommand:
                    var reverted = FileChangeJournal.RevertAll(options.JournalPath);
                    server.Send(new StatusMessage(
                        link.IsConnected ? AgentState.Watching : AgentState.NoGame,
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
            return;
        }

        if (link.IsConnected != wasConnected && optimisationSession is null)
        {
            wasConnected = link.IsConnected;
            server.Send(wasConnected
                ? new StatusMessage(AgentState.Watching, "Watching " + SandboxKnowledge.GameName, "GAPO is measuring quietly. Choose a goal and optimise when you are ready.")
                : new StatusMessage(AgentState.NoGame, "No game is running", "Start a game and GAPO will find out what limits it on this PC."));
        }

        if (insideWindow)
        {
            return;
        }

        var frames = liveFrames.Summarise();
        var latest = monitor.Latest;
        var ownBudget = OverheadBudget.For(optimisationSession is null ? OptimiserState.GameRunning : OptimiserState.Optimising);
        server.Send(new LiveSampleMessage(
            link.IsConnected,
            link.IsConnected ? SandboxKnowledge.GameName : string.Empty,
            frames.AverageFps,
            frames.MedianFrameMs,
            frames.MedianBusyMs,
            frames.StuttersPerMinute,
            latest.GameProcessorsUsed,
            latest.AverageLoadPercent,
            meter.RollingCpuPercent,
            meter.PrivateMegabytes,
            meter.RollingCpuPercent > ownBudget.MaximumCpuPercent || meter.PrivateMegabytes > ownBudget.MaximumPrivateMegabytes,
            link.IsConnected ? liveFrames.ChartPoints() : []));
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

        if (!link.IsConnected)
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
            monitor.Reconcile(link.GameProcessId);
            monitor.Sample(clock.Elapsed, own, allowProcessSnapshot: !insideWindow);
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
                ? new PresentTiming(frameTiming.FrameMs.Median(), frameTiming.CpuBusyMs.Median(), frameTiming.InPresentMs.Median())
                : null,
            HitchObservation = baseline.ObservedSinceWarm(),
        });
    }

    private void ReconcileFrameTiming()
    {
        if (frameTiming is null)
        {
            return;
        }

        if (link.IsConnected && !frameTiming.IsTracking)
        {
            link.SkipBacklog();
            frameTiming.Track(link.GameProcessId);
        }
        else if (!link.IsConnected && frameTiming.IsTracking)
        {
            frameTiming.StopTracking();
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

        return !options.StopWhenGameExits && !options.Serve && (baseline is { IsFinished: true } || options.OptimisationGoal is not null);
    }
}
