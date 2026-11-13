using Gapo.Core.Hardware;
using Gapo.Core.Optimisation;
using Xunit;

namespace Gapo.Core.Tests;

public sealed class OptimisationEngineTests
{
    private static readonly HardwareInventory SixtyHertzQuadCore = new()
    {
        Cpu = new CpuFacts { Cores = 2, LogicalProcessors = 4, HasSimultaneousMultithreading = true, MaximumMegahertz = 2900, LimitMegahertz = 2900 },
        Display = new DisplayFacts { Width = 1600, Height = 900, ReportedRefreshHz = 60 },
    };

    private static (OptimisationReport Report, SimulatedGame Game, OptimisationEngine Engine) Optimise(Goal goal = Goal.Balanced, Action<SimulatedGame>? configure = null)
    {
        var game = new SimulatedGame();
        configure?.Invoke(game);
        var engine = new OptimisationEngine(game, game, game, SixtyHertzQuadCore);
        return (engine.Run(goal), game, engine);
    }

    private static ExperimentRecord Experiment(OptimisationReport report, string setting) =>
        report.Experiments.First(experiment => experiment.Setting == setting);

    [Fact]
    public void ReachesTheTargetWithMeasuredChangesAndReportsAnHonestGain()
    {
        var (report, game, _) = Optimise();

        Assert.True(report.Completed);
        Assert.Null(report.Error);
        Assert.Equal(60, report.TargetFps);
        Assert.Equal("your 60 Hz display", report.TargetReason);
        Assert.InRange(report.Baseline.MedianBusyMs, 29.5, 30.5);
        Assert.True(report.Final.MedianBusyMs < 1000.0 / 60);
        Assert.True(report.FpsGainPercent > 50);
        Assert.True(report.WorkReductionFactor > 1.8);
        Assert.Equal(report.FinalSettings!.Revision, game.Settings.Revision);
        Assert.True(game.WaitedForWarmup);
        Assert.False(game.InBenchmark);
        Assert.Equal("finished", game.JournalEvents[^1]);
    }

    [Fact]
    public void RemovesHitchesBeforeAnyThroughputExperiment()
    {
        var (report, game, _) = Optimise();

        var first = report.Experiments[0];
        Assert.Equal(ChangeKind.HitchFix, first.Kind);
        Assert.Equal(Decision.Kept, first.Decision);
        Assert.Contains("Done first because random hitches would otherwise pollute every later measurement", first.Reason);
        Assert.Contains("Verified: no save freezes", first.Reason);
        Assert.Equal("300", game.Settings[SimulatedGame.Autosave]);
    }

    [Fact]
    public void TriesTheFreeChangeBeforeChangesThatCostVisualQuality()
    {
        var (report, _, _) = Optimise();

        var throughput = report.Experiments.Where(experiment => experiment.Kind == ChangeKind.Throughput).ToList();
        Assert.Equal("Render threads", throughput[0].Setting);
        Assert.Equal(Decision.Kept, throughput[0].Decision);
        Assert.Contains("with no visual cost", throughput[0].Reason);
    }

    [Fact]
    public void RollsBackAChangeWhoseGainIsInsideTheNoiseFloor()
    {
        var (report, game, _) = Optimise(Goal.MaximumFps);

        var placebo = Experiment(report, "Placebo effect");
        Assert.Equal(Decision.RolledBack, placebo.Decision);
        Assert.Contains("measurement noise, so there is no proven benefit, so not worth any visual cost.", placebo.Reason);
        Assert.Equal("On", game.Settings[SimulatedGame.Placebo]);
        Assert.Contains("rollback", game.AppliedNotes);
    }

    [Fact]
    public void StopsLoweringVisualsOnceTheTargetIsMetAndSaysWhatItLeftAlone()
    {
        var (report, game, _) = Optimise(configure: simulated => simulated.RenderMs = 16);

        Assert.Contains(report.NotTouched, note => note.StartsWith("Stopped lowering visuals: the 60 FPS target is met with headroom", StringComparison.Ordinal));
        Assert.Equal("Ultra", game.Settings[SimulatedGame.Shadows]);
        Assert.DoesNotContain(report.Experiments, experiment => experiment.Setting == "Shadows");
    }

    [Fact]
    public void MaximumFpsKeepsGoingPastTheTargetAndNeverCaps()
    {
        var (report, game, _) = Optimise(Goal.MaximumFps);

        Assert.Equal(144, report.TargetFps);
        Assert.Contains(report.Experiments, experiment => experiment.Setting == "Shadows" && experiment.Decision == Decision.Kept);
        Assert.Equal("0", game.Settings[SimulatedGame.Cap]);
        Assert.Equal("No frame cap (Maximum FPS mode).", report.PacingNote);
    }

