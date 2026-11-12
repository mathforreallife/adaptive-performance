using Gapo.Core.Diagnosis;
using Gapo.Core.Hardware;
using Gapo.Core.Measurement;
using Gapo.Core.Optimisation;
using Gapo.Sandbox.Contract;
using Xunit;

namespace Gapo.Adapters.Sandbox.Tests;

public sealed class SandboxKnowledgeTests
{
    private const int Precision = 6;

    private static readonly CpuFacts DualCoreWithTwoThreadsEach = new() { Cores = 2, LogicalProcessors = 4, HasSimultaneousMultithreading = true };
    private static readonly SandboxKnowledge Knowledge = new();

    private static GameSettings Defaults(Action<SandboxSettings>? change = null)
    {
        var settings = SandboxSettings.UnoptimisedDefaults();
        change?.Invoke(settings);
        return SandboxSettingsMap.ToGameSettings(settings, revision: 0);
    }

    private static WindowResult Reference(double render = 28, double physics = 6, double entityLogic = 2.5) =>
        new(
            FrameStatistics.Empty with { Count = 100, MedianBusyMs = render + physics + entityLogic + 3 },
            new StageTimes(physics + entityLogic + 0.4, render, 3, 0, RenderThreads: 1, EntitiesSimulated: 700, physics,
                [
                    new RenderPass(SandboxPasses.Terrain, 8),
                    new RenderPass(SandboxPasses.Shadows, 7),
                    new RenderPass(SandboxPasses.Water, 2),
                    new RenderPass(SandboxPasses.Sky, 4),
                    new RenderPass(SandboxPasses.Fog, 4),
                    new RenderPass(SandboxPasses.Sprites, 1),
                    new RenderPass(SandboxPasses.AntiAliasing, 2),
                ])
            {
                EntityLogicMs = entityLogic,
                ParticlesMs = 0.4,
                EntitiesTotal = 700,
                RenderWidth = 224,
                RenderHeight = 126,
            },
            []);

    private static SettingCandidate Candidate(GameSettings settings, string setting, CpuFacts? processor = null, bool memoryPressure = false) =>
        Knowledge.Candidates(settings, Reference(), processor ?? DualCoreWithTwoThreadsEach, memoryPressure).Single(candidate => candidate.Setting == setting);

    [Fact]
    public void SettingsSurviveTheRoundTripThroughTheGenericForm()
    {
        var original = SandboxSettings.UnoptimisedDefaults();
        original.Shadows = ShadowQuality.High;
        original.TerrainLod = true;
        original.RenderThreads = 2;
        original.FrameRateCap = 60;

        var generic = SandboxSettingsMap.ToGameSettings(original, revision: 7);
        var back = SandboxSettingsMap.ToSandboxSettings(generic, "GAPO (test)");

        Assert.Equal(7, back.Revision);
        Assert.Equal("GAPO (test)", back.ModifiedBy);
        back.ModifiedBy = original.ModifiedBy;
        back.Revision = original.Revision;
        Assert.Equal(original.ToIni(), back.ToIni());
    }

    [Fact]
    public void OffersOneStepPerSettingFromTheUnoptimisedDefaults()
    {
        var candidates = Knowledge.Candidates(Defaults(), Reference(), DualCoreWithTwoThreadsEach, memoryIsUnderPressure: false);

        Assert.Equal(candidates.Count, candidates.Select(candidate => candidate.Setting).Distinct().Count());
        Assert.Contains(candidates, candidate => candidate.Title == "Render threads: 1 → 2");
        Assert.Contains(candidates, candidate => candidate.Title == "Anti-aliasing: 4× SSAA → FXAA");
        Assert.Contains(candidates, candidate => candidate.Title == "Shadows: Ultra → High");
        Assert.Contains(candidates, candidate => candidate.Title == "Physics substeps: 8 → 4");
        Assert.DoesNotContain(candidates, candidate => candidate.Setting == "Textures");
        Assert.All(candidates, candidate => Assert.True(candidate.PredictedSavingMs >= 0));
    }

    [Fact]
    public void RenderThreadsGoToThePhysicalCoreCountFirstAndThenTestTheExtraThreads()
    {
        var toCores = Candidate(Defaults(), "Render threads");
        var toThreads = Candidate(Defaults(settings => settings.RenderThreads = 2), "Render threads");

        Assert.Equal("2", toCores.To);
        Assert.Equal(28 * (1 - (1 / (2 * 0.85))), toCores.PredictedSavingMs, Precision);
        Assert.Equal(0, toCores.VisualLoss);
        Assert.Equal("4", toThreads.To);
        Assert.Equal(28 * 0.12, toThreads.PredictedSavingMs, Precision);
        Assert.Contains("has to be measured, not assumed", toThreads.Hypothesis);
    }

    [Fact]
    public void ASingleProcessorMachineIsNotOfferedMoreRenderThreads()
    {
        var oneProcessor = new CpuFacts { Cores = 1, LogicalProcessors = 1 };

        var candidates = Knowledge.Candidates(Defaults(), Reference(), oneProcessor, memoryIsUnderPressure: false);

        Assert.DoesNotContain(candidates, candidate => candidate.Setting == "Render threads");
    }

