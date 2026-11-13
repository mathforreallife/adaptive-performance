using System.Diagnostics;
using System.Globalization;
using Gapo.Core.Protocol;
using Microsoft.UI.Xaml;

namespace Gapo.App;

public sealed record AppOptions(string? ReportPath, string? ScreenshotDirectory, ElementTheme Theme, string PipeName, string? AgentPath, double ScreenshotAfterSeconds = 0, string? NotificationTestPath = null, string? PreferencesPath = null)
{
    private const string PreferencesOption = "--preferences=";
    private const string NotificationTestOption = "--notification-test=";
    private const string ScreenshotAfterOption = "--screenshot-after=";
    private const string ReportOption = "--report=";
    private const string ScreenshotOption = "--screenshots=";
    private const string ThemeOption = "--theme=";
    private const string PipeOption = "--pipe-name=";
    private const string AgentOption = "--agent=";
    private const string AgentFileName = "Gapo.Agent.exe";

    public static AppOptions Parse(IEnumerable<string> arguments)
    {
        var options = new AppOptions(null, null, ElementTheme.Default, DefaultPipeName(), FindAgent());
        foreach (var argument in arguments)
        {
            if (argument.StartsWith(ReportOption, StringComparison.Ordinal))
            {
                options = options with { ReportPath = argument[ReportOption.Length..] };
            }
            else if (argument.StartsWith(ScreenshotOption, StringComparison.Ordinal))
            {
                options = options with { ScreenshotDirectory = argument[ScreenshotOption.Length..] };
            }
            else if (argument.StartsWith(ThemeOption, StringComparison.Ordinal))
            {
                options = options with { Theme = ParseTheme(argument[ThemeOption.Length..]) };
            }
            else if (argument.StartsWith(PipeOption, StringComparison.Ordinal))
            {
                options = options with { PipeName = argument[PipeOption.Length..] };
            }
            else if (argument.StartsWith(AgentOption, StringComparison.Ordinal))
            {
                options = options with { AgentPath = argument[AgentOption.Length..] };
            }
            else if (argument.StartsWith(PreferencesOption, StringComparison.Ordinal))
            {
                options = options with { PreferencesPath = argument[PreferencesOption.Length..] };
            }
            else if (argument.StartsWith(NotificationTestOption, StringComparison.Ordinal))
            {
                options = options with { NotificationTestPath = argument[NotificationTestOption.Length..] };
            }
            else if (argument.StartsWith(ScreenshotAfterOption, StringComparison.Ordinal) && double.TryParse(argument[ScreenshotAfterOption.Length..], NumberStyles.Float, CultureInfo.InvariantCulture, out var after) && after >= 0)
            {
                options = options with { ScreenshotAfterSeconds = after };
            }
        }

        return options;
    }

    private static string DefaultPipeName()
    {
        using var self = Process.GetCurrentProcess();
        return WireFormat.PipeNameFor(self.SessionId);
    }

    private static string? FindAgent()
    {
        string[] candidates =
        [
            Path.Combine(AppContext.BaseDirectory, AgentFileName),
            Path.Combine(AppContext.BaseDirectory, "..", "..", "Gapo.Agent", "release", AgentFileName),
        ];
        return candidates.Select(Path.GetFullPath).FirstOrDefault(File.Exists);
    }

    private static ElementTheme ParseTheme(string text) => text.ToLowerInvariant() switch
    {
        "light" => ElementTheme.Light,
        "dark" => ElementTheme.Dark,
        _ => ElementTheme.Default,
    };
}
