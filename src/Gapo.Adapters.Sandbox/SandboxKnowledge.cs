using System.Globalization;
using Gapo.Core.Diagnosis;
using Gapo.Core.Hardware;
using Gapo.Core.Measurement;
using Gapo.Core.Optimisation;
using Gapo.Sandbox.Contract;
using static Gapo.Adapters.Sandbox.SandboxSettingsMap;

namespace Gapo.Adapters.Sandbox;

public sealed class SandboxKnowledge : IGameKnowledge
{
    public const string GameName = "GAPO Sandbox";

    private const int MostRenderThreads = 16;
    private const int LongAutosaveIntervalSeconds = 300;
    private const int AutosaveIntervalThatIsAlreadyLong = 120;
    private const double CheapPresentMs = 3;

    private static readonly int[] ParticleCounts = [0, 700, 2000, 4500, 9000];
    private static readonly string[] ParticleLevels = ["Off", "Low", "Medium", "High", "Ultra"];

    public IReadOnlyList<SettingCandidate> Candidates(GameSettings current, WindowResult reference, CpuFacts processor, bool memoryIsUnderPressure)
    {
        if (reference.Stages is not { } stages)
        {
            return [];
        }

        var candidates = new List<SettingCandidate>();
        AddRenderThreads(candidates, current, stages, processor);
        AddRenderingSteps(candidates, current, stages);
        AddSimulationSteps(candidates, current, stages);
        AddVisibleReductions(candidates, current, stages);
        if (memoryIsUnderPressure && current.Is(Textures, "Ultra"))
        {
            candidates.Add(new SettingCandidate("Textures", "Ultra", "High", ChangeKind.MemoryRelief, 2, 0, "System memory is under pressure; High textures use less of it.", Change(Textures, "High")));
        }

        return candidates;
    }

    public SettingCandidate? HitchFix(GameSettings current, FrameStatistics hitches)
    {
        var interval = current.Number(AutosaveSeconds);
        if (hitches.StorageHitches == 0 || interval <= 0 || interval >= AutosaveIntervalThatIsAlreadyLong)
        {
            return null;
        }

        return new SettingCandidate(
            "Autosave interval", Text($"{interval} s"), "5 min", ChangeKind.HitchFix, 0, 0,
            Text($"Every {interval} s the game saves the world on its main thread and waits for the disk, freezing for up to {hitches.LongestStorageHitchMs:0} ms. Saving every 5 minutes removes almost all of those freezes."),
            Change(AutosaveSeconds, Number(LongAutosaveIntervalSeconds)));
    }

    public SettingCandidate FrameCap(GameSettings current, int capFps, string why)
    {
        var currentCap = current.Number(FrameRateCap);
        return new SettingCandidate(
            OptimisationEngine.FrameCapSetting, currentCap == 0 ? "Unlimited" : Text($"{currentCap} FPS"), Text($"{capFps} FPS"), ChangeKind.Pacing, 0, 0, why,
            Change(FrameRateCap, Number(capFps)));
    }

    public IReadOnlyList<string> NotWorthTouching(GameSettings current, WindowResult reference, bool memoryIsUnderPressure)
    {
        var notes = new List<string>();
        if (!memoryIsUnderPressure && !current.Is(Textures, "Low"))
        {
            notes.Add("Texture quality: memory is not under pressure, and textures do not cost frame time in this game. Lowering them would cost visuals for no gain.");
        }

        if (reference.Stages is { PresentMs: > 0 and < CheapPresentMs } stages)
        {
            notes.Add(Text($"Window and present mode: copying the frame to the screen takes only {stages.PresentMs:0.0} ms."));
        }

        return notes;
    }

    private static void AddRenderThreads(List<SettingCandidate> candidates, GameSettings current, StageTimes stages, CpuFacts processor)
    {
        var logical = Math.Max(1, processor.LogicalProcessors);
        var physical = Math.Max(1, processor.Cores > 0 ? processor.Cores : logical);
        var threads = current.Number(RenderThreads);
        var wanted = Math.Min(physical, MostRenderThreads);
        if (threads < wanted && logical > 1)
        {
            var efficiency = wanted >= 4 ? 0.8 : 0.85;
            var plural = threads == 1 ? string.Empty : "s";
            candidates.Add(new SettingCandidate(
                "Render threads", Number(threads), Number(wanted), ChangeKind.Throughput, 0, stages.RenderMs * (1 - (threads / (wanted * efficiency))),
                Text($"Rendering runs on {threads} thread{plural} but your CPU has {physical} cores. The render passes are parallel work, so spreading them over {wanted} threads should cut {stages.RenderMs:0.0} ms of rendering substantially."),
                Change(RenderThreads, Number(wanted))));
        }
        else if (processor.HasSimultaneousMultithreading && logical > physical && threads == physical)
        {
            candidates.Add(new SettingCandidate(
                "Render threads", Number(physical), Number(logical), ChangeKind.Throughput, 0, stages.RenderMs * 0.12,
                "Your CPU runs 2 threads per core. Whether that helps is game-specific, so it has to be measured, not assumed.",
                Change(RenderThreads, Number(logical))));
        }
    }

