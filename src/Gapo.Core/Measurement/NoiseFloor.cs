namespace Gapo.Core.Measurement;

public sealed record NoiseFloor(double RawSpreadFraction, double FloorFraction, bool CanBeTrusted)
{
    public const double Multiplier = 2.5;
    public const double SmallestFloor = 0.025;
    public const double LargestFloor = 0.10;
    public const double LargestSpreadThatCanBeJudged = LargestFloor / Multiplier;

    private const double SmallestMeaningfulBusyMs = 0.01;

    public static NoiseFloor FromRepeatedMeasurement(double firstMedianBusyMs, double secondMedianBusyMs)
    {
        var mean = Math.Max(SmallestMeaningfulBusyMs, (firstMedianBusyMs + secondMedianBusyMs) / 2);
        var spread = Math.Abs(firstMedianBusyMs - secondMedianBusyMs) / mean;
        return new NoiseFloor(spread, Math.Clamp(spread * Multiplier, SmallestFloor, LargestFloor), spread <= LargestSpreadThatCanBeJudged);
    }

    public bool IsBeatenBy(double relativeGain) => relativeGain > FloorFraction;
}

public enum ChangeEffect
{
    Improvement,
    WithinNoise,
    Regression,
    NeedsConfirmation,
}

public static class ChangeComparison
{
    public const double ClearlyDecidedMultiple = 3.0;

    public static double RelativeGain(double referenceMedianBusyMs, double candidateMedianBusyMs) =>
        referenceMedianBusyMs <= 0 ? 0 : (referenceMedianBusyMs - candidateMedianBusyMs) / referenceMedianBusyMs;

    public static ChangeEffect Judge(double relativeGain, NoiseFloor noise)
    {
        var magnitude = Math.Abs(relativeGain);
        if (magnitude <= noise.FloorFraction)
        {
            return ChangeEffect.WithinNoise;
        }

        if (magnitude < noise.FloorFraction * ClearlyDecidedMultiple)
        {
            return ChangeEffect.NeedsConfirmation;
        }

        return relativeGain > 0 ? ChangeEffect.Improvement : ChangeEffect.Regression;
    }

    public static ChangeEffect JudgeInterleaved(ReadOnlySpan<double> relativeGainPerPair, NoiseFloor noise)
    {
        if (relativeGainPerPair.IsEmpty)
        {
            return ChangeEffect.WithinNoise;
        }

        var sum = 0.0;
        var allPositive = true;
        var allNegative = true;
        foreach (var gain in relativeGainPerPair)
        {
            sum += gain;
            allPositive &= gain > 0;
            allNegative &= gain < 0;
        }

        var mean = sum / relativeGainPerPair.Length;
        if (Math.Abs(mean) <= noise.FloorFraction || (!allPositive && !allNegative))
        {
            return ChangeEffect.WithinNoise;
        }

        return mean > 0 ? ChangeEffect.Improvement : ChangeEffect.Regression;
    }
}
