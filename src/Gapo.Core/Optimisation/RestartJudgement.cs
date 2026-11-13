using Gapo.Core.Measurement;

namespace Gapo.Core.Optimisation;

public sealed record RestartVerdict(bool Keep, double GainPercent, double NoiseFloorPercent, string Reason);

public static class RestartJudgement
{
    public const double SmallestUncontrolledNoisePercent = 5.0;
    public const double TargetMetShareOfBudget = 0.92;

    private const double PercentScale = 100.0;
    private const double MillisecondsPerSecond = 1000.0;

    public static double NoiseFloorPercent(FrameStatistics firstHalf, FrameStatistics secondHalf)
    {
        ArgumentNullException.ThrowIfNull(firstHalf);
        ArgumentNullException.ThrowIfNull(secondHalf);
        var measured = NoiseFloor.FromRepeatedMeasurement(firstHalf.MedianBusyMs, secondHalf.MedianBusyMs).FloorFraction * PercentScale;
        return Math.Max(SmallestUncontrolledNoisePercent, measured);
    }

    public static RestartVerdict Judge(FrameStatistics reference, FrameStatistics trial, double noiseFloorPercent)
    {
        ArgumentNullException.ThrowIfNull(reference);
        ArgumentNullException.ThrowIfNull(trial);
        if (reference.MedianBusyMs <= 0 || trial.MedianBusyMs <= 0)
        {
            return new RestartVerdict(false, 0, noiseFloorPercent, "Rolled back: the session after the restart could not be measured.");
        }

        var gain = (reference.MedianBusyMs - trial.MedianBusyMs) / reference.MedianBusyMs * PercentScale;
        var keep = gain > noiseFloorPercent;
        var reason = keep
            ? FormattableString.Invariant($"Kept: frame work {reference.MedianBusyMs:0.0} → {trial.MedianBusyMs:0.0} ms ({gain:+0;-0;0}%, above the ±{noiseFloorPercent:0.0}% noise floor for scenes GAPO does not control).")
            : FormattableString.Invariant($"Rolled back: frame work {reference.MedianBusyMs:0.0} → {trial.MedianBusyMs:0.0} ms ({gain:+0;-0;0}%) is within the ±{noiseFloorPercent:0.0}% noise floor for scenes GAPO does not control, so the visual cost is not worth it.");
        return new RestartVerdict(keep, gain, noiseFloorPercent, reason);
    }

    public static bool TargetMet(FrameStatistics statistics, double targetFps)
    {
        ArgumentNullException.ThrowIfNull(statistics);
        return targetFps > 0 && statistics.MedianBusyMs > 0 && statistics.MedianBusyMs <= MillisecondsPerSecond / targetFps * TargetMetShareOfBudget;
    }
}
