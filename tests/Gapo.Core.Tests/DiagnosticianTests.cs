using Gapo.Core.Diagnosis;
using Gapo.Core.Measurement;
using Xunit;

namespace Gapo.Core.Tests;

public sealed class DiagnosticianTests
{
    private const long Megabyte = 1024 * 1024;

    private static FrameStatistics Frames(double busyMs, double medianFrameMs = 0, double percentile99Ms = 0, int storageHitches = 0, double longestHitchMs = 0) =>
        FrameStatistics.Empty with
        {
            Count = 120,
            DurationMs = 15_000,
            MedianBusyMs = busyMs,
            MedianFrameMs = medianFrameMs == 0 ? busyMs : medianFrameMs,
            Percentile99FrameMs = percentile99Ms == 0 ? busyMs * 1.2 : percentile99Ms,
            StorageHitches = storageHitches,
            LongestStorageHitchMs = longestHitchMs,
        };

    private static MonitorSummary Monitor(
        double[]? loads = null,
        double gameProcessors = 1.0,
        double? busiestThread = 97,
        double background = 5,
        int memoryLoad = 45,
        long lowestAvailableMegabytes = 8000)
    {
        loads ??= [52, 53];
        return new MonitorSummary
        {
            LoadPerProcessor = loads,
            AverageLoadPercent = loads.Average(),
            BusiestProcessorPercent = loads.Max(),
            GameProcessorsUsed = gameProcessors,
            BusiestGameThreadPercent = busiestThread,
            BackgroundPercent = background,
            MemoryLoadPercent = memoryLoad,
            LowestAvailableMemoryBytes = lowestAvailableMegabytes * Megabyte,
        };
    }

    private static StageTimes Stages(double simulation, double render, int renderThreads = 1, double present = 1, double streaming = 0) =>
        new(simulation, render, present, streaming, renderThreads, EntitiesSimulated: 700, PhysicsMs: simulation * 0.6,
            [new RenderPass("shadows", render * 0.4), new RenderPass("terrain", render * 0.3), new RenderPass("fog", render * 0.2), new RenderPass("water", render * 0.1)]);

    private static DiagnosisInput Input(
        double busyMs = 25,
        StageTimes? stages = null,
        MonitorSummary? monitor = null,
        int logicalProcessors = 2,
        PresentTiming? presents = null,
        FrameStatistics? frames = null,
        FrameStatistics? hitches = null,
        double warmupShare = 0,
        int limitMegahertz = 2900) =>
        new()
        {
            Frames = frames ?? Frames(busyMs),
            TargetFps = 60,
            Monitor = monitor ?? Monitor(),
            Processor = new ProcessorLimits(logicalProcessors, MaximumMegahertz: 2900, limitMegahertz),
            Stages = stages,
            Presents = presents,
            HitchObservation = hitches,
            WarmupShareOfFrames = warmupShare,
        };

    [Fact]
    public void RenderingOnOneThreadIsNamedEvenThoughNoProcessorLooksBusy()
    {
        var diagnosis = Diagnostician.Diagnose(Input(busyMs: 25, stages: Stages(simulation: 6, render: 18)));

        Assert.Equal(Bottleneck.RenderSingleThread, diagnosis.Primary);
        Assert.Equal(ProblemCategory.GameSetting, diagnosis.Category);
        Assert.Contains("18.0 ms of each 25.0 ms frame, all on 1 of your 2 CPU threads", diagnosis.Summary);
        Assert.Contains(diagnosis.Evidence, sentence => sentence.Contains("busiest thread uses 97% of one processor"));
        Assert.DoesNotContain(diagnosis.Evidence, sentence => sentence.Contains("hides a one-thread limit"));
    }

    [Fact]
    public void IdleOtherProcessorsAreOnlyMentionedWhenTheyAreIdle()
    {
        var idleOthers = Diagnostician.Diagnose(Input(stages: Stages(6, 18), monitor: Monitor(loads: [99, 12, 17, 22]), logicalProcessors: 4));
        var busyOthers = Diagnostician.Diagnose(Input(stages: Stages(6, 18), monitor: Monitor(loads: [99, 60, 70, 65], background: 8), logicalProcessors: 4));

        Assert.Contains("mostly idle (17% on average)", idleOthers.Summary);
        Assert.DoesNotContain("idle", busyOthers.Summary);
        Assert.DoesNotContain("background", busyOthers.Summary);
    }

