using Gapo.Agent.Monitoring;
using Gapo.Core.Diagnosis;
using Gapo.Core.FrameTiming;
using Gapo.Core.Hardware;
using Gapo.Core.Measurement;
using Gapo.Core.Statistics;

namespace Gapo.Agent.Measurement;

public sealed class ObservedGameDiagnosis
{
    public static readonly TimeSpan ObservationLength = TimeSpan.FromSeconds(20);

    private const int FewestFramesToJudge = 100;
    private const int MostFramesKept = 12_000;
    private const int HighestDefaultTargetFps = 144;

    private readonly WarmupDetector warmup = new();
    private readonly List<MeasuredFrame> frames = new(2048);
    private readonly SeriesSummary inPresentMs = new(MostFramesKept);
    private Phase phase = Phase.WaitingForWarmup;
    private TimeSpan phaseStartedAt;

    private readonly Func<long> gpuBusyTicksSoFar;
    private long gpuBusyTicksAtStart;

    public ObservedGameDiagnosis(TimeSpan now, Func<long>? gpuBusyTicksSoFar = null)
    {
        phaseStartedAt = now;
        this.gpuBusyTicksSoFar = gpuBusyTicksSoFar ?? (() => 0);
    }

    public double? GpuBusyMsPerFrame { get; private set; }

    private enum Phase
    {
        WaitingForWarmup,
        Observing,
        Finished,
    }

    public bool IsFinished => phase == Phase.Finished;

    public BottleneckDiagnosis? Result { get; private set; }

    public FrameStatistics Statistics { get; private set; } = FrameStatistics.Empty;

    public FrameStatistics FirstHalf { get; private set; } = FrameStatistics.Empty;

    public FrameStatistics SecondHalf { get; private set; } = FrameStatistics.Empty;

    public string? FailureReason { get; private set; }

    public void OnFrame(in PresentedFrame frame)
    {
        var measured = new MeasuredFrame(frame.PresentStartMs, frame.FrameMs, frame.CpuBusyMs, 0, FrameMarks.None);
        if (phase == Phase.WaitingForWarmup)
        {
            warmup.Offer(measured);
        }
        else if (phase == Phase.Observing && frames.Count < MostFramesKept)
        {
            frames.Add(measured);
            inPresentMs.Add(frame.InPresentMs);
        }
    }

    public bool Advance(TimeSpan now, AgentMonitor monitor, HardwareInventory hardware)
    {
        switch (phase)
        {
            case Phase.WaitingForWarmup when warmup.IsWarm || now - phaseStartedAt >= WarmupDetector.LongestWait:
                monitor.ResetSummaries();
                gpuBusyTicksAtStart = gpuBusyTicksSoFar();
                phase = Phase.Observing;
                phaseStartedAt = now;
                return false;
            case Phase.Observing when now - phaseStartedAt >= ObservationLength:
                Finish(monitor, hardware);
                return true;
            default:
                return false;
        }
    }

    private void Finish(AgentMonitor monitor, HardwareInventory hardware)
    {
        phase = Phase.Finished;
        if (frames.Count < FewestFramesToJudge)
        {
            FailureReason = frames.Count == 0
                ? "No frame times were received from the game."
                : $"Only {frames.Count} frames were seen in {ObservationLength.TotalSeconds:0} s, too few to judge.";
            return;
        }

        var gpuTicks = gpuBusyTicksSoFar() - gpuBusyTicksAtStart;
        GpuBusyMsPerFrame = gpuTicks > 0 ? gpuTicks * 1000.0 / System.Diagnostics.Stopwatch.Frequency / frames.Count : null;
        var all = System.Runtime.InteropServices.CollectionsMarshal.AsSpan(frames);
        var statistics = FrameStatistics.From(all);
        Statistics = statistics;
        FirstHalf = FrameStatistics.From(all[..(all.Length / 2)]);
        SecondHalf = FrameStatistics.From(all[(all.Length / 2)..]);
        Result = Diagnostician.Diagnose(new DiagnosisInput
        {
            Frames = statistics,
            TargetFps = Math.Min(hardware.Display.EffectiveRefreshHz, HighestDefaultTargetFps),
            Monitor = monitor.SummariseForDiagnosis(),
            Processor = new ProcessorLimits(hardware.Cpu.LogicalProcessors, hardware.Cpu.MaximumMegahertz, hardware.Cpu.LimitMegahertz),
            Presents = new PresentTiming(statistics.MedianFrameMs, statistics.MedianBusyMs, inPresentMs.Median(), GpuBusyMsPerFrame),
            HitchObservation = statistics,
        });
    }
}
