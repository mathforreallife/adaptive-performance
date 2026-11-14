using System.Text.Json;
using Gapo.Agent.Measurement;
using Gapo.Agent.Monitoring;
using Gapo.Core.Hardware;
using Gapo.Core.Tips;

namespace Gapo.Agent;

public static class TipsJson
{
    public static IReadOnlyList<Tip> For(HardwareInventory hardware, MonitorReport monitor, BaselineMeasurement? baseline)
    {
        var measured = monitor.Samples > 0;
        var frames = baseline?.Result is { Succeeded: true } result ? result.Combined : null;
        return TipAdvisor.Advise(new TipEvidence
        {
            Hardware = hardware,
            BackgroundPercent = measured ? monitor.MeanBackgroundPercent : null,
            MemoryLoadPercent = measured ? monitor.MemoryLoadPercent : null,
            StorageHitches = frames?.StorageHitches ?? 0,
            LongestStorageHitchMs = frames?.LongestStorageHitchMs ?? 0,
        });
    }

    public static void Write(Utf8JsonWriter json, IReadOnlyList<Tip> tips)
    {
        json.WriteStartArray("tips");
        foreach (var tip in tips)
        {
            json.WriteStartObject();
            json.WriteString("title", tip.Title);
            json.WriteString("reason", tip.Reason);
            json.WriteString("kind", tip.Kind.ToString());
            if (tip.SettingsPage is not null)
            {
                json.WriteString("settingsPage", tip.SettingsPage);
                json.WriteString("buttonText", tip.ButtonText ?? "Open settings");
            }

            json.WriteEndObject();
        }

        json.WriteEndArray();
    }
}