    [Fact]
    public void BusyOtherProcessorsAreBlamedOnBackgroundAppsOnlyWithEvidence()
    {
        var diagnosis = Diagnostician.Diagnose(Input(stages: Stages(6, 18), monitor: Monitor(loads: [99, 60, 70, 65], background: 31), logicalProcessors: 4));

        Assert.Contains("busy with background apps (31% of the CPU)", diagnosis.Summary);
        Assert.Contains(diagnosis.SecondaryProblems, problem => problem.StartsWith("Background apps use 31%", StringComparison.Ordinal));
    }

    [Fact]
    public void HiddenOneThreadLimitIsSpelledOutWhenTheGapIsLarge()
    {
        var diagnosis = Diagnostician.Diagnose(Input(stages: Stages(6, 18), monitor: Monitor(loads: [99, 94, 17, 12]), logicalProcessors: 4));

        Assert.Contains(diagnosis.Evidence, sentence => sentence.StartsWith("Total CPU use is 56%, but the busiest processor runs at 99%", StringComparison.Ordinal));
    }

    [Fact]
    public void MultithreadedRenderingIsAWorkloadProblemAndNamesTheCostliestPasses()
    {
        var diagnosis = Diagnostician.Diagnose(Input(busyMs: 22, stages: Stages(simulation: 5, render: 15, renderThreads: 4), logicalProcessors: 4));

        Assert.Equal(Bottleneck.RenderWorkload, diagnosis.Primary);
        Assert.Contains("Most expensive: shadows 6.0 ms, terrain 4.5 ms, fog 3.0 ms.", diagnosis.Summary);
    }

    [Fact]
    public void SimulationHeavierThanRenderingIsAnEngineLimitationWithItsMitigation()
    {
        var diagnosis = Diagnostician.Diagnose(Input(busyMs: 40, stages: Stages(simulation: 30, render: 8)));

        Assert.Equal(Bottleneck.CpuMainThread, diagnosis.Primary);
        Assert.Equal(ProblemCategory.EngineLimitation, diagnosis.Category);
        Assert.Equal(Diagnostician.MainThreadLimitation, diagnosis.EngineLimitation);
        Assert.Contains("700 creatures simulated", diagnosis.Summary);
    }

    [Fact]
    public void StageSharesAddUpToTheFrameWork()
    {
        var diagnosis = Diagnostician.Diagnose(Input(busyMs: 25, stages: Stages(simulation: 6, render: 16, present: 1.5, streaming: 0.5)));

        Assert.Equal(1.0, diagnosis.Stages.Sum(stage => stage.ShareOfFrameWork), 6);
        Assert.Contains(diagnosis.Stages, stage => stage.Name == "Streaming and saving");
        Assert.Equal(1.0, diagnosis.Stages.Single(stage => stage.Name == "Other").Milliseconds, 6);
    }

    [Fact]
    public void BudgetAndRequiredSavingComeFromTheTarget()
    {
        var diagnosis = Diagnostician.Diagnose(Input(busyMs: 25, stages: Stages(6, 18)));

        Assert.Equal(1000.0 / 60, diagnosis.BudgetMs, 6);
        Assert.Equal(25 - (1000.0 / 60), diagnosis.RequiredSavingMs, 6);
        Assert.Contains("Frame work 25.0 ms against a 16.7 ms budget for 60 FPS.", diagnosis.Evidence);
    }

    [Fact]
    public void WorkComfortablyInsideTheBudgetMeansTheTargetIsMet()
    {
        var diagnosis = Diagnostician.Diagnose(Input(busyMs: 12, stages: Stages(4, 7)));

        Assert.True(diagnosis.TargetIsMet);
        Assert.Equal("Target reached", diagnosis.Title);
        Assert.Equal(0.0, diagnosis.RequiredSavingMs, 6);
    }

