using System.Globalization;

namespace Gapo.Sandbox.Contract;

public static class UnrealStyleSettings
{
    public const string Author = "Unreal-style GameUserSettings.ini (read at start-up)";

    private const string ScalabilitySection = "scalabilitygroups";
    private const string UserSettingsSection = "/script/engine.gameusersettings";
    private const int LowestResolutionScale = 50;
    private const int HighestResolutionScale = 100;
    private const int NearRenderDistance = 600;
    private const int FarRenderDistance = 1000;
    private const int FullRenderDistance = 1400;

    public static SandboxSettings FromIni(string text)
    {
        var values = ReadValues(text);
        var settings = SandboxSettings.UnoptimisedDefaults();
        settings.ModifiedBy = Author;
        if (Number(values, ScalabilitySection, "sg.ResolutionQuality") is { } scale)
        {
            settings.ResolutionScale = Math.Clamp((int)Math.Round(scale), LowestResolutionScale, HighestResolutionScale);
        }

        if (Level(values, "sg.AntiAliasingQuality") is { } antiAliasing)
        {
            settings.AntiAliasing = antiAliasing switch { 0 => AntiAliasing.Off, 1 or 2 => AntiAliasing.FXAA, _ => AntiAliasing.SSAA4x };
        }

        if (Level(values, "sg.ShadowQuality") is { } shadows)
        {
            settings.Shadows = shadows switch { 0 => ShadowQuality.Off, 1 or 2 => ShadowQuality.High, _ => ShadowQuality.Ultra };
        }

        if (Level(values, "sg.ViewDistanceQuality") is { } distance)
        {
            settings.SimulationDistance = distance switch { 0 => SimulationDistance.Near, 1 or 2 => SimulationDistance.Far, _ => SimulationDistance.Unlimited };
            settings.RenderDistance = distance switch { 0 => NearRenderDistance, 1 or 2 => FarRenderDistance, _ => FullRenderDistance };
        }

        if (Level(values, "sg.PostProcessQuality") is { } postProcess)
        {
            settings.Fog = postProcess switch { 0 => FogQuality.Off, 1 or 2 => FogQuality.Simple, _ => FogQuality.Volumetric };
        }

        if (Level(values, "sg.EffectsQuality") is { } effects)
        {
            settings.Particles = effects switch { 0 => ParticleQuality.Off, 1 => ParticleQuality.Low, 2 => ParticleQuality.Medium, 3 => ParticleQuality.High, _ => ParticleQuality.Ultra };
        }

        if (Level(values, "sg.ReflectionQuality") is { } reflections)
        {
            settings.WaterReflections = reflections switch { 0 => WaterQuality.Off, 1 or 2 => WaterQuality.Simple, _ => WaterQuality.Ultra };
        }

        if (Level(values, "sg.TextureQuality") is { } textures)
        {
            settings.Textures = textures switch { 0 or 1 => TextureQuality.Low, 2 => TextureQuality.High, _ => TextureQuality.Ultra };
        }

        if (Level(values, "sg.FoliageQuality") is { } foliage)
        {
            settings.Clouds = foliage switch { 0 => CloudQuality.Off, 1 or 2 => CloudQuality.Low, _ => CloudQuality.Ultra };
        }

        if (Number(values, UserSettingsSection, "FrameRateLimit") is { } cap)
        {
            settings.FrameRateCap = Math.Max(0, (int)Math.Round(cap));
        }

        if (values.TryGetValue((UserSettingsSection, "busevsync"), out var vsync))
        {
            settings.VSync = string.Equals(vsync, "True", StringComparison.OrdinalIgnoreCase);
        }

        return settings;
    }

    private static Dictionary<(string Section, string Key), string> ReadValues(string text)
    {
        var values = new Dictionary<(string, string), string>();
        var section = string.Empty;
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length >= 2 && line[0] == '[' && line[^1] == ']')
            {
                section = line[1..^1].Trim().ToLowerInvariant();
                continue;
            }

            var equals = line.IndexOf('=');
            if (equals > 0 && !line.StartsWith(';'))
            {
                values[(section, line[..equals].Trim().ToLowerInvariant())] = line[(equals + 1)..].Trim();
            }
        }

        return values;
    }

    private static int? Level(Dictionary<(string, string), string> values, string key) =>
        Number(values, ScalabilitySection, key) is { } level ? Math.Clamp((int)Math.Round(level), 0, 4) : null;

    private static double? Number(Dictionary<(string, string), string> values, string section, string key) =>
        values.TryGetValue((section, key.ToLowerInvariant()), out var text) && double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) ? number : null;
}
