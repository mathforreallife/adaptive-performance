namespace Gapo.Core.Diagnosis;

public enum Bottleneck
{
    Unknown,
    Transient,
    CpuMainThread,
    RenderSingleThread,
    RenderWorkload,
    CpuOneThreadLimit,
    CpuAllProcessors,
    WaitingInPresent,
    TargetMet,
}

public enum ProblemCategory
{
    ExternalFix,
    GameSetting,
    EngineLimitation,
    Transient,
    Unclassified,
}

public sealed record StageShare(string Name, double Milliseconds, double ShareOfFrameWork);

public sealed record BottleneckDiagnosis(
    Bottleneck Primary,
    string Title,
    string Summary,
    ProblemCategory Category,
    string? EngineLimitation,
    IReadOnlyList<StageShare> Stages,
    IReadOnlyList<string> Evidence,
    IReadOnlyList<string> SecondaryProblems,
    double TargetFps,
    double BudgetMs,
    double FrameWorkMs)
{
    public double RequiredSavingMs => Math.Max(0, FrameWorkMs - BudgetMs);

    public bool TargetIsMet => Primary == Bottleneck.TargetMet;
}
