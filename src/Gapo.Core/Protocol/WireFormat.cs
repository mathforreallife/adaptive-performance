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
    private const string DiagnosisType = "diagnosis";
    private const string SettingsType = "settings";
    private const string LockType = "lock";
    private const string SetType = "set";
    private const string RevertSettingType = "revertSetting";
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
                json.WriteBoolean("canOptimise", sample.CanOptimise);
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
            case GameSettingsMessage settings:
                json.WriteString(TypeProperty, SettingsType);
                json.WriteString("gameName", settings.GameName);
                json.WriteBoolean("canChange", settings.CanChange);
                json.WriteBoolean("appliesAtRestart", settings.AppliesAtRestart);
                json.WriteString("note", settings.Note);
                json.WriteStartArray("settings");
                foreach (var setting in settings.Settings)
                {
                    json.WriteStartObject();
                    json.WriteString("key", setting.Key);
                    json.WriteString("label", setting.Label);
                    json.WriteString("value", setting.Value);
                    json.WriteString("original", setting.OriginalValue);
                    json.WriteBoolean("locked", setting.Locked);
                    json.WriteStartArray("choices");
                    foreach (var choice in setting.Choices)
                    {
                        json.WriteStartObject();
                        json.WriteString("value", choice.Value);
                        json.WriteString("label", choice.Label);
                        json.WriteEndObject();
                    }

                    json.WriteEndArray();
                    json.WriteEndObject();
                }

                json.WriteEndArray();
                break;
            case DiagnosisMessage diagnosis:
                json.WriteString(TypeProperty, DiagnosisType);
                json.WriteString("gameName", diagnosis.GameName);
                json.WriteString("bottleneck", diagnosis.Bottleneck);
                json.WriteString("title", diagnosis.Title);
                json.WriteString("summary", diagnosis.Summary);
                json.WriteStartArray("alsoNoticed");
                foreach (var line in diagnosis.AlsoNoticed)
                {
                    json.WriteStringValue(line);
                }

                json.WriteEndArray();
                json.WriteString("basis", diagnosis.Basis);
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
            case LockSettingCommand lockSetting:
                json.WriteString(TypeProperty, LockType);
                json.WriteString("key", lockSetting.Key);
                json.WriteBoolean("locked", lockSetting.Locked);
                break;
            case SetSettingCommand set:
                json.WriteString(TypeProperty, SetType);
                json.WriteString("key", set.Key);
                json.WriteString("value", set.Value);
                break;
            case RevertSettingCommand revert:
                json.WriteString(TypeProperty, RevertSettingType);
                json.WriteString("key", revert.Key);
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
                root.GetProperty("recentFrameMs").EnumerateArray().Select(frame => frame.GetDouble()).ToArray(),
                !root.TryGetProperty("canOptimise", out var canOptimise) || canOptimise.ValueKind != JsonValueKind.False),
            ReportType => new ReportReadyMessage(Text(root, "path")),
            SettingsType => new GameSettingsMessage(
                Text(root, "gameName"),
                Flag(root, "canChange"),
                Flag(root, "appliesAtRestart"),
                Text(root, "note"),
                root.TryGetProperty("settings", out var list) && list.ValueKind == JsonValueKind.Array
                    ? list.EnumerateArray().Select(ReadSetting).ToArray()
                    : []),
            DiagnosisType => new DiagnosisMessage(
                Text(root, "gameName"),
                Text(root, "bottleneck"),
                Text(root, "title"),
                Text(root, "summary"),
                root.TryGetProperty("alsoNoticed", out var also) && also.ValueKind == JsonValueKind.Array
                    ? also.EnumerateArray().Select(line => line.GetString() ?? string.Empty).ToArray()
                    : [],
                Text(root, "basis")),
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

        var root = document.RootElement;
        return type.GetString() switch
        {
            OptimiseType => new OptimiseCommand(Enum.TryParse<Goal>(Text(root, "goal"), out var goal) ? goal : Goal.Balanced),
            RevertAllType => new RevertAllCommand(),
            StopType => new StopCommand(),
            LockType when Text(root, "key").Length > 0 => new LockSettingCommand(Text(root, "key"), Flag(root, "locked")),
            SetType when Text(root, "key").Length > 0 => new SetSettingCommand(Text(root, "key"), Text(root, "value")),
            RevertSettingType when Text(root, "key").Length > 0 => new RevertSettingCommand(Text(root, "key")),
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

    private static bool Flag(JsonElement root, string property) =>
        root.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.True;

    private static SettingState ReadSetting(JsonElement setting) =>
        new(
            Text(setting, "key"),
            Text(setting, "label"),
            Text(setting, "value"),
            Text(setting, "original"),
            Flag(setting, "locked"),
            setting.TryGetProperty("choices", out var choices) && choices.ValueKind == JsonValueKind.Array
                ? choices.EnumerateArray().Select(choice => new SettingChoice(Text(choice, "value"), Text(choice, "label"))).ToArray()
                : []);

    private static double Number(JsonElement root, string property) =>
        root.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.Number ? value.GetDouble() : 0;

    private static double Round(double value) => Math.Round(value, DecimalPlaces);
}
