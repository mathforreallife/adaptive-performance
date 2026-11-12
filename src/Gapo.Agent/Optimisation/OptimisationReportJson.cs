using System.Text.Json;
using Gapo.Agent.Measurement;
using Gapo.Core.Measurement;
using Gapo.Core.Optimisation;

namespace Gapo.Agent.Optimisation;

public static class OptimisationReportJson
{
    private const int DecimalPlaces = 3;
    private const double PercentScale = 100.0;

    public static void Write(Utf8JsonWriter json, OptimisationReport report)
    {
        json.WriteStartObject("optimisation");
        json.WriteString("goal", report.Goal.ToString());
        json.WriteBoolean("completed", report.Completed);
        json.WriteString("error", report.Error ?? string.Empty);
        json.WriteNumber("targetFps", Round(report.TargetFps));
        json.WriteString("targetReason", report.TargetReason);
        json.WriteNumber("fpsGainPercent", Round(report.FpsGainPercent));
        json.WriteNumber("workReductionFactor", Round(report.WorkReductionFactor));
        json.WriteNumber("noiseFloorPercent", Round((report.Noise?.FloorFraction ?? 0) * PercentScale));
        json.WriteNumber("measurementWindows", report.MeasurementWindows);
        json.WriteNumber("durationSeconds", Round(report.Duration.TotalSeconds));
        json.WriteNumber("kept", report.Kept);
        json.WriteNumber("rolledBack", report.RolledBack);
        json.WriteString("pacingNote", report.PacingNote);
        WriteStatistics(json, "baseline", report.Baseline);
        WriteStatistics(json, "final", report.Final);
        json.WriteString("bottleneckBefore", report.DiagnosisBefore?.Title ?? string.Empty);
        json.WriteString("bottleneckAfter", report.DiagnosisAfter?.Title ?? string.Empty);
        json.WriteStartArray("experiments");
        foreach (var experiment in report.Experiments)
        {
            json.WriteStartObject();
            json.WriteNumber("index", experiment.Index);
            json.WriteString("title", experiment.Title);
            json.WriteString("setting", experiment.Setting);
            json.WriteString("kind", experiment.Kind.ToString());
            json.WriteString("decision", experiment.Decision.ToString());
            json.WriteString("hypothesis", experiment.Hypothesis);
            json.WriteString("reason", experiment.Reason);
            json.WriteNumber("visualLoss", experiment.VisualLoss);
            json.WriteNumber("predictedSavingMs", Round(experiment.PredictedSavingMs));
            json.WriteNumber("measuredSavingMs", Round(experiment.MeasuredSavingMs));
            json.WriteNumber("gainPercent", Round(experiment.GainPercent));
            json.WriteNumber("frameWorkBeforeMs", Round(experiment.Before.MedianBusyMs));
            json.WriteNumber("frameWorkAfterMs", Round(experiment.After?.MedianBusyMs ?? 0));
            json.WriteString("bottleneckAfter", experiment.BottleneckAfter ?? string.Empty);
            json.WriteEndObject();
        }

        json.WriteEndArray();
        json.WriteStartArray("notTouched");
        foreach (var note in report.NotTouched)
        {
            json.WriteStringValue(note);
        }

        json.WriteEndArray();
        WriteSettings(json, "settingsBefore", report.OriginalSettings);
        WriteSettings(json, "settingsAfter", report.FinalSettings);
        json.WriteEndObject();
        if (report.DiagnosisBefore is { } before)
        {
            DiagnosisJson.Write(json, before);
        }
    }

    private static void WriteStatistics(Utf8JsonWriter json, string name, FrameStatistics statistics)
    {
        json.WriteStartObject(name);
        json.WriteNumber("frames", statistics.Count);
        json.WriteNumber("averageFps", Round(statistics.AverageFps));
        json.WriteNumber("medianBusyMs", Round(statistics.MedianBusyMs));
        json.WriteNumber("percentile95FrameMs", Round(statistics.Percentile95FrameMs));
        json.WriteNumber("frameTimeSpreadMs", Round(statistics.FrameTimeSpreadMs));
        json.WriteNumber("stuttersPerMinute", Round(statistics.StuttersPerMinute));
        json.WriteEndObject();
    }

    private static void WriteSettings(Utf8JsonWriter json, string name, GameSettings? settings)
    {
        json.WriteStartObject(name);
        foreach (var (key, value) in settings?.Values ?? new Dictionary<string, string>())
        {
            json.WriteString(key, value);
        }

        json.WriteEndObject();
    }

    private static double Round(double value) => Math.Round(value, DecimalPlaces);
}
