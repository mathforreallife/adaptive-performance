using Gapo.Core.Diagnosis;
using Gapo.Core.Statistics;
using Gapo.Sandbox.Contract;

namespace Gapo.Agent.Measurement;

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
    private int renderThreads;

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
        renderThreads = frame.RenderThreads;
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
                    new RenderPass("shadows", shadows.Median()),
                    new RenderPass("terrain", terrain.Median()),
                    new RenderPass("water", water.Median()),
                    new RenderPass("sky and clouds", sky.Median()),
                    new RenderPass("fog", fog.Median()),
                    new RenderPass("creatures and particles", sprites.Median()),
                    new RenderPass("anti-aliasing", antiAliasing.Median()),
                ]);
}
