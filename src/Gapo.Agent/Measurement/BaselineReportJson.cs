using System.Text.Json;
using Gapo.Core.Measurement;

namespace Gapo.Agent.Measurement;

public static class BaselineReportJson
{
    private const int DecimalPlaces = 3;
    private const double PercentScale = 100.0;

    public static void Write(Utf8JsonWriter json, BaselineMeasurement measurement)
    {
        json.WriteStartObject("baseline");
        json.WriteBoolean("finished", measurement.IsFinished);
        json.WriteNumber("waitedForWarmupSeconds", Round(measurement.WaitedForWarmup.TotalSeconds));
        json.WriteBoolean("warmupWaitTimedOut", measurement.WarmupWaitTimedOut);
        json.WriteNumber("attempts", measurement.Attempts);
        if (measurement.Result is { } result)
        {
            json.WriteBoolean("succeeded", result.Succeeded);
            json.WriteString("failureReason", result.FailureReason ?? string.Empty);
            json.WriteBoolean("anyWindowCutShort", result.AnyWindowCutShort);
            json.WriteNumber("rawSpreadPercent", Round(result.Noise.RawSpreadFraction * PercentScale));
            json.WriteNumber("noiseFloorPercent", Round(result.Noise.FloorFraction * PercentScale));
            json.WriteBoolean("noiseFloorCanBeTrusted", result.Noise.CanBeTrusted);
            WriteWindow(json, "firstWindow", result.FirstWindow);
            WriteWindow(json, "secondWindow", result.SecondWindow);
        }
        else
        {
            json.WriteBoolean("succeeded", false);
            json.WriteString("failureReason", "The measurement did not finish.");
        }

        json.WriteEndObject();
    }

    private static void WriteWindow(Utf8JsonWriter json, string name, FrameStatistics window)
    {
        json.WriteStartObject(name);
        json.WriteNumber("frames", window.Count);
        json.WriteNumber("durationMs", Round(window.DurationMs));
        json.WriteNumber("averageFps", Round(window.AverageFps));
        json.WriteNumber("medianBusyMs", Round(window.MedianBusyMs));
        json.WriteNumber("medianFrameMs", Round(window.MedianFrameMs));
        json.WriteNumber("percentile95FrameMs", Round(window.Percentile95FrameMs));
        json.WriteNumber("stutters", window.Stutters);
        json.WriteEndObject();
    }

    private static double Round(double value) => Math.Round(value, DecimalPlaces);
}
