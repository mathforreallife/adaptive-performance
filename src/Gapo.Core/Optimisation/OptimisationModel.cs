using Gapo.Core.Diagnosis;
using Gapo.Core.Hardware;
using Gapo.Core.Measurement;

namespace Gapo.Core.Optimisation;

public enum ChangeKind
{
    Throughput,
    MemoryRelief,
    HitchFix,
    Pacing,
    QualityRestore,
}

public sealed record SettingCandidate(
    string Setting,
    string From,
    string To,
    ChangeKind Kind,
    double VisualLoss,
    double PredictedSavingMs,
    string Hypothesis,
    IReadOnlyDictionary<string, string> Changes)
{
    private const double ValueOffset = 0.6;

    public string Key => Setting + "→" + To;

    public string Title => $"{Setting}: {From} → {To}";

    public double Value => PredictedSavingMs / (VisualLoss + ValueOffset);
}

public sealed record WindowResult(FrameStatistics Statistics, StageTimes? Stages, IReadOnlyList<MeasuredFrame> Frames)
{
    public static WindowResult Combine(WindowResult first, WindowResult second)
    {
        var frames = first.Frames.Concat(second.Frames).ToArray();
        return new WindowResult(FrameStatistics.From(frames), second.Stages ?? first.Stages, frames);
    }
}

public enum Goal
{
    Balanced,
    MaximumFps,
    Quality,
    Smoothness,
}

public sealed record GoalWeights(double Performance, double Pacing, double PerVisualLossPoint)
{
    public static GoalWeights For(Goal goal) => goal switch
    {
        Goal.Balanced => new GoalWeights(1.0, 0.5, 0.022),
        Goal.MaximumFps => new GoalWeights(1.0, 0.3, 0.004),
        Goal.Quality => new GoalWeights(1.0, 0.5, 0.05),
        Goal.Smoothness => new GoalWeights(0.6, 1.2, 0.02),
        _ => throw new ArgumentOutOfRangeException(nameof(goal), goal, "Unknown goal."),
    };
}

public enum Decision
{
    Kept,
    RolledBack,
    Failed,
}

public sealed record ExperimentRecord(
    int Index,
    string Title,
    string Setting,
    ChangeKind Kind,
    string Hypothesis,
    double VisualLoss,
    double PredictedSavingMs,
    FrameStatistics Before,
    FrameStatistics? After,
    double GainPercent,
    Decision Decision,
    string Reason,
    string? BottleneckAfter)
{
    public double MeasuredSavingMs => After is null ? 0 : Before.MedianBusyMs - After.MedianBusyMs;
}

public sealed record OptimisationReport
{
    public required Goal Goal { get; init; }

    public double TargetFps { get; init; }

    public string TargetReason { get; init; } = string.Empty;

    public FrameStatistics Baseline { get; init; } = FrameStatistics.Empty;

    public FrameStatistics Final { get; init; } = FrameStatistics.Empty;

    public NoiseFloor? Noise { get; init; }

    public IReadOnlyList<ExperimentRecord> Experiments { get; init; } = [];

    public IReadOnlyList<string> NotTouched { get; init; } = [];

    public string PacingNote { get; init; } = string.Empty;

    public BottleneckDiagnosis? DiagnosisBefore { get; init; }

    public BottleneckDiagnosis? DiagnosisAfter { get; init; }

    public GameSettings? OriginalSettings { get; init; }

    public GameSettings? FinalSettings { get; init; }

    public bool Completed { get; init; }

    public string? Error { get; init; }

    public int MeasurementWindows { get; init; }

    public TimeSpan Duration { get; init; }

    public double FpsGainPercent => Baseline.AverageFps <= 0 ? 0 : (Final.AverageFps - Baseline.AverageFps) / Baseline.AverageFps * 100.0;

    public double WorkReductionFactor => Final.MedianBusyMs <= 0 ? 0 : Baseline.MedianBusyMs / Final.MedianBusyMs;

    public int Kept => Experiments.Count(experiment => experiment.Decision == Decision.Kept);

    public int RolledBack => Experiments.Count(experiment => experiment.Decision == Decision.RolledBack);
}

public sealed class GameSessionException : Exception
{
    public GameSessionException(string message, bool gameIsGone)
        : base(message)
    {
        GameIsGone = gameIsGone;
    }

    public bool GameIsGone { get; }
}

public interface IGameSession
{
    GameSettings ReadSettings();

    GameSettings Apply(GameSettings settings, string note);

    void WaitUntilWarm();

    void EnterBenchmark();

    void LeaveBenchmark();

    WindowResult Measure(int settingsRevision);

    FrameStatistics ObserveHitches(TimeSpan trailingSpan);

    MonitorSummary Monitor();
}

public interface IGameKnowledge
{
    IReadOnlyList<SettingCandidate> Candidates(GameSettings current, WindowResult reference, CpuFacts processor, bool memoryIsUnderPressure);

    SettingCandidate? HitchFix(GameSettings current, FrameStatistics hitches);

    SettingCandidate FrameCap(GameSettings current, int capFps, string why);

    IReadOnlyList<string> NotWorthTouching(GameSettings current, WindowResult reference, bool memoryIsUnderPressure);
}

public interface IChangeJournal
{
    void Begin(GameSettings original);

    void TrialStarted(GameSettings lastCommitted, SettingCandidate candidate);

    void TrialEnded(GameSettings committed);

    void Finished();
}
