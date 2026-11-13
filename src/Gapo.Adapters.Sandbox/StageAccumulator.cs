using Gapo.Core.Diagnosis;
using Gapo.Core.Statistics;
using Gapo.Sandbox.Contract;

namespace Gapo.Adapters.Sandbox;

public sealed class StageAccumulator
{
    private const int FramesPerAttempt = 4096;

    private readonly SeriesSummary simulation = new(FramesPerAttempt);
    private readonly SeriesSummary render = new(FramesPerAttempt);
    private readonly SeriesSummary present = new(FramesPerAttempt);
    private readonly SeriesSummary streaming = new(FramesPerAttempt);
    private readonly SeriesSummary physics = new(FramesPerAttempt);
    private readonly SeriesSummary entities = new(FramesPerAttempt);
    private readonly SeriesSummary terrain = new(FramesPerAttempt);
    private readonly SeriesSummary shadows = new(FramesPerAttempt);
    private readonly SeriesSummary water = new(FramesPerAttempt);
    private readonly SeriesSummary sky = new(FramesPerAttempt);
    private readonly SeriesSummary fog = new(FramesPerAttempt);
    private readonly SeriesSummary sprites = new(FramesPerAttempt);
    private readonly SeriesSummary antiAliasing = new(FramesPerAttempt);
    private readonly SeriesSummary entityLogic = new(FramesPerAttempt);
    private readonly SeriesSummary particles = new(FramesPerAttempt);
    private readonly SeriesSummary entitiesTotal = new(FramesPerAttempt);
    private int renderThreads;
    private int renderWidth;
    private int renderHeight;

    public void Add(in FrameRecord frame)
    {
        simulation.Add(frame.SimMs);
        render.Add(frame.RenderMs);
        present.Add(frame.PresentMs);
        streaming.Add(frame.IoMs);
        physics.Add(frame.SimPhysicsMs);
        entities.Add(frame.EntitiesSimulated);
        terrain.Add(frame.TerrainMs);
        shadows.Add(frame.ShadowMs);
        water.Add(frame.WaterMs);
        sky.Add(frame.SkyMs);
        fog.Add(frame.FogMs);
        sprites.Add(frame.SpritesMs);
        antiAliasing.Add(frame.PostMs);
        entityLogic.Add(frame.SimEntitiesMs);
        particles.Add(frame.SimParticlesMs);
        entitiesTotal.Add(frame.EntitiesTotal);
        renderThreads = frame.RenderThreads;
        renderWidth = frame.RenderWidth;
        renderHeight = frame.RenderHeight;
    }

    public StageTimes? ToStageTimes() =>
        simulation.Count == 0
            ? null
            : new StageTimes(
                simulation.Median(),
                render.Median(),
                present.Median(),
                streaming.Median(),
                renderThreads,
                (int)entities.Median(),
                physics.Median(),
                [
                    new RenderPass(SandboxPasses.Shadows, shadows.Median()),
                    new RenderPass(SandboxPasses.Terrain, terrain.Median()),
                    new RenderPass(SandboxPasses.Water, water.Median()),
                    new RenderPass(SandboxPasses.Sky, sky.Median()),
                    new RenderPass(SandboxPasses.Fog, fog.Median()),
                    new RenderPass(SandboxPasses.Sprites, sprites.Median()),
                    new RenderPass(SandboxPasses.AntiAliasing, antiAliasing.Median()),
                ])
            {
                EntityLogicMs = entityLogic.Median(),
                ParticlesMs = particles.Median(),
                EntitiesTotal = (int)entitiesTotal.Median(),
                RenderWidth = renderWidth,
                RenderHeight = renderHeight,
            };
}

public static class SandboxPasses
{
    public const string Shadows = "shadows";
    public const string Terrain = "terrain";
    public const string Water = "water";
    public const string Sky = "sky and clouds";
    public const string Fog = "fog";
    public const string Sprites = "creatures and particles";
    public const string AntiAliasing = "anti-aliasing";
}