    private static void AddRenderingSteps(List<SettingCandidate> candidates, GameSettings current, StageTimes stages)
    {
        var terrain = stages.PassMs(SandboxPasses.Terrain);
        var shadows = stages.PassMs(SandboxPasses.Shadows);
        var water = stages.PassMs(SandboxPasses.Water);
        var sky = stages.PassMs(SandboxPasses.Sky);
        var fog = stages.PassMs(SandboxPasses.Fog);
        var sprites = stages.PassMs(SandboxPasses.Sprites);
        var antiAliasing = stages.PassMs(SandboxPasses.AntiAliasing);
        if (current.Is(TerrainLod, Off))
        {
            candidates.Add(Step("Terrain LOD", Off, On, 1, terrain * 0.55, Text($"Terrain is sampled at full detail all the way to the horizon ({terrain:0.0} ms). Distant terrain covers few pixels, so coarser sampling there is nearly invisible."), TerrainLod, On));
        }

        if (current.Is(Shadows, "Ultra"))
        {
            candidates.Add(Step("Shadows", "Ultra", "High", 1, shadows * 0.88, Text($"Ultra shadows ray-march every pixel every frame ({shadows:0.0} ms). High uses a baked shadow map that looks almost identical."), Shadows, "High"));
        }
        else if (current.Is(Shadows, "High"))
        {
            candidates.Add(Step("Shadows", "High", Off, 5, shadows * 0.95, Text($"Shadows cost {shadows:0.0} ms. Turning them off saves that but flattens the terrain visibly."), Shadows, Off));
        }

        if (current.Is(AntiAliasingKey, nameof(AntiAliasing.SSAA4x)))
        {
            candidates.Add(Step("Anti-aliasing", "4× SSAA", "FXAA", 2, (terrain * 0.5) + ((shadows + water + sky + fog + sprites) * 0.75) + (antiAliasing * 0.4),
                Text($"4× supersampling renders four times the pixels ({stages.RenderWidth * 2}×{stages.RenderHeight * 2}) and averages them. FXAA smooths edges in one cheap pass."), AntiAliasingKey, nameof(AntiAliasing.FXAA)));
        }
        else if (current.Is(AntiAliasingKey, nameof(AntiAliasing.FXAA)))
        {
            candidates.Add(Step("Anti-aliasing", "FXAA", Off, 2, antiAliasing * 0.9, Text($"FXAA costs {antiAliasing:0.0} ms; turning it off leaves jagged edges."), AntiAliasingKey, Off));
        }

        if (current.Is(WaterReflections, "Ultra"))
        {
            candidates.Add(Step("Water reflections", "Ultra", "Simple", 2, water * 0.85, Text($"Ultra reflections ray-march the terrain for every water pixel ({water:0.0} ms). Simple reflects the sky only."), WaterReflections, "Simple"));
        }
        else if (current.Is(WaterReflections, "Simple"))
        {
            candidates.Add(Step("Water reflections", "Simple", Off, 3, water * 0.9, Text($"Water shading costs {water:0.0} ms."), WaterReflections, Off));
        }

        if (current.Is(Clouds, "Ultra"))
        {
            candidates.Add(Step("Clouds", "Ultra", "Low", 1, sky * 0.75, Text($"Ultra clouds compute several octaves of noise per sky pixel every frame ({sky:0.0} ms); Low samples a pre-baked cloud texture that looks the same at this resolution."), Clouds, "Low"));
        }
        else if (current.Is(Clouds, "Low"))
        {
            candidates.Add(Step("Clouds", "Low", Off, 3, sky * 0.5, Text($"Sky and clouds cost {sky:0.0} ms."), Clouds, Off));
        }

        if (current.Is(Fog, "Volumetric"))
        {
            candidates.Add(Step("Fog", "Volumetric", "Simple", 1, fog * 0.7, Text($"Volumetric fog marches every pixel's view ray ({fog:0.0} ms). Simple distance fog is one lookup per pixel."), Fog, "Simple"));
        }
        else if (current.Is(Fog, "Simple"))
        {
            candidates.Add(Step("Fog", "Simple", Off, 4, fog, Text($"Fog costs {fog:0.0} ms but hides the edge of the render distance."), Fog, Off));
        }

        var particleLevel = Array.FindIndex(ParticleLevels, level => current.Is(Particles, level));
        if (particleLevel > 0)
        {
            var cost = stages.ParticlesMs + (sprites * 0.6);
            var visualLoss = particleLevel >= 3 ? 1 : particleLevel == 2 ? 2 : 3;
            var share = 1 - ((double)ParticleCounts[particleLevel - 1] / ParticleCounts[particleLevel]);
            candidates.Add(Step("Particles", ParticleLevels[particleLevel], ParticleLevels[particleLevel - 1], visualLoss, cost * share, Text($"Blossom particles cost {cost:0.0} ms (update and drawing)."), Particles, ParticleLevels[particleLevel - 1]));
        }
    }

