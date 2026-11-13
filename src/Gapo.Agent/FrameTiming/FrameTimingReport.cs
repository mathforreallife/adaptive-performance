using System.Text.Json;
using Gapo.Core.Statistics;
using Gapo.Windows.FrameTiming;

namespace Gapo.Agent.FrameTiming;

public sealed record FrameTimingReport(
    FrameTimingAvailability Availability,
    long EventsSeen,
    uint EventsLost,
    int PresentedFrames,
    double MeanFrameMs,
    double MedianFrameMs,
    double MedianCpuBusyMs,
    double MedianInPresentMs,
    int HookFrames,
    double HookMeanFrameMs,
    double HookMedianFrameMs,
    double HookMedianWorkMs,
    double HookMedianPresentMs,
    bool OnlyTheGamesEventsWereDelivered = false)
{
    private const int DecimalPlaces = 3;

    public static FrameTimingReport From(FrameTimingProbe probe, SeriesSummary hookFrameMs, SeriesSummary hookWorkMs, SeriesSummary hookPresentMs) =>
        new(
            probe.Availability,
            probe.EventsSeen,
            probe.EventsLost,
            probe.FrameMs.Count,
            probe.FrameMs.Mean,
            probe.FrameMs.Median(),
            probe.CpuBusyMs.Median(),
            probe.InPresentMs.Median(),
            hookFrameMs.Count,
            hookFrameMs.Mean,
            hookFrameMs.Median(),
            hookWorkMs.Median(),
            hookPresentMs.Median(),
            probe.OnlyTheGamesEventsWereDelivered);

    public void WriteTo(Utf8JsonWriter json)
    {
        json.WriteStartObject("frameTiming");
        json.WriteString("availability", Availability.ToString());
        json.WriteNumber("eventsSeen", EventsSeen);
        json.WriteNumber("eventsLost", EventsLost);
        json.WriteBoolean("onlyTheGamesEventsWereDelivered", OnlyTheGamesEventsWereDelivered);
        json.WriteNumber("presentedFrames", PresentedFrames);
        json.WriteNumber("meanFrameMs", Round(MeanFrameMs));
        json.WriteNumber("medianFrameMs", Round(MedianFrameMs));
        json.WriteNumber("medianCpuBusyMs", Round(MedianCpuBusyMs));
        json.WriteNumber("medianInPresentMs", Round(MedianInPresentMs));
        json.WriteNumber("hookFrames", HookFrames);
        json.WriteNumber("hookMeanFrameMs", Round(HookMeanFrameMs));
        json.WriteNumber("hookMedianFrameMs", Round(HookMedianFrameMs));
        json.WriteNumber("hookMedianWorkMs", Round(HookMedianWorkMs));
        json.WriteNumber("hookMedianPresentMs", Round(HookMedianPresentMs));
        json.WriteEndObject();
    }

    private static double Round(double value) => Math.Round(value, DecimalPlaces);
}
