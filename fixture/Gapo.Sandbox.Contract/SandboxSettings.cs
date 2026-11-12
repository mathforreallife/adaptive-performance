using System.Globalization;
using System.Text;

namespace Gapo.Sandbox.Contract;

public enum AntiAliasing
{
    Off,
    FXAA,
    SSAA4x,
}

public enum ShadowQuality
{
    Off,
    High,
    Ultra,
}

public enum WaterQuality
{
    Off,
    Simple,
    Ultra,
}

public enum CloudQuality
{
    Off,
    Low,
    Ultra,
}

public enum FogQuality
{
    Off,
    Simple,
    Volumetric,
}

public enum ParticleQuality
{
    Off,
    Low,
    Medium,
    High,
    Ultra,
}

public enum SimulationDistance
{
    Near,
    Far,
    Unlimited,
}

public enum TextureQuality
{
    Low,
    High,
    Ultra,
}

public sealed class SandboxSettings
{
    public const string UnoptimisedAuthor = "Game defaults (unoptimised)";

    private const int FileAccessRetries = 20;
    private const int SaveRetryDelayMs = 15;
    private const int LoadRetryDelayMs = 10;
    private const string ModifiedByKey = "ModifiedBy";

    public int ResolutionScale { get; set; } = 100;

    public AntiAliasing AntiAliasing { get; set; } = AntiAliasing.SSAA4x;

    public int RenderDistance { get; set; } = 1400;

    public bool TerrainLod { get; set; }

    public ShadowQuality Shadows { get; set; } = ShadowQuality.Ultra;

    public WaterQuality WaterReflections { get; set; } = WaterQuality.Ultra;

    public CloudQuality Clouds { get; set; } = CloudQuality.Ultra;

    public FogQuality Fog { get; set; } = FogQuality.Volumetric;

    public ParticleQuality Particles { get; set; } = ParticleQuality.Ultra;

    public TextureQuality Textures { get; set; } = TextureQuality.Ultra;

    public SimulationDistance SimulationDistance { get; set; } = SimulationDistance.Unlimited;

    public int EntityDensity { get; set; } = 100;

    public int PhysicsSubsteps { get; set; } = 8;

    public int RenderThreads { get; set; } = 1;

    public int FrameRateCap { get; set; }

    public bool VSync { get; set; }

    public int AutosaveSeconds { get; set; } = 10;

    public int Revision { get; set; }

    public string ModifiedBy { get; set; } = UnoptimisedAuthor;

    public static SandboxSettings UnoptimisedDefaults() => new();

    public SandboxSettings Clone() => (SandboxSettings)MemberwiseClone();

    public bool IsUnoptimisedDefaults()
    {
        var defaults = UnoptimisedDefaults();
        defaults.Revision = Revision;
        defaults.ModifiedBy = ModifiedBy;
        return defaults.ToIni(withHeader: false) == ToIni(withHeader: false);
    }

    public string ToIni(bool withHeader = true)
    {
        var culture = CultureInfo.InvariantCulture;
        var ini = new StringBuilder();
        if (withHeader)
        {
            ini.AppendLine("; GAPO Sandbox settings. The game reloads this file automatically while it runs.");
            ini.AppendLine("; The GAPO Optimiser edits it one setting at a time and reverts anything that does not help.");
            ini.AppendLine();
        }

        ini.AppendLine("[Graphics]");
        ini.AppendLine("ResolutionScale=" + ResolutionScale.ToString(culture) + "        ; 50-100 (% of base resolution)");
        ini.AppendLine("AntiAliasing=" + AntiAliasing + "           ; Off, FXAA, SSAA4x");
        ini.AppendLine("RenderDistance=" + RenderDistance.ToString(culture) + "        ; 300-1600");
        ini.AppendLine("TerrainLOD=" + OnOff(TerrainLod) + "                ; On, Off");
        ini.AppendLine("Shadows=" + Shadows + "                ; Off, High, Ultra");
        ini.AppendLine("WaterReflections=" + WaterReflections + "       ; Off, Simple, Ultra");
        ini.AppendLine("Clouds=" + Clouds + "                 ; Off, Low, Ultra");
        ini.AppendLine("Fog=" + Fog + "               ; Off, Simple, Volumetric");
        ini.AppendLine("Particles=" + Particles + "              ; Off, Low, Medium, High, Ultra");
        ini.AppendLine("Textures=" + Textures + "               ; Low, High, Ultra");
        ini.AppendLine();
        ini.AppendLine("[Simulation]");
        ini.AppendLine("SimulationDistance=" + SimulationDistance + "  ; Near, Far, Unlimited");
        ini.AppendLine("EntityDensity=" + EntityDensity.ToString(culture) + "          ; 10-100 (%)");
        ini.AppendLine("PhysicsSubsteps=" + PhysicsSubsteps.ToString(culture) + "          ; 1-8");
        ini.AppendLine();
        ini.AppendLine("[Performance]");
        ini.AppendLine("RenderThreads=" + RenderThreads.ToString(culture) + "            ; 1-64");
        ini.AppendLine("FrameRateCap=" + FrameRateCap.ToString(culture) + "             ; 0 = unlimited");
        ini.AppendLine("VSync=" + OnOff(VSync));
        ini.AppendLine();
        ini.AppendLine("[System]");
        ini.AppendLine("AutosaveSeconds=" + AutosaveSeconds.ToString(culture) + "         ; 0 = off");
        ini.AppendLine();
        ini.AppendLine("[Meta]");
        ini.AppendLine("Revision=" + Revision.ToString(culture));
        ini.AppendLine(ModifiedByKey + "=" + ModifiedBy);
        return ini.ToString();
    }

