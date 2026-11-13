using System.Diagnostics;
using Gapo.Agent.EngineHook;
using Gapo.Core.Overhead;
using Gapo.Windows.Processes;

namespace Gapo.Agent;

public sealed class AgentSession
{
    private static readonly TimeSpan TelemetryReadInterval = TimeSpan.FromMilliseconds(100);
    private const int ReadsPerOverheadSample = 10;

    private readonly AgentOptions options;

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

        meter.Record(clock.Elapsed, probe.Read(), insideMeasurementWindow: false);
        for (var read = 1; !ShouldStop(link, clock.Elapsed, cancellation); read++)
        {
            link.Reconcile();
            link.ReadNewFrames();
            if (read % ReadsPerOverheadSample == 0)
            {
                meter.Record(clock.Elapsed, probe.Read(), insideMeasurementWindow: false);
            }

            cancellation.WaitHandle.WaitOne(TelemetryReadInterval);
        }

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
            overhead.Evaluate(budget));
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
