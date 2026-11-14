using System.Text.Json;
using Gapo.Agent.FrameTiming;
using Gapo.Agent.Hardware;
using Gapo.Agent.Measurement;
using Gapo.Agent.Monitoring;
using Gapo.Agent.Optimisation;
using Gapo.Core.Diagnosis;
using Gapo.Core.Hardware;
using Gapo.Core.Optimisation;
using Gapo.Core.Overhead;

namespace Gapo.Agent;

public sealed record AgentReport(
    bool ConnectedToGame,
    long FramesRead,
    bool EfficiencyModeEnabled,
    bool PriorityLowered,
    bool CpuMeasuredFromCycleCounts,
    OverheadBudget Budget,
    OverheadSummary Overhead,
    OverheadVerdict Verdict,
    FrameTimingReport? FrameTiming,
    MonitorReport Monitor,
    HardwareInventory Hardware,
    BaselineMeasurement? Baseline,
    BottleneckDiagnosis? Diagnosis,
    OptimisationReport? Optimisation,
    bool RestoredInterruptedTrial)
{
    private const int DecimalPlaces = 3;

    public void WriteTo(string path)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        using var stream = File.Create(path);
        using var json = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true });
        json.WriteStartObject();
        json.WriteBoolean("connectedToGame", ConnectedToGame);
        json.WriteNumber("framesRead", FramesRead);
        json.WriteBoolean("efficiencyModeEnabled", EfficiencyModeEnabled);
        json.WriteBoolean("priorityLowered", PriorityLowered);
        json.WriteBoolean("cpuMeasuredFromCycleCounts", CpuMeasuredFromCycleCounts);
        json.WriteNumber("observedSeconds", Round(Overhead.Observed.TotalSeconds));
        json.WriteNumber("runAverageCpuPercent", Round(Overhead.RunAverageCpuPercent));
        json.WriteNumber("measurementWindowCpuPercent", Round(Overhead.MeasurementWindowCpuPercent));
        json.WriteNumber("peakCpuPercent", Round(Overhead.PeakCpuPercent));
        json.WriteNumber("privateMegabytes", Round(Overhead.PrivateMegabytes));
        json.WriteNumber("peakPrivateMegabytes", Round(Overhead.PeakPrivateMegabytes));
        json.WriteNumber("budgetCpuPercent", Budget.MaximumCpuPercent);
        json.WriteNumber("budgetPrivateMegabytes", Budget.MaximumPrivateMegabytes);
        json.WriteBoolean("withinBudget", Verdict.WithinBudget);
        json.WriteStartArray("violations");
        foreach (var violation in Verdict.Violations)
        {
            json.WriteStringValue(violation);
        }

        json.WriteEndArray();
        FrameTiming?.WriteTo(json);
        Monitor.WriteTo(json);
        HardwareInventoryJson.Write(json, Hardware);
        TipsJson.Write(json, TipsJson.For(Hardware, Monitor, Baseline));
        if (Baseline is not null)
        {
            BaselineReportJson.Write(json, Baseline);
        }

        if (Diagnosis is not null)
        {
            DiagnosisJson.Write(json, Diagnosis);
        }

        json.WriteBoolean("restoredInterruptedTrial", RestoredInterruptedTrial);
        if (Optimisation is not null)
        {
            OptimisationReportJson.Write(json, Optimisation);
        }
        json.WriteEndObject();
    }

    private static double Round(double value) => Math.Round(value, DecimalPlaces);
}
