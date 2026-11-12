using System.Globalization;
using Gapo.Sandbox.Contract;

namespace Gapo.Agent;

public sealed record AgentOptions(
    string SandboxMapName,
    TimeSpan MaximumRunTime,
    bool StopWhenGameExits,
    bool MeasureFrameTiming,
    bool MeasureBaseline,
    string? ReportPath)
{
    private const string MapNameOption = "--sandbox-map";
    private const string SecondsOption = "--seconds";
    private const string ReportOption = "--report";
    private const string StopWithGameFlag = "--stop-when-game-exits";
    private const string FrameTimingFlag = "--frame-timing";
    private const string BaselineFlag = "--measure-baseline";

    public static AgentOptions Parse(IReadOnlyList<string> arguments)
    {
        var options = new AgentOptions(TelemetryChannel.DefaultMapName, Timeout.InfiniteTimeSpan, StopWhenGameExits: false, MeasureFrameTiming: false, MeasureBaseline: false, ReportPath: null);
        foreach (var argument in arguments)
        {
            if (argument == StopWithGameFlag)
            {
                options = options with { StopWhenGameExits = true };
            }
            else if (argument == BaselineFlag)
            {
                options = options with { MeasureBaseline = true };
            }
            else if (argument == FrameTimingFlag)
            {
                options = options with { MeasureFrameTiming = true };
            }
            else if (TryReadValue(argument, MapNameOption, out var mapName))
            {
                options = options with { SandboxMapName = mapName };
            }
            else if (TryReadValue(argument, ReportOption, out var reportPath))
            {
                options = options with { ReportPath = reportPath };
            }
            else if (TryReadValue(argument, SecondsOption, out var seconds))
            {
                options = options with { MaximumRunTime = ParseSeconds(seconds) };
            }
            else
            {
                throw new ArgumentException($"Unknown argument: {argument}");
            }
        }

        return options;
    }

    private static TimeSpan ParseSeconds(string text) =>
        double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds) && seconds > 0
            ? TimeSpan.FromSeconds(seconds)
            : throw new ArgumentException($"{SecondsOption} expects a positive number of seconds.");

    private static bool TryReadValue(string argument, string name, out string value)
    {
        var prefix = name + "=";
        if (argument.StartsWith(prefix, StringComparison.Ordinal))
        {
            value = argument[prefix.Length..];
            return true;
        }

        value = string.Empty;
        return false;
    }
}