    [Fact]
    public void TargetIsNotCalledMetWhileSaveFreezesRemain()
    {
        var hitches = Frames(12, storageHitches: 2, longestHitchMs: 77);

        var diagnosis = Diagnostician.Diagnose(Input(busyMs: 12, stages: Stages(4, 7), hitches: hitches));

        Assert.False(diagnosis.TargetIsMet);
        Assert.Contains(diagnosis.SecondaryProblems, problem => problem.Contains("froze for up to 77 ms while saving, 2 time(s) in the last 15 s"));
    }

    [Fact]
    public void WarmupBlocksAnyJudgement()
    {
        var diagnosis = Diagnostician.Diagnose(Input(stages: Stages(6, 18), warmupShare: 0.4));

        Assert.Equal(Bottleneck.Transient, diagnosis.Primary);
        Assert.Equal(ProblemCategory.Transient, diagnosis.Category);
        Assert.Empty(diagnosis.Evidence);
    }

    [Fact]
    public void MemoryPressureNeedsHighLoadOrLittleFreeMemory()
    {
        var calm = Diagnostician.Diagnose(Input(stages: Stages(6, 18)));
        var loaded = Diagnostician.Diagnose(Input(stages: Stages(6, 18), monitor: Monitor(memoryLoad: 93)));
        var nearlyFull = Diagnostician.Diagnose(Input(stages: Stages(6, 18), monitor: Monitor(memoryLoad: 80, lowestAvailableMegabytes: 400)));

        Assert.DoesNotContain(calm.SecondaryProblems, problem => problem.StartsWith("Memory pressure", StringComparison.Ordinal));
        Assert.Contains("Memory pressure: 93% of RAM is in use.", loaded.SecondaryProblems);
        Assert.Contains("Memory pressure: 80% of RAM is in use.", nearlyFull.SecondaryProblems);
    }

    [Fact]
    public void UnevenPacingIsReportedOnlyWithoutSaveFreezesToBlame()
    {
        var uneven = Frames(25, medianFrameMs: 25, percentile99Ms: 70);
        var withFreezes = uneven with { StorageHitches = 1, LongestStorageHitchMs = 70 };

        Assert.Contains(Diagnostician.Diagnose(Input(stages: Stages(6, 18), frames: uneven)).SecondaryProblems, problem => problem.StartsWith("Uneven frame pacing", StringComparison.Ordinal));
        Assert.DoesNotContain(Diagnostician.Diagnose(Input(stages: Stages(6, 18), frames: withFreezes)).SecondaryProblems, problem => problem.StartsWith("Uneven frame pacing", StringComparison.Ordinal));
    }

    [Fact]
    public void LimitedProcessorClockIsReported()
    {
        var diagnosis = Diagnostician.Diagnose(Input(stages: Stages(6, 18), limitMegahertz: 2000));

        Assert.Contains("Windows reports the CPU clock limited to 2000 of 2900 MHz (a power or thermal limit).", diagnosis.SecondaryProblems);
    }

    [Fact]
    public void WithoutAGameHookOneBusyThreadIsStillRecognised()
    {
        var diagnosis = Diagnostician.Diagnose(Input(busyMs: 40, monitor: Monitor(loads: [40, 35, 30, 38], gameProcessors: 1.05, busiestThread: 98), logicalProcessors: 4,
            presents: new PresentTiming(40.2, 40.0, 0.2)));

        Assert.Equal(Bottleneck.CpuOneThreadLimit, diagnosis.Primary);
        Assert.Contains("98% of one processor", diagnosis.Summary);
        Assert.Contains(diagnosis.Stages, stage => stage.Name == "CPU work");
    }

    [Fact]
    public void AGraphicsCardBusyForMostOfEachFrameIsTheLimitAndTheTargetIsNotMet()
    {
        var diagnosis = Diagnostician.Diagnose(Input(busyMs: 8, monitor: Monitor(gameProcessors: 0.5, busiestThread: 40), logicalProcessors: 4,
            frames: Frames(8, medianFrameMs: 33), presents: new PresentTiming(33, 8, 24, GpuBusyMs: 31)));

        Assert.Equal(Bottleneck.GraphicsCard, diagnosis.Primary);
        Assert.Contains("busy 31.0 ms of each 33.0 ms frame, while the game's busiest processor thread is busy only 40% of the time", diagnosis.Summary, StringComparison.Ordinal);
        Assert.Contains(diagnosis.Stages, stage => stage.Name == "Graphics card busy");
    }

