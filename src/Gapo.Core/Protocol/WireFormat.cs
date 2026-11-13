using System.Text;
using System.Text.Json;
using Gapo.Core.Optimisation;

namespace Gapo.Core.Protocol;

public static class WireFormat
{
    public const string PipeNamePrefix = "GAPO.Agent.";

    private const string TypeProperty = "type";
    private const string StatusType = "status";
    private const string SampleType = "sample";
    private const string ReportType = "report";
    private const string OptimiseType = "optimise";
    private const string RevertAllType = "revertAll";
    private const string StopType = "stop";
    private const int DecimalPlaces = 3;

    public static string PipeNameFor(int windowsSessionId) => PipeNamePrefix + windowsSessionId.ToString(System.Globalization.CultureInfo.InvariantCulture);

    public static string Encode(AgentMessage message) => Write(json =>
    {
        switch (message)
        {
            case StatusMessage status:
                json.WriteString(TypeProperty, StatusType);
                json.WriteString("state", status.State.ToString());
                json.WriteString("title", status.Title);
                json.WriteString("detail", status.Detail);
                break;
            case LiveSampleMessage sample:
                json.WriteString(TypeProperty, SampleType);
                json.WriteBoolean("gameConnected", sample.GameConnected);
                json.WriteString("gameName", sample.GameName);
                json.WriteNumber("averageFps", Round(sample.AverageFps));
                json.WriteNumber("medianFrameMs", Round(sample.MedianFrameMs));
                json.WriteNumber("medianBusyMs", Round(sample.MedianBusyMs));
                json.WriteNumber("stuttersPerMinute", Round(sample.StuttersPerMinute));
                json.WriteNumber("gameProcessorsUsed", Round(sample.GameProcessorsUsed));
                json.WriteNumber("machineLoadPercent", Round(sample.MachineLoadPercent));
                json.WriteNumber("ownCpuPercent", Round(sample.OwnCpuPercent));
                json.WriteNumber("ownMegabytes", Round(sample.OwnMegabytes));
                json.WriteBoolean("ownUseIsOverBudget", sample.OwnUseIsOverBudget);
                json.WriteStartArray("recentFrameMs");
                foreach (var frame in sample.RecentFrameMs)
                {
                    json.WriteNumberValue(Round(frame));
                }

                json.WriteEndArray();
                break;
            case ReportReadyMessage report:
                json.WriteString(TypeProperty, ReportType);
                json.WriteString("path", report.Path);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(message), message, "Unknown agent message.");
        }
    });

    public static string Encode(UiCommand command) => Write(json =>
    {
        switch (command)
        {
            case OptimiseCommand optimise:
                json.WriteString(TypeProperty, OptimiseType);
                json.WriteString("goal", optimise.Goal.ToString());
                break;
            case RevertAllCommand:
                json.WriteString(TypeProperty, RevertAllType);
                break;
            case StopCommand:
                json.WriteString(TypeProperty, StopType);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(command), command, "Unknown command.");
        }
    });

    public static AgentMessage? DecodeMessage(string line)
    {
        using var document = TryParse(line);
        if (document is null || !document.RootElement.TryGetProperty(TypeProperty, out var type))
        {
            return null;
        }

        var root = document.RootElement;
        return type.GetString() switch
        {
            StatusType => new StatusMessage(
                Enum.TryParse<AgentState>(Text(root, "state"), out var state) ? state : AgentState.NoGame,
                Text(root, "title"),
                Text(root, "detail")),
            SampleType => new LiveSampleMessage(
                root.GetProperty("gameConnected").GetBoolean(),
                Text(root, "gameName"),
                Number(root, "averageFps"),
                Number(root, "medianFrameMs"),
                Number(root, "medianBusyMs"),
                Number(root, "stuttersPerMinute"),
                Number(root, "gameProcessorsUsed"),
                Number(root, "machineLoadPercent"),
                Number(root, "ownCpuPercent"),
                Number(root, "ownMegabytes"),
                root.GetProperty("ownUseIsOverBudget").GetBoolean(),
                root.GetProperty("recentFrameMs").EnumerateArray().Select(frame => frame.GetDouble()).ToArray()),
            ReportType => new ReportReadyMessage(Text(root, "path")),
            _ => null,
        };
    }

    public static UiCommand? DecodeCommand(string line)
    {
        using var document = TryParse(line);
        if (document is null || !document.RootElement.TryGetProperty(TypeProperty, out var type))
        {
            return null;
        }

        return type.GetString() switch
        {
            OptimiseType => new OptimiseCommand(Enum.TryParse<Goal>(Text(document.RootElement, "goal"), out var goal) ? goal : Goal.Balanced),
            RevertAllType => new RevertAllCommand(),
            StopType => new StopCommand(),
            _ => null,
        };
    }

    private static string Write(Action<Utf8JsonWriter> writeBody)
    {
        using var stream = new MemoryStream();
        using (var json = new Utf8JsonWriter(stream))
        {
            json.WriteStartObject();
            writeBody(json);
            json.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.GetBuffer(), 0, (int)stream.Length);
    }

    private static JsonDocument? TryParse(string line)
    {
        try
        {
            return JsonDocument.Parse(line);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string Text(JsonElement root, string property) =>
        root.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? string.Empty : string.Empty;

    private static double Number(JsonElement root, string property) =>
        root.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.Number ? value.GetDouble() : 0;

    private static double Round(double value) => Math.Round(value, DecimalPlaces);
}
