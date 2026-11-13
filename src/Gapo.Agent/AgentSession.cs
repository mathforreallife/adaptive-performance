using System.Diagnostics;
using Gapo.Agent.EngineHook;
using Gapo.Agent.FrameTiming;
using Gapo.Agent.Measurement;
using Gapo.Agent.Monitoring;
using Gapo.Core.Diagnosis;
using Gapo.Core.Hardware;
using Gapo.Core.Overhead;
using Gapo.Core.Statistics;
using Gapo.Sandbox.Contract;
using Gapo.Windows.Hardware;
using Gapo.Windows.Processes;

namespace Gapo.Agent;

public sealed class AgentSession
{
    private static readonly TimeSpan TelemetryReadInterval = TimeSpan.FromMilliseconds(100);
    private const int ReadsPerOverheadSample = 10;
    private const int HighestDefaultTargetFps = 144;

    private readonly AgentOptions options;
    private readonly SeriesSummary hookFrameMs = new();
    private readonly SeriesSummary hookWorkMs = new();
    private readonly SeriesSummary hookPresentMs = new();
    private BaselineMeasurement? baseline;
    private bool collectHookFrameTimes;

    public AgentSession(AgentOptions options)
    {
        this.options = options;
    }

    public AgentReport Run(CancellationToken cancellation)
    {
        var hardware = HardwareScanner.Scan();
        GC.Collect();
        var background = BackgroundMode.Enter();
        var probe = new OwnProcessProbe();
        var meter = new OverheadMeter(Environment.ProcessorCount);
        var clock = Stopwatch.StartNew();
        using var link = new SandboxLink(options.SandboxMapName);
        using var frameTiming = options.MeasureFrameTiming ? new FrameTimingProbe() : null;
        using var monitor = new AgentMonitor();
        baseline = options.MeasureBaseline ? new BaselineMeasurement() : null;

        meter.Record(clock.Elapsed, probe.Read(), insideMeasurementWindow: false);
        for (var read = 1; !ShouldStop(link, clock.Elapsed, cancellation); read++)
        {
            link.Reconcile();
            ReconcileFrameTiming(link, frameTiming);
            collectHookFrameTimes = frameTiming is { IsTracking: true };
            var insideWindow = baseline is { IsInsideWindow: true };
            link.ReadNewFrames(collectHookFrameTimes || baseline is not null ? ObserveHookFrame : null);
            baseline?.Advance(link, clock.Elapsed);
            frameTiming?.Collect();
            if (read % ReadsPerOverheadSample == 0)
            {
                var own = probe.Read();
                meter.Record(clock.Elapsed, own, insideWindow);
                monitor.Reconcile(link.GameProcessId);
                monitor.Sample(clock.Elapsed, own, allowProcessSnapshot: !insideWindow);
            }

            cancellation.WaitHandle.WaitOne(TelemetryReadInterval);
        }

        frameTiming?.Collect();
        meter.Record(clock.Elapsed, probe.Read(), insideMeasurementWindow: false);
        var budget = OverheadBudget.For(OptimiserState.GameRunning);
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
            Diagnose(hardware, monitor, frameTiming));
    }

    private BottleneckDiagnosis? Diagnose(HardwareInventory hardware, AgentMonitor monitor, FrameTimingProbe? frameTiming)
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

    private static void ReconcileFrameTiming(SandboxLink link, FrameTimingProbe? frameTiming)
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
        if (collectHookFrameTimes)
        {
            hookFrameMs.Add(frame.FrameMs);
            hookWorkMs.Add(frame.WorkMs);
            hookPresentMs.Add(frame.PresentMs);
        }
    }

    private bool ShouldStop(SandboxLink link, TimeSpan elapsed, CancellationToken cancellation)
    {
        if (cancellation.IsCancellationRequested)
        {
            return true;
        }

        if (options.MaximumRunTime != Timeout.InfiniteTimeSpan && elapsed >= options.MaximumRunTime)
        {
            return true;
        }

        if (options.StopWhenGameExits && link.HasEverConnected && !link.IsConnected)
        {
            return true;
        }

        return baseline is { IsFinished: true } && !options.StopWhenGameExits;
    }
}
