using System.Text.Json;
using Gapo.Core.Telemetry;

namespace Gapo.Agent.Monitoring;

public sealed record MonitorReport(
    int Samples,
    double MeanGameProcessorsUsed,
    double MeanMachineLoadPercent,
    double MeanBusiestProcessorPercent,
    double MeanBackgroundPercent,
    double MedianBusiestGameThreadPercent,
    double PeakGameWriteMegabytesPerSecond,
    long GamePrivateBytes,
    int MemoryLoadPercent,
    ProcessPriority GamePriority)
{
    private const int DecimalPlaces = 3;
    private const double BytesPerMegabyte = 1024.0 * 1024.0;

    public void WriteTo(Utf8JsonWriter json)
    {
        json.WriteStartObject("monitor");
        json.WriteNumber("samples", Samples);
        json.WriteNumber("meanGameProcessorsUsed", Round(MeanGameProcessorsUsed));
        json.WriteNumber("meanMachineLoadPercent", Round(MeanMachineLoadPercent));
        json.WriteNumber("meanBusiestProcessorPercent", Round(MeanBusiestProcessorPercent));
        json.WriteNumber("meanBackgroundPercent", Round(MeanBackgroundPercent));
        json.WriteNumber("medianBusiestGameThreadPercent", Round(MedianBusiestGameThreadPercent));
        json.WriteNumber("peakGameWriteMegabytesPerSecond", Round(PeakGameWriteMegabytesPerSecond));
        json.WriteNumber("gamePrivateMegabytes", Round(GamePrivateBytes / BytesPerMegabyte));
        json.WriteNumber("memoryLoadPercent", MemoryLoadPercent);
        json.WriteString("gamePriority", GamePriority.ToString());
        json.WriteEndObject();
    }

    private static double Round(double value) => Math.Round(value, DecimalPlaces);
}
