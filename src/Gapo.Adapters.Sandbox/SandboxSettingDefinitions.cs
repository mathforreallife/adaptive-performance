using System.Globalization;
using Gapo.Core.Optimisation;

namespace Gapo.Adapters.Sandbox;

public static class SandboxSettingDefinitions
{
    public static IReadOnlyList<SettingDefinition> All { get; } =
    [
        new(SandboxSettingsMap.ResolutionScale, "Resolution scale", Numbers([50, 60, 70, 75, 80, 85, 90, 100], value => value + " %")),
        new(SandboxSettingsMap.AntiAliasingKey, "Anti-aliasing", [new("Off", "Off"), new("FXAA", "FXAA"), new("SSAA4x", "4× SSAA")]),
        new(SandboxSettingsMap.RenderDistance, "Render distance", Numbers([600, 800, 1000, 1200, 1400], value => value + " m")),
        new(SandboxSettingsMap.TerrainLod, "Terrain LOD", OnOff()),
        new(SandboxSettingsMap.Shadows, "Shadows", Words("Off", "High", "Ultra")),
        new(SandboxSettingsMap.WaterReflections, "Water reflections", Words("Off", "Simple", "Ultra")),
        new(SandboxSettingsMap.Clouds, "Clouds", Words("Off", "Low", "Ultra")),
        new(SandboxSettingsMap.Fog, "Fog", Words("Off", "Simple", "Volumetric")),
        new(SandboxSettingsMap.Particles, "Particles", Words("Off", "Low", "Medium", "High", "Ultra")),
        new(SandboxSettingsMap.Textures, "Textures", Words("Low", "High", "Ultra")),
        new(SandboxSettingsMap.SimulationDistanceKey, "Simulation distance", Words("Near", "Far", "Unlimited")),
        new(SandboxSettingsMap.EntityDensity, "Creature density", Numbers([50, 75, 100], value => value + " %")),
        new(SandboxSettingsMap.PhysicsSubsteps, "Physics substeps", Numbers([2, 4, 8], value => value.ToString(CultureInfo.InvariantCulture))),
        new(SandboxSettingsMap.RenderThreads, "Render threads", Numbers([1, 2, 4, 8], value => value.ToString(CultureInfo.InvariantCulture))),
        new(SandboxSettingsMap.FrameRateCap, "Frame-rate cap", Numbers([0, 30, 60, 120, 144], value => value == 0 ? "Unlimited" : value + " FPS")),
        new(SandboxSettingsMap.VSync, "VSync", OnOff()),
        new(SandboxSettingsMap.AutosaveSeconds, "Autosave interval", Numbers([10, 60, 300], value => value < 60 ? value + " s" : value / 60 + " min")),
    ];

    private static SettingChoice[] Words(params string[] words) => words.Select(word => new SettingChoice(word, word)).ToArray();

    private static SettingChoice[] OnOff() => [new(SandboxSettingsMap.On, "On"), new(SandboxSettingsMap.Off, "Off")];

    private static SettingChoice[] Numbers(int[] values, Func<int, string> label) =>
        values.Select(value => new SettingChoice(value.ToString(CultureInfo.InvariantCulture), label(value))).ToArray();
}
