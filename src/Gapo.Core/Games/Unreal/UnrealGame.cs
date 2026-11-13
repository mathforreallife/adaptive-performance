using System.Globalization;
using Gapo.Core.Optimisation;

namespace Gapo.Core.Games.Unreal;

public sealed record UnrealSetting(string Section, string Key, string Label, IReadOnlyList<string> LevelNames);

public sealed record UnrealGame(InstalledSteamGame Game, string ProjectName, string ShippingExecutable, IReadOnlyList<string> SettingsFileCandidates, string? AntiCheat)
{
    public bool IsProtectedByAntiCheat => AntiCheat is not null;
}

public sealed record UnrealSettingsRead(string Path, IReadOnlyDictionary<string, string> Values);

public static class UnrealSettings
{
    public const string ScalabilitySection = "ScalabilityGroups";
    public const string UserSettingsSection = "/Script/Engine.GameUserSettings";
    public const string FileName = "GameUserSettings.ini";

    private static readonly string[] QualityLevels = ["Low", "Medium", "High", "Epic", "Cinematic"];

    public static IReadOnlyList<UnrealSetting> Known { get; } =
    [
        new(ScalabilitySection, "sg.ResolutionQuality", "Resolution scale", []),
        new(ScalabilitySection, "sg.ViewDistanceQuality", "View distance", QualityLevels),
        new(ScalabilitySection, "sg.AntiAliasingQuality", "Anti-aliasing", QualityLevels),
        new(ScalabilitySection, "sg.ShadowQuality", "Shadows", QualityLevels),
        new(ScalabilitySection, "sg.GlobalIlluminationQuality", "Global illumination", QualityLevels),
        new(ScalabilitySection, "sg.ReflectionQuality", "Reflections", QualityLevels),
        new(ScalabilitySection, "sg.PostProcessQuality", "Post-processing", QualityLevels),
        new(ScalabilitySection, "sg.TextureQuality", "Textures", QualityLevels),
        new(ScalabilitySection, "sg.EffectsQuality", "Effects", QualityLevels),
        new(ScalabilitySection, "sg.FoliageQuality", "Foliage", QualityLevels),
        new(ScalabilitySection, "sg.ShadingQuality", "Shading", QualityLevels),
        new(UserSettingsSection, "FrameRateLimit", "Frame-rate cap", []),
        new(UserSettingsSection, "bUseVSync", "VSync", []),
        new(UserSettingsSection, "ResolutionSizeX", "Resolution width", []),
        new(UserSettingsSection, "ResolutionSizeY", "Resolution height", []),
        new(UserSettingsSection, "FullscreenMode", "Window mode", []),
    ];

    public static IReadOnlyList<SettingDefinition> Definitions { get; } = Known.Select(Define).ToList();

    public static UnrealSetting? Find(string key) => Known.FirstOrDefault(setting => setting.Key == key);

