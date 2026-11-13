using System.Globalization;

namespace Gapo.Core.Diagnosis;

public static class Diagnostician
{
    public const string MainThreadLimitation =
        "This is a main-thread limitation. External settings cannot make the game simulation multithreaded. Reducing simulation distance is likely to provide the largest available improvement.";

    private const double MillisecondsPerSecond = 1000.0;
    private const double WarmupShareThatBlocksJudgement = 0.2;
    private const double SmallestStreamingStageMs = 0.05;
    private const double SmallestMeaningfulWorkMs = 0.01;
    private const double TargetMetShareOfBudget = 0.92;
    private const double IdleOtherProcessorsPercent = 35;
    private const double BackgroundThatCompetesPercent = 15;
    private const double HiddenOneThreadGapPoints = 25;
    private const double MonitorHasDataPercent = 0.5;
    private const int MemoryPressureLoadPercent = 88;
    private const long MemoryPressureAvailableBytes = 700L * 1024 * 1024;
    private const double UnevenPacingFactor = 2.2;
    private const double ClockLimitShare = 0.8;
    private const double BusyThreadPercent = 85;
    private const double OneThreadProcessorsUsed = 1.6;
    private const double AllProcessorsShare = 0.8;
    private const double WaitingInPresentShare = 0.4;
    private const double GraphicsCardShareOfFrame = 0.8;
    private const double GraphicsCardSaturatedShare = 0.95;
    private const int MostExpensivePassesNamed = 3;

    public static BottleneckDiagnosis Diagnose(DiagnosisInput input)
    {
        var budget = MillisecondsPerSecond / input.TargetFps;
        var work = Math.Max(SmallestMeaningfulWorkMs, input.Frames.MedianBusyMs);
        if (input.WarmupShareOfFrames > WarmupShareThatBlocksJudgement)
        {
            return new BottleneckDiagnosis(
                Bottleneck.Transient,
                "The game is still loading",
                "The game is streaming data in. Judging performance now would blame the wrong thing, so GAPO waits until it settles.",
                ProblemCategory.Transient,
                null,
                StagesOf(input, work),
                [],
                [],
                input.TargetFps,
                budget,
                work);
        }

        var primary = input.Stages is { } stages ? FromStageTimes(stages, input, work) : FromOutside(input, work);
        var hitches = input.HitchObservation ?? input.Frames;
        var diagnosis = new BottleneckDiagnosis(
            primary.Bottleneck,
            primary.Title,
            primary.Summary,
            primary.Category,
            primary.EngineLimitation,
            StagesOf(input, work),
            Evidence(input, work, budget),
            SecondaryProblems(input, hitches),
            input.TargetFps,
            budget,
            work);
        var waitsBeyondTheBudget = primary.Bottleneck is Bottleneck.WaitingInPresent or Bottleneck.GraphicsCard && input.Presents is { } presents && presents.MedianFrameMs > budget;
        if (work <= budget * TargetMetShareOfBudget && hitches.StorageHitches == 0 && !waitsBeyondTheBudget)
        {
            return diagnosis with
            {
                Primary = Bottleneck.TargetMet,
                Title = "Target reached",
                Summary = Text($"Each frame takes {work:0.0} ms of work, inside the {budget:0.0} ms budget for {input.TargetFps:0} FPS."),
            };
        }

        return diagnosis;
    }

    private static Finding FromStageTimes(StageTimes stages, DiagnosisInput input, double work)
    {
        var logical = Math.Max(1, input.Processor.LogicalProcessors);
        if (stages.RenderMs >= stages.SimulationMs && stages.RenderThreads <= 1 && logical > 1)
        {
            return new Finding(
                Bottleneck.RenderSingleThread,
                "Rendering is stuck on one CPU thread",
                Text($"Rendering takes {stages.RenderMs:0.0} ms of each {work:0.0} ms frame, all on 1 of your {logical} CPU threads{OtherProcessorsNote(input.Monitor)}."),
                ProblemCategory.GameSetting,
                null);
        }

        if (stages.RenderMs >= stages.SimulationMs)
        {
            var expensive = stages.RenderPasses
                .OrderByDescending(pass => pass.Milliseconds)
                .Take(MostExpensivePassesNamed)
                .Select(pass => Text($"{pass.Name} {pass.Milliseconds:0.0} ms"));
            return new Finding(
                Bottleneck.RenderWorkload,
                "Rendering workload (pixels and effects)",
                Text($"Rendering takes {stages.RenderMs:0.0} ms of each {work:0.0} ms frame. Most expensive: {string.Join(", ", expensive)}."),
                ProblemCategory.GameSetting,
                null);
        }

        return new Finding(
            Bottleneck.CpuMainThread,
            "CPU main thread (simulation)",
            Text($"The simulation takes {stages.SimulationMs:0.0} ms of each {work:0.0} ms frame ({stages.EntitiesSimulated} creatures simulated, physics {stages.PhysicsMs:0.0} ms)."),
            ProblemCategory.EngineLimitation,
            MainThreadLimitation);
    }