    [Fact]
    public void QualityModeRefusesAVeryVisibleChangeWhenTheGainDoesNotJustifyIt()
    {
        var (report, _, _) = Optimise(Goal.Quality, simulated => simulated.RenderMs = 60);

        var notTried = report.NotTouched.Any(note => note.StartsWith("Quality mode: not trying Shadows: High → Off", StringComparison.Ordinal));
        var rolledBack = report.Experiments.Any(experiment => experiment.Title == "Shadows: High → Off" && experiment.Decision == Decision.RolledBack);
        Assert.True(notTried || rolledBack);
    }

    [Fact]
    public void CapsToTheDisplayWhenFramesFitComfortablyAndPacingDoesNotGetWorse()
    {
        var (report, game, _) = Optimise(configure: simulated => simulated.RenderMs = 14);

        var cap = Experiment(report, OptimisationEngine.FrameCapSetting);
        Assert.Equal(Decision.Kept, cap.Decision);
        Assert.Equal("60", game.Settings[SimulatedGame.Cap]);
        Assert.Equal("Locked to 60 FPS to match your 60 Hz display.", report.PacingNote);
        Assert.InRange(report.Final.AverageFps, 58, 61);
    }

    [Fact]
    public void KeepsTheCapWhenItsVariationIsTooSmallToSee()
    {
        var (report, game, _) = Optimise(configure: simulated =>
        {
            simulated.RenderMs = 14;
            simulated.CapJitterMs = 1;
        });

        Assert.Equal(Decision.Kept, Experiment(report, OptimisationEngine.FrameCapSetting).Decision);
        Assert.Equal("60", game.Settings[SimulatedGame.Cap]);
    }

    [Fact]
    public void RollsTheCapBackWhenItMakesPacingWorse()
    {
        var (report, game, _) = Optimise(configure: simulated =>
        {
            simulated.RenderMs = 14;
            simulated.CapJitterMs = 4;
        });

        Assert.Equal(Decision.RolledBack, Experiment(report, OptimisationEngine.FrameCapSetting).Decision);
        Assert.Equal("0", game.Settings[SimulatedGame.Cap]);
        Assert.StartsWith("Left uncapped", report.PacingNote);
    }

    [Fact]
    public void DoesNotCapWhenTheRefreshRateIsNotSustainable()
    {
        var (report, _, _) = Optimise(configure: simulated => simulated.SimulationMs = 15);

        Assert.StartsWith("Not capped: 60 FPS is not sustainable yet", report.PacingNote);
        Assert.DoesNotContain(report.Experiments, experiment => experiment.Kind == ChangeKind.Pacing);
    }

    [Fact]
    public void GivesQualityBackWhenLaterChangesLeftPlentyOfHeadroom()
    {
        var (report, game, _) = Optimise(configure: simulated =>
        {
            simulated.SimulationMs = 5;
            simulated.RenderMs = 10;
            simulated.ThreadsPredictionFactor = 0.15;
            simulated.OffersOnlyThreadsAndAntiAliasing = true;
        });

        var restore = Assert.Single(report.Experiments, experiment => experiment.Kind == ChangeKind.QualityRestore);
        Assert.Equal("Restore quality: Anti-aliasing", restore.Title);
        Assert.Equal(Decision.Kept, restore.Decision);
        Assert.Contains("quality restored and the target is still met", restore.Reason);
        Assert.Equal("SSAA", game.Settings[SimulatedGame.AntiAliasing]);
        Assert.Equal("2", game.Settings[SimulatedGame.Threads]);
        Assert.True(report.Final.MedianBusyMs <= 1000.0 / 60 * 0.8);
    }

    [Fact]
    public void DoesNotEvenTryAGiveBackItPredictsWouldBreakTheBudget()
    {
        var (report, game, _) = Optimise(configure: simulated =>
        {
            simulated.SimulationMs = 2;
            simulated.RenderMs = 26;
        });

        Assert.DoesNotContain(report.Experiments, experiment => experiment.Kind == ChangeKind.QualityRestore);
        Assert.Contains(report.NotTouched, note => note.StartsWith("Quality give-back: restoring Anti-aliasing would cost about", StringComparison.Ordinal));
        Assert.Equal("FXAA", game.Settings[SimulatedGame.AntiAliasing]);
    }

