using System.Diagnostics;
using Gapo.Agent.EngineHook;
using Gapo.Agent.FrameTiming;
using Gapo.Agent.Monitoring;
using Gapo.Core.Overhead;
using Gapo.Core.Statistics;
using Gapo.Sandbox.Contract;
using Gapo.Windows.Processes;

namespace Gapo.Agent;

public sealed class AgentSession
{
    private static readonly TimeSpan TelemetryReadInterval = TimeSpan.FromMilliseconds(100);
    private const int ReadsPerOverheadSample = 10;

    private readonly AgentOptions options;
    private readonly SeriesSummary hookFrameMs = new();
    private readonly SeriesSummary hookWorkMs = new();
    private readonly SeriesSummary hookPresentMs = new();

    public AgentSession(AgentOptions options)
    {
        this.options = options;
    }

    public AgentReport Run(CancellationToken cancellation)
    {
        var background = BackgroundMode.Enter();
        var probe = new OwnProcessProbe();
        var meter = new OverheadMeter(Environment.ProcessorCount);
        var clock = Stopwatch.StartNew();
        using var link = new SandboxLink(options.SandboxMapName);
        using var frameTiming = options.MeasureFrameTiming ? new FrameTimingProbe() : null;
        using var monitor = new AgentMonitor();

        meter.Record(clock.Elapsed, probe.Read(), insideMeasurementWindow: false);
        for (var read = 1; !ShouldStop(link, clock.Elapsed, cancellation); read++)
        {
            link.Reconcile();
            ReconcileFrameTiming(link, frameTiming);
            link.ReadNewFrames(frameTiming is { IsTracking: true } ? RecordHookFrame : null);
            frameTiming?.Collect();
            if (read % ReadsPerOverheadSample == 0)
            {
                var own = probe.Read();
                meter.Record(clock.Elapsed, own, insideMeasurementWindow: false);
                monitor.Reconcile(link.GameProcessId);
                monitor.Sample(clock.Elapsed, own);
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
            monitor.Summarise());
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

    private void RecordHookFrame(in FrameRecord frame)
    {
        hookFrameMs.Add(frame.FrameMs);
        hookWorkMs.Add(frame.WorkMs);
        hookPresentMs.Add(frame.PresentMs);
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

        return options.StopWhenGameExits && link.HasEverConnected && !link.IsConnected;
    }
}
