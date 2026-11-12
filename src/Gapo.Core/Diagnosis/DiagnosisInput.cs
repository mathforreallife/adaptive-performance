using Gapo.Core.Measurement;

namespace Gapo.Core.Diagnosis;

public sealed record RenderPass(string Name, double Milliseconds);

public sealed record StageTimes(
    double SimulationMs,
    double RenderMs,
    double PresentMs,
    double StreamingMs,
    int RenderThreads,
    int EntitiesSimulated,
    double PhysicsMs,
    IReadOnlyList<RenderPass> RenderPasses)
{
    public double EntityLogicMs { get; init; }

    public double ParticlesMs { get; init; }

    public int EntitiesTotal { get; init; }

    public int RenderWidth { get; init; }

    public int RenderHeight { get; init; }

    public double PassMs(string name) => RenderPasses.FirstOrDefault(pass => pass.Name == name)?.Milliseconds ?? 0;
}

public sealed record PresentTiming(double MedianFrameMs, double MedianCpuBusyMs, double MedianInPresentMs);

public sealed record MonitorSummary
{
    public IReadOnlyList<double> LoadPerProcessor { get; init; } = [];

    public double AverageLoadPercent { get; init; }

    public double BusiestProcessorPercent { get; init; }

    public double GameProcessorsUsed { get; init; }

    public double? BusiestGameThreadPercent { get; init; }

    public double BackgroundPercent { get; init; }

    public int MemoryLoadPercent { get; init; }

    public long LowestAvailableMemoryBytes { get; init; }
}

public sealed record ProcessorLimits(int LogicalProcessors, int MaximumMegahertz, int LimitMegahertz);

public sealed record DiagnosisInput
{
    public required FrameStatistics Frames { get; init; }

    public required double TargetFps { get; init; }

    public required MonitorSummary Monitor { get; init; }

    public required ProcessorLimits Processor { get; init; }

    public StageTimes? Stages { get; init; }

    public PresentTiming? Presents { get; init; }

    public FrameStatistics? HitchObservation { get; init; }

    public double WarmupShareOfFrames { get; init; }
}