    private static Finding FromOutside(DiagnosisInput input, double work)
    {
        var monitor = input.Monitor;
        var logical = Math.Max(1, input.Processor.LogicalProcessors);
        var busiestThread = monitor.BusiestGameThreadPercent;
        if (input.Presents is { GpuBusyMs: { } gpu } timing && timing.MedianFrameMs > 0
            && (gpu >= timing.MedianFrameMs * GraphicsCardSaturatedShare || (gpu >= timing.MedianFrameMs * GraphicsCardShareOfFrame && busiestThread is not >= BusyThreadPercent)))
        {
            var processorNote = busiestThread switch
            {
                >= BusyThreadPercent => Text($". The game's busiest processor thread also shows {busiestThread:0}% busy, which can be the graphics driver waiting for the card"),
                { } thread => Text($", while the game's busiest processor thread is busy only {thread:0}% of the time"),
                _ => string.Empty,
            };
            return new Finding(
                Bottleneck.GraphicsCard,
                "The graphics card is the limit",
                (gpu >= timing.MedianFrameMs
                    ? Text($"The graphics card is busy for practically all of each {timing.MedianFrameMs:0.0} ms frame")
                    : Text($"The graphics card is busy {gpu:0.0} ms of each {timing.MedianFrameMs:0.0} ms frame")) + processorNote + ". Lower resolution, anti-aliasing, shadows or post-processing help; a faster processor would not.",
                ProblemCategory.GameSetting,
                null);
        }

        if (input.Presents is { } presents && presents.MedianFrameMs > 0 && presents.MedianInPresentMs >= presents.MedianFrameMs * WaitingInPresentShare)
        {
            return new Finding(
                Bottleneck.WaitingInPresent,
                "The game waits to show each frame",
                Text($"Of each {presents.MedianFrameMs:0.0} ms frame the game spends {presents.MedianInPresentMs:0.0} ms waiting inside the present call. That is time spent waiting for the graphics card, VSync or a frame-rate cap, not CPU work."),
                ProblemCategory.GameSetting,
                null);
        }

        if (monitor.BusiestGameThreadPercent is { } busiest && busiest >= BusyThreadPercent && monitor.GameProcessorsUsed < OneThreadProcessorsUsed && logical > 1)
        {
            return new Finding(
                Bottleneck.CpuOneThreadLimit,
                "The game is limited by one CPU thread",
                Text($"The game's busiest thread uses {busiest:0}% of one processor while the whole game uses {monitor.GameProcessorsUsed:0.0} of your {logical} CPU threads. More processors cannot help work that runs on one thread."),
                ProblemCategory.EngineLimitation,
                null);
        }

        if (monitor.GameProcessorsUsed >= logical * AllProcessorsShare)
        {
            return new Finding(
                Bottleneck.CpuAllProcessors,
                "The CPU is fully used by the game",
                Text($"The game uses {monitor.GameProcessorsUsed:0.0} of your {logical} CPU threads."),
                ProblemCategory.GameSetting,
                null);
        }

        return new Finding(
            Bottleneck.Unknown,
            "No single limit stands out",
            Text($"Each frame takes {work:0.0} ms of work. GAPO needs more detail from this game to name the stage that costs the most."),
            ProblemCategory.Unclassified,
            null);
    }

    private static IReadOnlyList<StageShare> StagesOf(DiagnosisInput input, double work)
    {
        var shares = new List<StageShare>();
        if (input.Stages is { } stages)
        {
            Add("Simulation (main thread)", stages.SimulationMs);
            Add("Rendering", stages.RenderMs);
            Add("Present to screen", stages.PresentMs);
            if (stages.StreamingMs > SmallestStreamingStageMs)
            {
                Add("Streaming and saving", stages.StreamingMs);
            }

            Add("Other", Math.Max(0, work - stages.SimulationMs - stages.RenderMs - stages.PresentMs - stages.StreamingMs));
        }
        else if (input.Presents is { } presents)
        {
            Add("CPU work", presents.MedianCpuBusyMs);
            Add("Waiting to present", presents.MedianInPresentMs);
            if (presents.GpuBusyMs is { } gpuBusy)
            {
                Add("Graphics card busy", gpuBusy);
            }
        }

        return shares;

        void Add(string name, double milliseconds) => shares.Add(new StageShare(name, milliseconds, milliseconds / work));
    }

