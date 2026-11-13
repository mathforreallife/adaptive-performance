using System.Globalization;
using Gapo.Core.Diagnosis;

namespace Gapo.Core.Games.Unreal;

public sealed record UnrealCandidate(UnrealSetting Setting, string From, string To, double ExpectedShareSaved, double VisualLoss, string Why)
{
    private const double ValueOffset = 0.6;

    public string Title => $"{Setting.Label}: {UnrealSettings.Describe(Setting, From)} → {UnrealSettings.Describe(Setting, To)}";

    public double Value => ExpectedShareSaved / (VisualLoss + ValueOffset);
}

public static class UnrealKnowledge
{
    private const int HighestLevelWorthLowering = 3;
    private const string ResolutionKey = "sg.ResolutionQuality";
    private const double FullResolution = 100;
    private const double LoweredResolution = 85;

    private static readonly IReadOnlyDictionary<string, Prior> CpuPriors = new Dictionary<string, Prior>
    {
        ["sg.ViewDistanceQuality"] = new(0.20, 0.30, "View distance decides how much of the world is drawn and updated each frame; on a CPU-limited game fewer objects means less work on the busy thread."),
        ["sg.ShadowQuality"] = new(0.10, 0.30, "Every shadow-casting object is drawn again for the shadow maps, which adds CPU work per object."),
        ["sg.FoliageQuality"] = new(0.08, 0.20, "Foliage density sets how many plants are placed and drawn."),
        ["sg.EffectsQuality"] = new(0.05, 0.20, "Effects quality sets particle counts and their simulation cost."),
        ["sg.AntiAliasingQuality"] = new(0.03, 0.40, "Anti-aliasing mostly costs graphics-card time, so on a CPU-limited game the gain is expected to be small."),
        ["sg.PostProcessQuality"] = new(0.03, 0.30, "Post-processing mostly costs graphics-card time."),
    };

    private static readonly IReadOnlyDictionary<string, Prior> GraphicsPriors = new Dictionary<string, Prior>
    {
        ["sg.AntiAliasingQuality"] = new(0.15, 0.40, "Higher anti-aliasing levels render or resolve extra samples per pixel; one level down usually saves a large share of graphics work."),
        [ResolutionKey] = new(0.20, 0.80, "Rendering at 85 % of the resolution draws about 28 % fewer pixels and is upscaled for display, so it looks slightly softer."),
        ["sg.ShadowQuality"] = new(0.12, 0.30, "Shadow maps are drawn at high resolution and filtered; one level down halves much of that work."),
        ["sg.PostProcessQuality"] = new(0.10, 0.30, "Post-processing (bloom, depth of field, motion blur, ambient occlusion) runs over every pixel."),
        ["sg.ViewDistanceQuality"] = new(0.08, 0.30, "A shorter view distance draws fewer distant objects."),
        ["sg.ReflectionQuality"] = new(0.08, 0.20, "Reflections re-render or trace parts of the scene."),
        ["sg.GlobalIlluminationQuality"] = new(0.10, 0.30, "Global illumination computes bounced light every frame."),
        ["sg.FoliageQuality"] = new(0.06, 0.20, "Foliage density sets how many plants are drawn."),
        ["sg.EffectsQuality"] = new(0.05, 0.20, "Effects quality sets particle counts and their overdraw."),
    };

    public static IReadOnlyList<UnrealCandidate> Candidates(IReadOnlyDictionary<string, string> current, Bottleneck bottleneck)
    {
        ArgumentNullException.ThrowIfNull(current);
        var priors = bottleneck is Bottleneck.CpuOneThreadLimit or Bottleneck.CpuMainThread or Bottleneck.CpuAllProcessors or Bottleneck.RenderSingleThread
            ? CpuPriors
            : GraphicsPriors;
        var candidates = new List<UnrealCandidate>();
        foreach (var (key, prior) in priors)
        {
            var setting = UnrealSettings.Known.Single(known => known.Key == key);
            if (!current.TryGetValue(key, out var value) || !double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var number))
            {
                continue;
            }

            if (key == ResolutionKey)
            {
                if (number >= FullResolution)
                {
                    candidates.Add(new UnrealCandidate(setting, value, LoweredResolution.ToString(CultureInfo.InvariantCulture), prior.ShareSaved, prior.VisualLoss, prior.Why));
                }

                continue;
            }

            var level = (int)Math.Round(number);
            if (level >= HighestLevelWorthLowering)
            {
                var lower = level - 1;
                candidates.Add(new UnrealCandidate(setting, value, lower.ToString(CultureInfo.InvariantCulture), prior.ShareSaved, prior.VisualLoss, prior.Why));
            }
        }

        return candidates.OrderByDescending(candidate => candidate.Value).ToList();
    }

    private sealed record Prior(double ShareSaved, double VisualLoss, string Why);
}
