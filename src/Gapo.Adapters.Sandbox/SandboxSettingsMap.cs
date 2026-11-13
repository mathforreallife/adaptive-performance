using System.Globalization;
using System.Text;
using Gapo.Core.Optimisation;
using Gapo.Sandbox.Contract;

namespace Gapo.Adapters.Sandbox;

public static class SandboxSettingsMap
{
    public const string ResolutionScale = "ResolutionScale";
    public const string AntiAliasingKey = "AntiAliasing";
    public const string RenderDistance = "RenderDistance";
    public const string TerrainLod = "TerrainLOD";
    public const string Shadows = "Shadows";
    public const string WaterReflections = "WaterReflections";
    public const string Clouds = "Clouds";
    public const string Fog = "Fog";
    public const string Particles = "Particles";
    public const string Textures = "Textures";
    public const string SimulationDistanceKey = "SimulationDistance";
    public const string EntityDensity = "EntityDensity";
    public const string PhysicsSubsteps = "PhysicsSubsteps";
    public const string RenderThreads = "RenderThreads";
    public const string FrameRateCap = "FrameRateCap";
    public const string VSync = "VSync";
    public const string AutosaveSeconds = "AutosaveSeconds";
    public const string On = "On";
    public const string Off = "Off";

    public static GameSettings ToGameSettings(SandboxSettings settings, int revision) =>
        new(
            new Dictionary<string, string>
            {
                [ResolutionScale] = Number(settings.ResolutionScale),
                [AntiAliasingKey] = settings.AntiAliasing.ToString(),
                [RenderDistance] = Number(settings.RenderDistance),
                [TerrainLod] = settings.TerrainLod ? On : Off,
                [Shadows] = settings.Shadows.ToString(),
                [WaterReflections] = settings.WaterReflections.ToString(),
                [Clouds] = settings.Clouds.ToString(),
                [Fog] = settings.Fog.ToString(),
                [Particles] = settings.Particles.ToString(),
                [Textures] = settings.Textures.ToString(),
                [SimulationDistanceKey] = settings.SimulationDistance.ToString(),
                [EntityDensity] = Number(settings.EntityDensity),
                [PhysicsSubsteps] = Number(settings.PhysicsSubsteps),
                [RenderThreads] = Number(settings.RenderThreads),
                [FrameRateCap] = Number(settings.FrameRateCap),
                [VSync] = settings.VSync ? On : Off,
                [AutosaveSeconds] = Number(settings.AutosaveSeconds),
            },
            revision);

    public static SandboxSettings ToSandboxSettings(GameSettings settings, string modifiedBy)
    {
        var ini = new StringBuilder();
        foreach (var (key, value) in settings.Values)
        {
            ini.Append(key).Append('=').Append(value).Append('\n');
        }

        var sandbox = SandboxSettings.FromIni(ini.ToString());
        sandbox.Revision = settings.Revision;
        sandbox.ModifiedBy = modifiedBy;
        return sandbox;
    }

    private static string Number(int value) => value.ToString(CultureInfo.InvariantCulture);
}