    private static string OtherProcessorsNote(MonitorSummary monitor)
    {
        if (monitor.LoadPerProcessor.Count < 2 || monitor.AverageLoadPercent < MonitorHasDataPercent)
        {
            return string.Empty;
        }

        var others = (monitor.LoadPerProcessor.Sum() - monitor.LoadPerProcessor.Max()) / (monitor.LoadPerProcessor.Count - 1);
        if (others < IdleOtherProcessorsPercent)
        {
            return Text($", while the other processors are mostly idle ({others:0}% on average)");
        }

        return monitor.BackgroundPercent >= BackgroundThatCompetesPercent
            ? Text($"; the other processors are busy with background apps ({monitor.BackgroundPercent:0}% of the CPU)")
            : string.Empty;
    }

    private static IReadOnlyList<string> Evidence(DiagnosisInput input, double work, double budget)
    {
        var monitor = input.Monitor;
        var evidence = new List<string>();
        var hasProcessorData = monitor.LoadPerProcessor.Count > 1 && monitor.AverageLoadPercent > MonitorHasDataPercent;
        if (hasProcessorData && monitor.BusiestProcessorPercent - monitor.AverageLoadPercent >= HiddenOneThreadGapPoints)
        {
            evidence.Add(Text($"Total CPU use is {monitor.AverageLoadPercent:0}%, but the busiest processor runs at {monitor.BusiestProcessorPercent:0}%: overall CPU use hides a one-thread limit."));
        }

        if (hasProcessorData)
        {
            evidence.Add("Load per processor: " + string.Join(" · ", monitor.LoadPerProcessor.Select((load, index) => Text($"{index} {load:0}%"))));
        }

        if (monitor.BusiestGameThreadPercent is { } busiest)
        {
            evidence.Add(Text($"The game's busiest thread uses {busiest:0}% of one processor (measured from outside the game)."));
        }

        evidence.Add(Text($"Frame work {work:0.0} ms against a {budget:0.0} ms budget for {input.TargetFps:0} FPS."));
        return evidence;
    }

    private static IReadOnlyList<string> SecondaryProblems(DiagnosisInput input, Measurement.FrameStatistics hitches)
    {
        var monitor = input.Monitor;
        var problems = new List<string>();
        if (hitches.StorageHitches > 0)
        {
            problems.Add(Text($"Storage hitch: the game froze for up to {hitches.LongestStorageHitchMs:0} ms while saving, {hitches.StorageHitches} time(s) in the last {hitches.DurationMs / MillisecondsPerSecond:0} s."));
        }

        if (monitor.BackgroundPercent >= BackgroundThatCompetesPercent)
        {
            problems.Add(Text($"Background apps use {monitor.BackgroundPercent:0}% of the CPU while you play."));
        }

        if (monitor.MemoryLoadPercent >= MemoryPressureLoadPercent || monitor.LowestAvailableMemoryBytes is > 0 and < MemoryPressureAvailableBytes)
        {
            problems.Add(Text($"Memory pressure: {monitor.MemoryLoadPercent}% of RAM is in use."));
        }

        if (input.Frames.Percentile99FrameMs > input.Frames.MedianFrameMs * UnevenPacingFactor && hitches.StorageHitches == 0)
        {
            problems.Add(Text($"Uneven frame pacing: the slowest 1% of frames take {input.Frames.Percentile99FrameMs:0.0} ms against a typical {input.Frames.MedianFrameMs:0.0} ms."));
        }

        var processor = input.Processor;
        if (processor.LimitMegahertz > 0 && processor.MaximumMegahertz > 0 && processor.LimitMegahertz < processor.MaximumMegahertz * ClockLimitShare)
        {
            problems.Add(Text($"Windows reports the CPU clock limited to {processor.LimitMegahertz} of {processor.MaximumMegahertz} MHz (a power or thermal limit)."));
        }

        return problems;
    }

    private static string Text(FormattableString message) => message.ToString(CultureInfo.InvariantCulture);

    private sealed record Finding(Bottleneck Bottleneck, string Title, string Summary, ProblemCategory Category, string? EngineLimitation);
}