    [Fact]
    public void AFairlyBusyGraphicsCardDoesNotHideAPeggedProcessorThread()
    {
        var diagnosis = Diagnostician.Diagnose(Input(busyMs: 40, monitor: Monitor(loads: [40, 35, 30, 38], gameProcessors: 1.05, busiestThread: 98), logicalProcessors: 4,
            presents: new PresentTiming(40.2, 40.0, 0.2, GpuBusyMs: 34.0)));

        Assert.Equal(Bottleneck.CpuOneThreadLimit, diagnosis.Primary);
    }

    [Fact]
    public void ASaturatedGraphicsCardIsTheLimitEvenWhenAThreadLooksBusy()
    {
        var diagnosis = Diagnostician.Diagnose(Input(busyMs: 35, monitor: Monitor(loads: [40, 35, 30, 38], gameProcessors: 1.0, busiestThread: 100), logicalProcessors: 4,
            frames: Frames(35, medianFrameMs: 36), presents: new PresentTiming(36.0, 35.0, 0.3, GpuBusyMs: 35.6)));

        Assert.Equal(Bottleneck.GraphicsCard, diagnosis.Primary);
        Assert.Contains("also shows 100% busy, which can be the graphics driver waiting for the card", diagnosis.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public void GpuTimeAboveTheFrameTimeIsNotPrintedAsMoreThanTheFrame()
    {
        var diagnosis = Diagnostician.Diagnose(Input(busyMs: 30, monitor: Monitor(gameProcessors: 0.6, busiestThread: 60), logicalProcessors: 4,
            frames: Frames(30, medianFrameMs: 53.0), presents: new PresentTiming(53.0, 30, 20, GpuBusyMs: 53.5)));

        Assert.StartsWith("The graphics card is busy for practically all of each 53.0 ms frame", diagnosis.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public void ALightlyUsedGraphicsCardDoesNotHideACpuLimit()
    {
        var diagnosis = Diagnostician.Diagnose(Input(busyMs: 40, monitor: Monitor(loads: [40, 35, 30, 38], gameProcessors: 1.05, busiestThread: 98), logicalProcessors: 4,
            presents: new PresentTiming(40.2, 40.0, 0.2, GpuBusyMs: 1.0)));

        Assert.Equal(Bottleneck.CpuOneThreadLimit, diagnosis.Primary);
    }

    [Fact]
    public void WithoutAGameHookWaitingInsidePresentPointsAwayFromTheProcessor()
    {
        var diagnosis = Diagnostician.Diagnose(Input(busyMs: 6, monitor: Monitor(gameProcessors: 0.5, busiestThread: 40), logicalProcessors: 4,
            frames: Frames(6, medianFrameMs: 22), presents: new PresentTiming(22, 6, 16)));

        Assert.Equal(Bottleneck.WaitingInPresent, diagnosis.Primary);
        Assert.Contains("16.0 ms waiting inside the present call", diagnosis.Summary);
    }

    [Fact]
    public void WithoutAGameHookAGameUsingEveryProcessorIsCpuBound()
    {
        var diagnosis = Diagnostician.Diagnose(Input(busyMs: 30, monitor: Monitor(loads: [95, 96, 94, 97], gameProcessors: 3.7, busiestThread: 99), logicalProcessors: 4));

        Assert.Equal(Bottleneck.CpuAllProcessors, diagnosis.Primary);
    }

    [Fact]
    public void WithoutEnoughSignalsNothingIsClaimed()
    {
        var diagnosis = Diagnostician.Diagnose(Input(busyMs: 30, monitor: Monitor(gameProcessors: 1.2, busiestThread: null), logicalProcessors: 8));

        Assert.Equal(Bottleneck.Unknown, diagnosis.Primary);
        Assert.Equal(ProblemCategory.Unclassified, diagnosis.Category);
    }
}
