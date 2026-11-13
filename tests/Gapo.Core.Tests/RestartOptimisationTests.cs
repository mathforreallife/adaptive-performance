using Gapo.Core.Diagnosis;
using Gapo.Core.Games.Unreal;
using Gapo.Core.Measurement;
using Gapo.Core.Optimisation;
using Xunit;

namespace Gapo.Core.Tests;

public sealed class RestartOptimisationTests
{
    private static readonly Dictionary<string, string> EpicEverything = new()
    {
        ["sg.ResolutionQuality"] = "100.000000",
        ["sg.ViewDistanceQuality"] = "3",
        ["sg.AntiAliasingQuality"] = "3",
        ["sg.ShadowQuality"] = "3",
        ["sg.PostProcessQuality"] = "3",
        ["sg.EffectsQuality"] = "4",
        ["sg.FoliageQuality"] = "3",
        ["FrameRateLimit"] = "0.000000",
    };

    [Fact]
    public void ACpuLimitedGameTriesViewDistanceFirstAndOneLevelAtATime()
    {
        var candidates = UnrealKnowledge.Candidates(EpicEverything, Bottleneck.CpuOneThreadLimit);

        Assert.Equal("sg.ViewDistanceQuality", candidates[0].Setting.Key);
        Assert.Equal("View distance: Epic → High", candidates[0].Title);
        Assert.Equal("3", candidates.Single(candidate => candidate.Setting.Key == "sg.EffectsQuality").To);
        Assert.DoesNotContain(candidates, candidate => candidate.Setting.Key == "sg.ResolutionQuality");
    }

    [Fact]
    public void AGraphicsLimitedGameTriesAntiAliasingBeforeResolutionBecauseResolutionLooksWorse()
    {
        var candidates = UnrealKnowledge.Candidates(EpicEverything, Bottleneck.WaitingInPresent);

        Assert.Equal("sg.AntiAliasingQuality", candidates[0].Setting.Key);
        var resolution = candidates.Single(candidate => candidate.Setting.Key == "sg.ResolutionQuality");
        Assert.Equal("85", resolution.To);
        Assert.Equal("Resolution scale: 100 % → 85 %", resolution.Title);
    }

    [Fact]
    public void SettingsAlreadyAtHighOrLowerAndSettingsTheFileDoesNotHaveAreLeftAlone()
    {
        var modest = new Dictionary<string, string> { ["sg.ShadowQuality"] = "2", ["sg.ViewDistanceQuality"] = "1", ["sg.ResolutionQuality"] = "75" };

        Assert.Empty(UnrealKnowledge.Candidates(modest, Bottleneck.CpuOneThreadLimit));
        Assert.Empty(UnrealKnowledge.Candidates(modest, Bottleneck.RenderWorkload));
    }

    [Fact]
    public void AnUncontrolledSceneNeverGetsANoiseFloorBelowFivePercent()
    {
        Assert.Equal(5.0, RestartJudgement.NoiseFloorPercent(Statistics(40.0), Statistics(40.1)));
        Assert.True(RestartJudgement.NoiseFloorPercent(Statistics(40.0), Statistics(42.0)) > 5.0);
    }

    [Fact]
    public void AChangeIsKeptOnlyWhenItBeatsTheNoiseFloor()
    {
        var kept = RestartJudgement.Judge(Statistics(42.8), Statistics(31.9), 5.0);
        var tooSmall = RestartJudgement.Judge(Statistics(42.8), Statistics(41.4), 5.0);
        var worse = RestartJudgement.Judge(Statistics(42.8), Statistics(44.0), 5.0);

        Assert.True(kept.Keep);
        Assert.Equal(25.5, kept.GainPercent, 1);
        Assert.StartsWith("Kept: frame work 42.8 → 31.9 ms", kept.Reason, StringComparison.Ordinal);
        Assert.False(tooSmall.Keep);
        Assert.StartsWith("Rolled back", tooSmall.Reason, StringComparison.Ordinal);
        Assert.False(worse.Keep);
    }

    [Fact]
    public void ANearMissIsRolledBackButIsNotCountedAsNoEffect()
    {
        var nearMiss = RestartJudgement.Judge(Statistics(29.3), Statistics(26.4), 10.0);
        var nothing = RestartJudgement.Judge(Statistics(29.3), Statistics(29.6), 10.0);

        Assert.False(nearMiss.Keep);
        Assert.False(nearMiss.ShowedNoEffect);
        Assert.False(nothing.Keep);
        Assert.True(nothing.ShowedNoEffect);
        Assert.False(RestartJudgement.Judge(Statistics(29.3), Statistics(20.0), 10.0).ShowedNoEffect);
        Assert.False(nothing.LooksDisturbed);
        Assert.True(RestartJudgement.Judge(Statistics(21.4), Statistics(28.9), 10.0).LooksDisturbed);
    }

    [Fact]
    public void ASessionWithoutFramesIsNeverKept() =>
        Assert.False(RestartJudgement.Judge(Statistics(42.8), FrameStatistics.Empty, 5.0).Keep);

    [Fact]
    public void TheTargetIsMetWithHeadroomOnly()
    {
        Assert.True(RestartJudgement.TargetMet(Statistics(15.0), 60));
        Assert.False(RestartJudgement.TargetMet(Statistics(16.5), 60));
        Assert.False(RestartJudgement.TargetMet(FrameStatistics.Empty, 60));
    }

    private static FrameStatistics Statistics(double busyMs)
    {
        var frames = Enumerable.Range(0, 200).Select(index => new MeasuredFrame(index * busyMs, busyMs, busyMs, 0, FrameMarks.None)).ToArray();
        return FrameStatistics.From(frames);
    }
}