    [Fact]
    public void AFailedMeasurementIsRolledBackReportedAsFailedAndTheRunContinues()
    {
        var (report, game, _) = Optimise(configure: simulated => simulated.FailMeasurementNumber = 3);

        var failed = Assert.Single(report.Experiments, experiment => experiment.Decision == Decision.Failed);
        Assert.Contains("could not be completed (Too few frames were measured (3).)", failed.Reason);
        Assert.Equal("1", game.Settings[SimulatedGame.Threads]);
        Assert.True(report.Completed);
        Assert.Contains(report.Experiments, experiment => experiment.Decision == Decision.Kept && experiment.Kind == ChangeKind.Throughput);
    }

    [Fact]
    public void WhenTheGameDisappearsTheRunStopsAndTheJournalStaysOpenForRecovery()
    {
        var (report, game, _) = Optimise(configure: simulated => simulated.GameDisappearsAtMeasurement = 3);

        Assert.False(report.Completed);
        Assert.Equal("The game was closed.", report.Error);
        Assert.StartsWith("trial ", game.JournalEvents[^1]);
        Assert.DoesNotContain("finished", game.JournalEvents);
    }

    [Fact]
    public void ADisturbedBaselineIsMeasuredAgainInsteadOfWideningTheTolerance()
    {
        var (report, game, _) = Optimise(configure: simulated => simulated.DisturbedBaselineWindows = 2);

        Assert.True(report.Noise!.CanBeTrusted);
        Assert.InRange(report.Baseline.MedianBusyMs, 29.5, 30.5);
        Assert.True(game.WindowsMeasured >= 4);
    }

    [Fact]
    public void StopsWhenSeveralChangesInARowMakeNoMeasurableDifference()
    {
        var (report, game, _) = Optimise(Goal.MaximumFps, simulated => simulated.WorkNeverGoesBelowMs = 29.9);

        Assert.True(report.Completed);
        Assert.Contains(report.NotTouched, note => note.StartsWith("Stopped testing: the last 3 changes made no measurable difference", StringComparison.Ordinal));
        Assert.Equal(3, report.Experiments.Count(experiment => experiment.Kind == ChangeKind.Throughput));
        Assert.All(report.Experiments.Where(experiment => experiment.Kind == ChangeKind.Throughput), experiment => Assert.Equal(Decision.RolledBack, experiment.Decision));
        Assert.Equal("1", game.Settings[SimulatedGame.Threads]);
    }

    [Fact]
    public void StopsLoweringVisualsOnceTheGameRunsAtTheDisplayRate()
    {
        var (report, game, _) = Optimise(configure: simulated => simulated.WorkNeverGoesBelowMs = 16.6);

        Assert.True(report.Completed);
        Assert.Contains(report.NotTouched, note => note.StartsWith("Stopped lowering visuals: the game already runs at your display's 60 Hz", StringComparison.Ordinal));
        Assert.Equal("Ultra", game.Settings[SimulatedGame.Shadows]);
        Assert.StartsWith("No cap needed: the display already paces the game", report.PacingNote);
        Assert.DoesNotContain(report.Experiments, experiment => experiment.Kind == ChangeKind.Pacing);
        Assert.InRange(report.Final.AverageFps, 58, 61);
    }

    [Fact]
    public void RevertAllPutsTheOriginalSettingsBack()
    {
        var (report, game, engine) = Optimise();
        Assert.False(report.OriginalSettings!.HasSameValuesAs(game.Settings));

        engine.RevertAll();

        Assert.True(report.OriginalSettings.HasSameValuesAs(game.Settings));
        Assert.Equal("reverted to original", game.AppliedNotes[^1]);
    }

    [Fact]
    public void EveryExperimentCarriesItsHypothesisMeasurementAndReason()
    {
        var (report, _, _) = Optimise(Goal.MaximumFps);

        Assert.All(report.Experiments, experiment =>
        {
            Assert.False(string.IsNullOrWhiteSpace(experiment.Hypothesis));
            Assert.False(string.IsNullOrWhiteSpace(experiment.Reason));
            Assert.True(experiment.Kind == ChangeKind.HitchFix || experiment.After is not null);
        });
        Assert.Equal(report.Experiments.Count, report.Kept + report.RolledBack + report.Experiments.Count(experiment => experiment.Decision == Decision.Failed));
        Assert.True(report.MeasurementWindows >= 4 + report.Experiments.Count(experiment => experiment.Kind != ChangeKind.HitchFix));
    }
}