    [Fact]
    public void FreeAndNearlyFreeChangesRankAboveVisibleOnes()
    {
        var ranked = Knowledge.Candidates(Defaults(), Reference(), DualCoreWithTwoThreadsEach, memoryIsUnderPressure: false)
            .OrderByDescending(candidate => candidate.Value)
            .Select(candidate => candidate.Setting)
            .ToList();

        Assert.Equal("Render threads", ranked[0]);
        Assert.True(ranked.IndexOf("Shadows") < ranked.IndexOf("Resolution scale"));
        Assert.True(ranked.IndexOf("Physics substeps") < ranked.IndexOf("Creature density"));
    }

    [Fact]
    public void AntiAliasingPredictionCoversEveryPixelPass()
    {
        var candidate = Candidate(Defaults(), "Anti-aliasing");

        Assert.Equal((8 * 0.5) + ((7 + 2 + 4 + 4 + 1) * 0.75) + (2 * 0.4), candidate.PredictedSavingMs, Precision);
        Assert.Contains("448×252", candidate.Hypothesis);
        Assert.Equal("FXAA", candidate.Changes[SandboxSettingsMap.AntiAliasingKey]);
    }

    [Fact]
    public void LaddersContinueOneStepAtATime()
    {
        Assert.Equal("Shadows: High → Off", Candidate(Defaults(settings => settings.Shadows = ShadowQuality.High), "Shadows").Title);
        Assert.Equal(5, Candidate(Defaults(settings => settings.Shadows = ShadowQuality.High), "Shadows").VisualLoss);
        Assert.Equal("Physics substeps: 4 → 2", Candidate(Defaults(settings => settings.PhysicsSubsteps = 4), "Physics substeps").Title);
        Assert.Equal("Particles: Ultra → High", Candidate(Defaults(), "Particles").Title);
        Assert.Equal("Render distance: 1400 → 1100", Candidate(Defaults(), "Render distance").Title);
        Assert.Equal("Resolution scale: 100% → 85%", Candidate(Defaults(), "Resolution scale").Title);
    }

    [Fact]
    public void SettingsAtTheBottomOfTheirLadderAreNotOffered()
    {
        var lowest = Defaults(settings =>
        {
            settings.Shadows = ShadowQuality.Off;
            settings.PhysicsSubsteps = 1;
            settings.Particles = ParticleQuality.Off;
            settings.ResolutionScale = 70;
            settings.TerrainLod = true;
        });

        var settings = Knowledge.Candidates(lowest, Reference(), DualCoreWithTwoThreadsEach, memoryIsUnderPressure: false).Select(candidate => candidate.Setting).ToList();

        Assert.DoesNotContain("Shadows", settings);
        Assert.DoesNotContain("Physics substeps", settings);
        Assert.DoesNotContain("Particles", settings);
        Assert.DoesNotContain("Resolution scale", settings);
        Assert.DoesNotContain("Terrain LOD", settings);
    }

    [Fact]
    public void TexturesAreOnlyTouchedUnderMemoryPressure()
    {
        var relief = Candidate(Defaults(), "Textures", memoryPressure: true);

        Assert.Equal(ChangeKind.MemoryRelief, relief.Kind);
        Assert.Contains(Knowledge.NotWorthTouching(Defaults(), Reference(), memoryIsUnderPressure: false), note => note.StartsWith("Texture quality", StringComparison.Ordinal));
        Assert.DoesNotContain(Knowledge.NotWorthTouching(Defaults(), Reference(), memoryIsUnderPressure: true), note => note.StartsWith("Texture quality", StringComparison.Ordinal));
    }

    [Fact]
    public void AutosaveFreezesGetAHitchFixThatLengthensTheInterval()
    {
        var hitches = FrameStatistics.Empty with { StorageHitches = 2, LongestStorageHitchMs = 85 };

        var fix = Knowledge.HitchFix(Defaults(), hitches);

        Assert.NotNull(fix);
        Assert.Equal("Autosave interval: 10 s → 5 min", fix.Title);
        Assert.Equal("300", fix.Changes[SandboxSettingsMap.AutosaveSeconds]);
        Assert.Contains("freezing for up to 85 ms", fix.Hypothesis);
        Assert.Null(Knowledge.HitchFix(Defaults(), FrameStatistics.Empty));
        Assert.Null(Knowledge.HitchFix(Defaults(settings => settings.AutosaveSeconds = 300), hitches));
    }

    [Fact]
    public void FrameCapCandidateSetsTheCap()
    {
        var cap = Knowledge.FrameCap(Defaults(), 60, "why");

        Assert.Equal("Frame-rate cap: Unlimited → 60 FPS", cap.Title);
        Assert.Equal(ChangeKind.Pacing, cap.Kind);
        Assert.Equal("60", cap.Changes[SandboxSettingsMap.FrameRateCap]);
    }

    [Fact]
    public void AppliedCandidateProducesValidGameSettings()
    {
        var settings = Defaults();
        foreach (var candidate in Knowledge.Candidates(settings, Reference(), DualCoreWithTwoThreadsEach, memoryIsUnderPressure: true))
        {
            var changed = SandboxSettingsMap.ToSandboxSettings(settings.With(candidate.Changes), "test");
            var reparsed = SandboxSettingsMap.ToGameSettings(changed, 0);
            Assert.All(candidate.Changes, change => Assert.Equal(change.Value, reparsed[change.Key]));
        }
    }
}
