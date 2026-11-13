using Gapo.Core.Games;
using Gapo.Core.Games.Unreal;
using Gapo.Windows.Games;

namespace Gapo.Agent.Games;

public sealed record RecognisedGame(string Engine, string? AntiCheat, string? SettingsPath, IReadOnlyList<string> Settings)
{
    public bool CanBeOptimised => AntiCheat is null && SettingsPath is not null;
}

public static class GameRecognition
{
    public const string NothingChanged = "Nothing was changed.";

    private const string UnrealEngine = "Unreal Engine";
    private const int SettingsNamedInStatus = 6;

    public static RecognisedGame? Recognise(InstalledSteamGame game, string localAppData, string documents)
    {
        var files = new DiskGameFileSource();
        if (UnrealGameDetector.Detect(game, files, localAppData, documents) is not { } unreal)
        {
            return null;
        }

        var read = UnrealGameDetector.ReadSettings(unreal, files);
        var settings = read is null
            ? []
            : UnrealSettings.Known
                .Where(setting => read.Values.ContainsKey(setting.Key))
                .Select(setting => setting.Label + " " + UnrealSettings.Describe(setting, read.Values[setting.Key]))
                .ToList();
        return new RecognisedGame(UnrealEngine, unreal.AntiCheat, read?.Path, settings);
    }

    public static string Explain(RecognisedGame? recognised, string unknownGameText)
    {
        if (recognised is null)
        {
            return unknownGameText;
        }

        if (recognised.AntiCheat is { } antiCheat)
        {
            return $"GAPO recognises this as an {recognised.Engine} game protected by {antiCheat}. GAPO measures it but never changes the files of a game with anti-cheat. {NothingChanged}";
        }

        if (recognised.SettingsPath is null)
        {
            return $"GAPO recognises this as an {recognised.Engine} game but found no settings file yet; the game writes one after you change a setting or quit once. {NothingChanged}";
        }

        var named = string.Join(", ", recognised.Settings.Take(SettingsNamedInStatus));
        return $"GAPO recognises this as an {recognised.Engine} game and read its graphics settings ({named}). The game applies them when it starts, so GAPO tests one change per restart and asks you to restart the game between tests. Choose a goal and optimise when you are ready.";
    }
}