    public static IReadOnlyDictionary<string, string> Read(IniDocument document)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var setting in Known)
        {
            if (document.Get(setting.Section, setting.Key) is { } value)
            {
                values[setting.Key] = value;
            }
        }

        return values;
    }

    private static SettingDefinition Define(UnrealSetting setting)
    {
        IReadOnlyList<SettingChoice> choices = setting.Key switch
        {
            _ when setting.LevelNames.Count > 0 => setting.LevelNames.Select((name, level) => new SettingChoice(level.ToString(CultureInfo.InvariantCulture), name)).ToArray(),
            "sg.ResolutionQuality" => new[] { 50, 60, 67, 75, 85, 100 }.Select(percent => new SettingChoice(percent.ToString(CultureInfo.InvariantCulture), percent + " %")).ToArray(),
            "FrameRateLimit" => new[] { 0, 30, 60, 120, 144 }.Select(fps => new SettingChoice(fps.ToString(CultureInfo.InvariantCulture), fps == 0 ? "Unlimited" : fps + " FPS")).ToArray(),
            "bUseVSync" => [new SettingChoice("True", "On"), new SettingChoice("False", "Off")],
            "FullscreenMode" => [new SettingChoice("0", "Fullscreen"), new SettingChoice("1", "Borderless"), new SettingChoice("2", "Windowed")],
            _ => [],
        };
        return new SettingDefinition(setting.Key, setting.Label, choices);
    }

    public static string Describe(UnrealSetting setting, string value)
    {
        if (setting.LevelNames.Count > 0 && int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var level))
        {
            return level >= 0 && level < setting.LevelNames.Count ? setting.LevelNames[level] : value;
        }

        return setting.Key switch
        {
            "FrameRateLimit" when double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var cap) => cap <= 0 ? "Unlimited" : cap.ToString("0", CultureInfo.InvariantCulture) + " FPS",
            "bUseVSync" => string.Equals(value, "True", StringComparison.OrdinalIgnoreCase) ? "On" : "Off",
            "FullscreenMode" => value switch { "0" => "Fullscreen", "1" => "Borderless", "2" => "Windowed", _ => value },
            "sg.ResolutionQuality" when double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var scale) => scale.ToString("0.#", CultureInfo.InvariantCulture) + " %",
            _ => value,
        };
    }
}

public static class UnrealGameDetector
{
    private const string ShippingSuffix = "-Win64-Shipping.exe";
    private const string BinariesFolder = "Binaries";
    private const string Win64Folder = "Win64";
    private const string EngineFolder = "Engine";
    private const string SavedConfig = @"Saved\Config";

    private static readonly (string Folder, string Name)[] AntiCheatFolders =
    [
        ("EasyAntiCheat", "Easy Anti-Cheat"),
        ("EasyAntiCheat_EOS", "Easy Anti-Cheat"),
        ("BattlEye", "BattlEye"),
    ];

    public static UnrealGame? Detect(InstalledSteamGame game, IGameFileSource files, string localAppData, string documents)
    {
        ArgumentNullException.ThrowIfNull(game);
        ArgumentNullException.ThrowIfNull(files);
        var root = game.InstallDirectory;
        if (!files.DirectoryExists(Path.Combine(root, EngineFolder)))
        {
            return null;
        }

        foreach (var project in files.ListDirectories(root))
        {
            var projectName = Path.GetFileName(project);
            if (string.Equals(projectName, EngineFolder, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var binaries = Path.Combine(project, BinariesFolder, Win64Folder);
            var shipping = files.ListFiles(binaries, "*" + ShippingSuffix).FirstOrDefault();
            if (shipping is null)
            {
                continue;
            }

            string[] platforms = ["Windows", "WindowsNoEditor"];
            var candidates = new List<string>();
            foreach (var baseFolder in new[] { Path.Combine(localAppData, projectName), Path.Combine(documents, "My Games", projectName) })
            {
                candidates.AddRange(platforms.Select(platform => Path.Combine(baseFolder, SavedConfig, platform, UnrealSettings.FileName)));
            }

            return new UnrealGame(game, projectName, shipping, candidates, AntiCheatIn(root, files));
        }

        return null;
    }

    public static UnrealSettingsRead? ReadSettings(UnrealGame game, IGameFileSource files)
    {
        foreach (var candidate in game.SettingsFileCandidates)
        {
            if (files.ReadText(candidate) is { } text)
            {
                return new UnrealSettingsRead(candidate, UnrealSettings.Read(IniDocument.Parse(text)));
            }
        }

        return null;
    }

    private static string? AntiCheatIn(string root, IGameFileSource files)
    {
        foreach (var (folder, name) in AntiCheatFolders)
        {
            if (files.DirectoryExists(Path.Combine(root, folder)))
            {
                return name;
            }
        }

        return files.ListFiles(root, "BEService*.exe").Count > 0 ? "BattlEye" : null;
    }
}
