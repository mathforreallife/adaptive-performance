namespace Gapo.EndToEnd;

public sealed record OverheadComparison(double BaselineFps, double AttachedFps, double BaselineSpreadPercent, double NoiseFloorPercent, double SlowdownPercent)
{
    public const double LargestBaselineSpreadThatCanBeJudgedPercent = 8.0;

    private const double NoiseFloorMultiplier = 2.5;
    private const double MinimumNoiseFloorPercent = 2.5;
    private const double MaximumNoiseFloorPercent = 10.0;
    private const double PercentScale = 100.0;

    public bool AttachedRunIsWithinNoise => SlowdownPercent <= NoiseFloorPercent;

    public bool BaselinesAgree => BaselineSpreadPercent <= LargestBaselineSpreadThatCanBeJudgedPercent;

    public static OverheadComparison Between(double firstBaselineFps, double secondBaselineFps, double attachedFps)
    {
        var baseline = (firstBaselineFps + secondBaselineFps) / 2;
        if (baseline <= 0)
        {
            return new OverheadComparison(0, attachedFps, PercentScale, MaximumNoiseFloorPercent, PercentScale);
        }

        var baselineSpreadPercent = Math.Abs(firstBaselineFps - secondBaselineFps) / baseline * PercentScale;
        var noiseFloor = Math.Clamp(baselineSpreadPercent * NoiseFloorMultiplier, MinimumNoiseFloorPercent, MaximumNoiseFloorPercent);
        var slowdown = (baseline - attachedFps) / baseline * PercentScale;
        return new OverheadComparison(baseline, attachedFps, baselineSpreadPercent, noiseFloor, slowdown);
    }
}