    private static void AddSimulationSteps(List<SettingCandidate> candidates, GameSettings current, StageTimes stages)
    {
        var simulationCore = stages.EntityLogicMs + stages.PhysicsMs;
        if (stages.EntitiesSimulated > 0 && current.Is(SimulationDistanceKey, nameof(SimulationDistance.Unlimited)))
        {
            candidates.Add(Step("Simulation distance", "Unlimited", "Far", 1, simulationCore * 0.65,
                Text($"All {stages.EntitiesSimulated} creatures are simulated every frame, even far out of sight. Their AI compares every creature with every other, so simulating only the ones within range cuts the cost disproportionately."), SimulationDistanceKey, nameof(SimulationDistance.Far)));
        }
        else if (stages.EntitiesSimulated > 0 && current.Is(SimulationDistanceKey, nameof(SimulationDistance.Far)))
        {
            candidates.Add(Step("Simulation distance", "Far", "Near", 2, simulationCore * 0.6, Text($"{stages.EntitiesSimulated} creatures are still simulated. Near only animates the ones close to the camera."), SimulationDistanceKey, nameof(SimulationDistance.Near)));
        }

        var substeps = current.Number(PhysicsSubsteps);
        if (substeps > 1 && stages.PhysicsMs > 0.05)
        {
            var fewer = substeps > 4 ? 4 : Math.Max(1, substeps / 2);
            var visualLoss = fewer >= 4 ? 0 : fewer >= 2 ? 1 : 2;
            candidates.Add(Step("Physics substeps", Number(substeps), Number(fewer), visualLoss, stages.PhysicsMs * (1 - ((double)fewer / substeps)),
                Text($"Collision is solved {substeps} times per frame ({stages.PhysicsMs:0.0} ms). Slow-moving creatures do not need more than {fewer}."), PhysicsSubsteps, Number(fewer)));
        }

        var density = current.Number(EntityDensity);
        if (density > 50 && stages.EntitiesTotal > 0)
        {
            var lower = density > 75 ? 75 : 50;
            var ratio = (double)lower / density;
            candidates.Add(Step("Creature density", Text($"{density}%"), Text($"{lower}%"), lower == 75 ? 3 : 4, (simulationCore * (1 - (ratio * ratio))) + (stages.PassMs(SandboxPasses.Sprites) * 0.2),
                "Fewer creatures means quadratically less AI work, but the world looks emptier.", EntityDensity, Number(lower)));
        }
    }

    private static void AddVisibleReductions(List<SettingCandidate> candidates, GameSettings current, StageTimes stages)
    {
        var terrain = stages.PassMs(SandboxPasses.Terrain);
        var distance = current.Number(RenderDistance);
        if (distance > 700)
        {
            var shorter = distance > 1150 ? 1100 : distance > 900 ? 850 : 650;
            var visualLoss = shorter >= 1100 ? 2 : shorter >= 850 ? 3 : 4;
            var lodFactor = current.Is(TerrainLod, On) ? 0.45 : 1.0;
            candidates.Add(Step("Render distance", Number(distance), Number(shorter), visualLoss, terrain * (1 - ((double)shorter / distance)) * lodFactor,
                Text($"Terrain work grows with view distance ({terrain:0.0} ms)."), RenderDistance, Number(shorter)));
        }

        var scale = current.Number(ResolutionScale);
        if (scale > 70)
        {
            var lower = scale > 85 ? 85 : 70;
            var ratio = (double)lower / scale;
            var pixelWork = stages.PassMs(SandboxPasses.Shadows) + stages.PassMs(SandboxPasses.Water) + stages.PassMs(SandboxPasses.Sky) + stages.PassMs(SandboxPasses.Fog) + stages.PassMs(SandboxPasses.Sprites) + (terrain * 0.5);
            candidates.Add(Step("Resolution scale", Text($"{scale}%"), Text($"{lower}%"), lower == 85 ? 3 : 5, (pixelWork + stages.PassMs(SandboxPasses.AntiAliasing)) * (1 - (ratio * ratio)),
                "Pixel work scales with the square of the resolution.", ResolutionScale, Number(lower)));
        }
    }

    private static SettingCandidate Step(string setting, string from, string to, double visualLoss, double predictedMs, string hypothesis, string key, string value) =>
        new(setting, from, to, ChangeKind.Throughput, visualLoss, Math.Max(0, predictedMs), hypothesis, Change(key, value));

    private static IReadOnlyDictionary<string, string> Change(string key, string value) => new Dictionary<string, string> { [key] = value };

    private static string Number(int value) => value.ToString(CultureInfo.InvariantCulture);

    private static string Text(FormattableString message) => message.ToString(CultureInfo.InvariantCulture);
}
