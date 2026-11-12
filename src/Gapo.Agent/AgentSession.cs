using System.Diagnostics;
using Gapo.Adapters.Sandbox;
using Gapo.Agent.EngineHook;
using Gapo.Agent.FrameTiming;
using Gapo.Agent.Measurement;
using Gapo.Agent.Monitoring;
using Gapo.Agent.Optimisation;
using Gapo.Core.Diagnosis;
using Gapo.Core.Hardware;
using Gapo.Core.Optimisation;
using Gapo.Core.Overhead;
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
    }

    public AgentReport Run()
    {
        var restoredInterruptedTrial = FileChangeJournal.RestoreInterruptedTrial(options.JournalPath);
        var hardware = HardwareScanner.Scan();
        GC.Collect();
        var background = BackgroundMode.Enter();
        meter.Record(clock.Elapsed, probe.Read(), insideMeasurementWindow: false);
        var optimisation = options.OptimisationGoal is { } goal ? Optimise(goal, hardware) : null;
        while (!ShouldStop())
        {
            Pump();
        }

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
            Diagnose(hardware),
            optimisation,
            restoredInterruptedTrial);
    }

    public void Dispose()
    {
        frameTiming?.Dispose();
        monitor.Dispose();
        link.Dispose();
    }

    private OptimisationReport Optimise(Goal goal, HardwareInventory hardware)
    {
        optimisationSession = new SandboxSession(link, monitor, Pump);
        var journal = new FileChangeJournal(options.JournalPath, () => link.SettingsPath);
        var engine = new OptimisationEngine(optimisationSession, new SandboxKnowledge(), journal, hardware, (title, detail) => Console.WriteLine($"{title} | {detail}"));
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

        link.ReadNewFrames(collectHookFrameTimes || baseline is not null || optimisationSession is not null ? ObserveHookFrame : null);
        baseline?.Advance(link, clock.Elapsed);
        frameTiming?.Collect();
        if (++reads % ReadsPerOverheadSample == 0)
        {
            var own = probe.Read();
            meter.Record(clock.Elapsed, own, insideWindow);
            monitor.Reconcile(link.GameProcessId);
            monitor.Sample(clock.Elapsed, own, allowProcessSnapshot: !insideWindow);
        }

        cancellation.WaitHandle.WaitOne(TelemetryReadInterval);
        return !cancellation.IsCancellationRequested;
    }

    private BottleneckDiagnosis? Diagnose(HardwareInventory hardware)
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

        return !options.StopWhenGameExits && (baseline is { IsFinished: true } || options.OptimisationGoal is not null);
    }
}
