using System.Globalization;
using Gapo.Sandbox.Contract;

namespace Gapo.Agent;

public sealed record AgentOptions(
    string SandboxMapName,
    TimeSpan MaximumRunTime,
    bool StopWhenGameExits,
    bool MeasureFrameTiming,
    bool MeasureBaseline,
    Gapo.Core.Optimisation.Goal? OptimisationGoal,
    bool AskGameToCloseWhenDone,
    bool RevertAll,
    string JournalPath,
    string? ReportPath)
{
    private const string MapNameOption = "--sandbox-map";
    private const string SecondsOption = "--seconds";
    private const string ReportOption = "--report";
    private const string StopWithGameFlag = "--stop-when-game-exits";
    private const string FrameTimingFlag = "--frame-timing";
    private const string BaselineFlag = "--measure-baseline";
    private const string OptimiseOption = "--optimise";
    private const string JournalOption = "--journal";
    private const string CloseGameFlag = "--ask-game-to-close-when-done";
    private const string RevertAllFlag = "--revert-all";

    public static AgentOptions Parse(IReadOnlyList<string> arguments)
    {
        var options = new AgentOptions(TelemetryChannel.DefaultMapName, Timeout.InfiniteTimeSpan, StopWhenGameExits: false, MeasureFrameTiming: false, MeasureBaseline: false, OptimisationGoal: null, AskGameToCloseWhenDone: false, RevertAll: false, JournalPath: DefaultJournalPath(), ReportPath: null);
        foreach (var argument in arguments)
        {
            if (argument == StopWithGameFlag)
            {
                options = options with { StopWhenGameExits = true };
            }
            else if (argument == CloseGameFlag)
            {
                options = options with { AskGameToCloseWhenDone = true };
            }
            else if (argument == RevertAllFlag)
            {
                options = options with { RevertAll = true };
            }
            else if (TryReadValue(argument, JournalOption, out var journalPath))
            {
                options = options with { JournalPath = journalPath };
            }
            else if (TryReadValue(argument, OptimiseOption, out var goal))
            {
                options = options with { OptimisationGoal = ParseGoal(goal) };
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

    private static string DefaultJournalPath() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "GAPO", "change-journal.json");

    private static Gapo.Core.Optimisation.Goal ParseGoal(string text) => text.ToLowerInvariant() switch
    {
        "balanced" => Gapo.Core.Optimisation.Goal.Balanced,
        "maximum-fps" => Gapo.Core.Optimisation.Goal.MaximumFps,
        "quality" => Gapo.Core.Optimisation.Goal.Quality,
        "smoothness" => Gapo.Core.Optimisation.Goal.Smoothness,
        _ => throw new ArgumentException($"{OptimiseOption} expects balanced, maximum-fps, quality or smoothness."),
    };

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
