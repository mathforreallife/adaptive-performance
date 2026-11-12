using Microsoft.UI.Xaml;

namespace Gapo.App;

public sealed record AppOptions(string? ReportPath, string? ScreenshotDirectory, ElementTheme Theme)
{
    private const string ReportOption = "--report=";
    private const string ScreenshotOption = "--screenshots=";
    private const string ThemeOption = "--theme=";

    public static AppOptions Parse(IEnumerable<string> arguments)
    {
        var options = new AppOptions(null, null, ElementTheme.Default);
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
        }

        return options;
    }

    private static ElementTheme ParseTheme(string text) => text.ToLowerInvariant() switch
    {
        "light" => ElementTheme.Light,
        "dark" => ElementTheme.Dark,
        _ => ElementTheme.Default,
    };
}
