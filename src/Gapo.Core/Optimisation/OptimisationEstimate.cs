using System.Globalization;
using Gapo.Core.Measurement;

namespace Gapo.Core.Optimisation;

public sealed record TimeRange(TimeSpan Low, TimeSpan High)
{
    private const double SecondsPerMinute = 60;
    private const double ShowSecondsBelow = 100;

    public string Describe()
    {
        var low = Low.TotalSeconds;
        var high = Math.Max(low, High.TotalSeconds);
        if (high < ShowSecondsBelow)
        {
            var lowSeconds = RoundSeconds(low);
            var highSeconds = RoundSeconds(high);
            return lowSeconds == highSeconds
                ? string.Create(CultureInfo.InvariantCulture, $"about {lowSeconds} seconds")
                : string.Create(CultureInfo.InvariantCulture, $"about {lowSeconds}–{highSeconds} seconds");
        }

        var highMinutes = Math.Max(2, (int)Math.Ceiling(high / SecondsPerMinute));
        if (low < SecondsPerMinute)
        {
            return string.Create(CultureInfo.InvariantCulture, $"about {RoundSeconds(low)} seconds to {highMinutes} minutes");
        }

        var lowMinutes = (int)Math.Floor(low / SecondsPerMinute);
        highMinutes = Math.Max(lowMinutes + 1, highMinutes);
        return string.Create(CultureInfo.InvariantCulture, $"about {lowMinutes}–{highMinutes} minutes");
    }

    private static int RoundSeconds(double seconds) => Math.Max(5, (int)(Math.Round(seconds / 5) * 5));
}

public static class OptimisationEstimate
{
    public const double ApplyAndAcknowledgeSeconds = 1.2;
    public const int FewestTestsBeforeStopping = 3;
    public const double RestartLowSeconds = 20;
    public const double RestartHighSeconds = 60;
    public const double ObservationSeconds = 20;

    private const int BaselineWindows = 2;
    private const int BaselineRetryWindows = 2;
    private const int ValidationWindows = 1;
    private const int GiveBackWindows = 1;
    private const double SettledWarmupLowSeconds = 1;
    private const double SettledWarmupHighSeconds = 10;
    private const double UnsettledWarmupLowSeconds = 5;
    private const double UnsettledWarmupHighSeconds = 45;
    private const double MillisecondsPerSecond = 1000;
    private const double EstimateSpread = 0.25;

    public static double WindowSeconds(double fps, MeasurementWindowOptions? options = null)
    {
        var window = options ?? new MeasurementWindowOptions();
        var giveUp = window.GiveUpAfterMs / MillisecondsPerSecond;
        var framesTake = fps > 0 ? window.MinimumFrames / fps : giveUp;
        return Math.Max(window.MinimumSpanMs / MillisecondsPerSecond, Math.Min(framesTake, giveUp)) + (window.SettlingDiscardMs / MillisecondsPerSecond) + ApplyAndAcknowledgeSeconds;
    }

    public static TimeRange ForLiveChanges(double fps, int candidates, bool gameHasSettled, bool frameCapAllowed)
    {
        var window = WindowSeconds(fps);
        var capWindows = frameCapAllowed ? 1 : 0;
        var lowWindows = BaselineWindows + Math.Min(FewestTestsBeforeStopping, candidates) + ValidationWindows;
        var highWindows = BaselineWindows + BaselineRetryWindows + candidates + capWindows + GiveBackWindows + ValidationWindows;
        var warmupLow = gameHasSettled ? SettledWarmupLowSeconds : UnsettledWarmupLowSeconds;
        var warmupHigh = gameHasSettled ? SettledWarmupHighSeconds : UnsettledWarmupHighSeconds;
        return new TimeRange(TimeSpan.FromSeconds(warmupLow + (lowWindows * window)), TimeSpan.FromSeconds(warmupHigh + (highWindows * window)));
    }

    public static TimeRange ForRestarts(int candidates, int mostRestarts)
    {
        var tests = Math.Min(candidates, mostRestarts);
        var testsLow = Math.Min(FewestTestsBeforeStopping, tests);
        var baselineLow = UnsettledWarmupLowSeconds + ObservationSeconds;
        var baselineHigh = UnsettledWarmupHighSeconds + ObservationSeconds;
        var perTestLow = RestartLowSeconds + UnsettledWarmupLowSeconds + ObservationSeconds;
        var perTestHigh = RestartHighSeconds + UnsettledWarmupHighSeconds + ObservationSeconds;
        return new TimeRange(TimeSpan.FromSeconds(baselineLow + (testsLow * perTestLow)), TimeSpan.FromSeconds(baselineHigh + (tests * perTestHigh)));
    }

    public static TimeRange Remaining(int stepsDone, int stepsExpected, TimeSpan elapsed)
    {
        if (stepsDone <= 0 || stepsExpected <= stepsDone)
        {
            return new TimeRange(TimeSpan.Zero, TimeSpan.Zero);
        }

        var perStep = elapsed.TotalSeconds / stepsDone;
        var remaining = perStep * (stepsExpected - stepsDone);
        return new TimeRange(TimeSpan.FromSeconds(remaining * (1 - EstimateSpread)), TimeSpan.FromSeconds(remaining * (1 + EstimateSpread)));
    }
}