    public static SandboxSettings FromIni(string text)
    {
        var values = ReadKeyValuePairs(text);
        var settings = new SandboxSettings();
        settings.ResolutionScale = Math.Clamp(ReadInt(values, "ResolutionScale", settings.ResolutionScale), 50, 100);
        settings.AntiAliasing = ReadEnum(values, "AntiAliasing", settings.AntiAliasing);
        settings.RenderDistance = Math.Clamp(ReadInt(values, "RenderDistance", settings.RenderDistance), 300, 1600);
        settings.TerrainLod = ReadBool(values, "TerrainLOD", settings.TerrainLod);
        settings.Shadows = ReadEnum(values, "Shadows", settings.Shadows);
        settings.WaterReflections = ReadEnum(values, "WaterReflections", settings.WaterReflections);
        settings.Clouds = ReadEnum(values, "Clouds", settings.Clouds);
        settings.Fog = ReadEnum(values, "Fog", settings.Fog);
        settings.Particles = ReadEnum(values, "Particles", settings.Particles);
        settings.Textures = ReadEnum(values, "Textures", settings.Textures);
        settings.SimulationDistance = ReadEnum(values, "SimulationDistance", settings.SimulationDistance);
        settings.EntityDensity = Math.Clamp(ReadInt(values, "EntityDensity", settings.EntityDensity), 10, 100);
        settings.PhysicsSubsteps = Math.Clamp(ReadInt(values, "PhysicsSubsteps", settings.PhysicsSubsteps), 1, 8);
        settings.RenderThreads = Math.Clamp(ReadInt(values, "RenderThreads", settings.RenderThreads), 1, 64);
        settings.FrameRateCap = Math.Clamp(ReadInt(values, "FrameRateCap", settings.FrameRateCap), 0, 1000);
        settings.VSync = ReadBool(values, "VSync", settings.VSync);
        settings.AutosaveSeconds = Math.Clamp(ReadInt(values, "AutosaveSeconds", settings.AutosaveSeconds), 0, 3600);
        settings.Revision = ReadInt(values, "Revision", 0);
        if (values.TryGetValue(ModifiedByKey, out var author) && author.Length > 0)
        {
            settings.ModifiedBy = author;
        }

        return settings;
    }

    public void Save(string path)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var temporaryPath = path + ".tmp";
        File.WriteAllText(temporaryPath, ToIni(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                File.Move(temporaryPath, path, overwrite: true);
                return;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException && attempt < FileAccessRetries)
            {
                Thread.Sleep(SaveRetryDelayMs);
            }
        }
    }

    public static SandboxSettings? Load(string path)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var reader = new StreamReader(stream, Encoding.UTF8);
                return FromIni(reader.ReadToEnd());
            }
            catch (FileNotFoundException)
            {
                return null;
            }
            catch (DirectoryNotFoundException)
            {
                return null;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                if (attempt >= FileAccessRetries)
                {
                    return null;
                }

                Thread.Sleep(LoadRetryDelayMs);
            }
        }
    }

    public static string Describe(AntiAliasing antiAliasing) =>
        antiAliasing == AntiAliasing.SSAA4x ? "4× SSAA" : antiAliasing.ToString();

    private static Dictionary<string, string> ReadKeyValuePairs(string text)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var rawLine in text.Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.Length == 0 || line[0] == ';' || line[0] == '[')
            {
                continue;
            }

            var separator = line.IndexOf('=');
            if (separator <= 0)
            {
                continue;
            }

            var key = line[..separator].Trim();
            var value = line[(separator + 1)..];
            var trailingNote = value.IndexOf(';');
            if (trailingNote >= 0 && !key.Equals(ModifiedByKey, StringComparison.OrdinalIgnoreCase))
            {
                value = value[..trailingNote];
            }

            values[key] = value.Trim();
        }

        return values;
    }

    private static string OnOff(bool value) => value ? "On" : "Off";

    private static int ReadInt(Dictionary<string, string> values, string key, int fallback) =>
        values.TryGetValue(key, out var text) && int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : fallback;

    private static bool ReadBool(Dictionary<string, string> values, string key, bool fallback)
    {
        if (!values.TryGetValue(key, out var text))
        {
            return fallback;
        }

        return text.ToLowerInvariant() switch
        {
            "on" or "true" or "1" or "yes" => true,
            "off" or "false" or "0" or "no" => false,
            _ => fallback,
        };
    }

    private static TEnum ReadEnum<TEnum>(Dictionary<string, string> values, string key, TEnum fallback)
        where TEnum : struct, Enum =>
        values.TryGetValue(key, out var text) && Enum.TryParse<TEnum>(text, ignoreCase: true, out var parsed) && Enum.IsDefined(parsed)
            ? parsed
            : fallback;
}
