using System.Diagnostics;
using System.Globalization;
using Gapo.Core.Diagnosis;
using Gapo.Core.Hardware;
using Gapo.Core.Measurement;

namespace Gapo.Core.Optimisation;

public sealed class OptimisationEngine
{
    public const string FrameCapSetting = "Frame-rate cap";

    private const int HighestDefaultTargetFps = 144;
    private const double MillisecondsPerSecond = 1000.0;
    private const double PercentScale = 100.0;
    private const int BaselineAttempts = 3;
    private const int MemoryPressureLoadPercent = 88;
    private const double BackgroundThatCompetesPercent = 15;
    private const double SmallestPredictionMs = 0.25;
    private const double SmallestPredictionShareOfWork = 0.025;
    private const double TargetMetWorkShare = 0.88;
    private const double TargetMetPercentile95Share = 1.25;
    private const double QualityGuardVisualLoss = 4;
    private const double SlowestFramesGuard = 0.12;
    private const double SlowestFramesGuardNoiseMultiple = 3;
    private const double GiveBackHeadroomShare = 0.6;
    private const double GiveBackSafeShare = 0.8;
    private const double GiveBackPercentile95Share = 1.3;
    private const double GiveBackSmallestVisualLoss = 2;
    private const double CapWorkHeadroom = 1.08;
    private const double CapSustainedShare = 0.9;
    private const double HalfCapWorkHeadroom = 1.1;
    private const double CapHeldShare = 0.93;
    private const double CapSpreadTolerance = 1.05;
    private const double InvisibleSpreadShareOfFramePeriod = 0.1;
    private const int MostRemainingOptionsNamed = 4;
    private const int ChangesWithoutEffectBeforeStopping = 3;
    private const double DisplayRateTolerance = 0.03;
    private const double DisplayRateBusyShare = 0.9;
    private const double NoEffectShareOfNoiseFloor = 0.5;
    private static readonly TimeSpan HitchObservationSpan = TimeSpan.FromSeconds(14);

    private readonly IGameSession session;
    private readonly IGameKnowledge knowledge;
    private readonly IChangeJournal journal;
    private readonly HardwareInventory hardware;
    private readonly Action<string, string> report;
    private readonly List<ExperimentRecord> experiments = [];
    private readonly List<SettingCandidate> keptCandidates = [];
    private readonly List<string> notTouched = [];
    private readonly IReadOnlySet<string> lockedSettings;
    private readonly OptimisationControls controls;
    private readonly List<string> skippedByPlayer = [];
    private readonly SortedSet<string> skippedBecauseLocked = new(StringComparer.Ordinal);
    private GameSettings? original;
    private GameSettings current = new([], 0);
    private WindowResult reference = new(FrameStatistics.Empty, null, []);
    private NoiseFloor noise = NoiseFloor.FromRepeatedMeasurement(0, 0);
    private double targetFps;
    private double budgetMs;
    private int windows;
    private int windowsStillExpected = InitialWindowsExpected;
    private const int InitialWindowsExpected = 10;
    private const int WindowsAfterTheLastTest = 2;
    private const int ValidationWindows = 2;
    private const double DriftNoiseMultiple = 3;
    private const double SmallestDrift = 0.15;

    public Action<int, int>? WindowMeasured { get; set; }
    private int changesWithoutEffect;

    public OptimisationEngine(IGameSession session, IGameKnowledge knowledge, IChangeJournal journal, HardwareInventory hardware, Action<string, string>? report = null, OptimisationControls? controls = null)
    {
        this.controls = controls ?? new OptimisationControls();
        lockedSettings = this.controls.LockedSettings;
        this.session = session;
        this.knowledge = knowledge;
        this.journal = journal;
        this.hardware = hardware;
        this.report = report ?? ((_, _) => { });
    }

    private bool SkipWhenLocked(SettingCandidate candidate)
    {
        if (!SettingLocks.Touches(candidate, lockedSettings))
        {
            return false;
        }

        skippedBecauseLocked.Add(candidate.Setting);
        return true;
    }

    private bool PlayerAgrees(SettingCandidate candidate)
    {
        if (controls.Approve is not { } approve || approve(candidate))
        {
            return true;
        }

        skippedByPlayer.Add(candidate.Title);
        return false;
    }

    private string[] WithLockNote()
    {
        var notes = new List<string>();
        if (skippedBecauseLocked.Count > 0)
        {
            notes.Add(SettingLocks.Note(skippedBecauseLocked));
        }

        if (skippedByPlayer.Count > 0)
        {
            notes.Add("You skipped: " + string.Join(", ", skippedByPlayer) + ".");
        }

        notes.AddRange(notTouched);
        return notes.ToArray();
    }

    public OptimisationReport Run(Goal goal)
    {
        var clock = Stopwatch.StartNew();
        var result = new OptimisationReport { Goal = goal };
        try
        {
            original = session.ReadSettings();
            current = original;
            journal.Begin(original);
            report("Preparing", "Waiting until the game has finished loading. Measuring now would blame the wrong thing.");
            session.WaitUntilWarm();
            var refresh = hardware.Display.EffectiveRefreshHz;
            targetFps = controls.TargetFps > 0 ? controls.TargetFps : goal == Goal.MaximumFps ? Math.Max(refresh, HighestDefaultTargetFps) : Math.Min(refresh, HighestDefaultTargetFps);
            budgetMs = MillisecondsPerSecond / targetFps;
            result = result with
            {
                OriginalSettings = original,
                TargetFps = targetFps,
                TargetReason = controls.TargetFps > 0 ? "the target you chose in GAPO's settings"
                    : goal == Goal.MaximumFps ? "as high as possible"
                    : hardware.Display.RefreshIsKnown ? Text($"your {refresh} Hz display")
                    : Text($"refresh rate unknown, {DisplayFacts.AssumedRefreshHz} Hz assumed"),
            };
            session.EnterBenchmark();
            report("Measuring the baseline", "Two measurements of the unchanged game show how much the numbers move by themselves.");
            var baseline = MeasureBaseline();
            reference = baseline;
            var monitor = session.Monitor();
            var hitches = session.ObserveHitches(HitchObservationSpan);
            var memoryPressure = monitor.MemoryLoadPercent >= MemoryPressureLoadPercent;
            var before = Diagnose(monitor, hitches);
            report(before.Title, before.Summary);
            result = result with { Baseline = baseline.Statistics, Noise = noise, DiagnosisBefore = before };
            notTouched.AddRange(knowledge.NotWorthTouching(current, reference, memoryPressure));
            NoteBackgroundLoad(monitor);

            var hitchFixClock = FixHitchesFirst(hitches);
            RunExperiments(goal, memoryPressure);
            if (goal is Goal.Balanced or Goal.Quality)
            {
                GiveQualityBack();
            }

            var pacingNote = TunePacing(goal, refresh);
            report("Validating the result", "Measuring the final configuration on the same scene as the baseline.");
            windowsStillExpected = ValidationWindows;
            var final = WindowResult.Combine(Measure(), Measure());
            var comparedBaseline = baseline.Statistics;
            if (EndedSlowerThanItStarted(baseline.Statistics, final.Statistics))
            {
                (comparedBaseline, final) = CheckTheEndAgainstTheOriginal(baseline.Statistics, final);
            }

            VerifyHitchFix(hitchFixClock, hitches);
            reference = final;
            var after = Diagnose(session.Monitor(), session.ObserveHitches(hitchFixClock?.Elapsed ?? HitchObservationSpan));
            session.LeaveBenchmark();
            journal.Finished();
            return result with
            {
                Baseline = comparedBaseline,
                Final = final.Statistics,
                DiagnosisAfter = after,
                FinalSettings = current,
                PacingNote = pacingNote,
                Experiments = experiments.ToArray(),
                NotTouched = WithLockNote(),
                Completed = true,
                MeasurementWindows = windows,
                Duration = clock.Elapsed,
            };
        }
        catch (GameSessionException failure)
        {
            if (!failure.GameIsGone)
            {
                TryLeaveBenchmark();
            }

            return result with
            {
                Error = failure.Message,
                FinalSettings = current,
                Experiments = experiments.ToArray(),
                NotTouched = WithLockNote(),
                MeasurementWindows = windows,
                Duration = clock.Elapsed,
            };
        }
    }

    private bool EndedSlowerThanItStarted(FrameStatistics start, FrameStatistics end) =>
        start.MedianBusyMs > 0 && end.MedianBusyMs > start.MedianBusyMs * (1 + Math.Max(noise.FloorFraction * DriftNoiseMultiple, SmallestDrift));

    private (FrameStatistics Baseline, WindowResult Final) CheckTheEndAgainstTheOriginal(FrameStatistics start, WindowResult final)
    {
        var finalSettings = current;
        report("Checking the result", "The game ran slower at the end than at the start, so GAPO measures your original settings again to tell its own changes apart from the PC slowing down.");
        windowsStillExpected = ValidationWindows;
        current = session.Apply(original!, "original settings, measured again");
        var originalNow = WindowResult.Combine(Measure(), Measure());
        if (EndedSlowerThanItStarted(originalNow.Statistics, final.Statistics))
        {
            notTouched.Insert(0, Text($"The kept changes together made frames slower ({originalNow.Statistics.MedianBusyMs:0.0} → {final.Statistics.MedianBusyMs:0.0} ms of work on the same scene), although each helped on its own, so GAPO put every setting back to your original."));
            journal.TrialEnded(current);
            return (originalNow.Statistics, originalNow);
        }

        current = session.Apply(finalSettings, "optimised settings");
        journal.TrialEnded(current);
        notTouched.Insert(0, Text($"The PC slowed down during the optimisation: your original settings needed {start.MedianBusyMs:0.0} ms of work per frame at the start and {originalNow.Statistics.MedianBusyMs:0.0} ms at the end, so something other than GAPO was using the PC. Before and after are therefore both measured at the end of the run."));
        return (originalNow.Statistics, final);
    }

    public void RevertAll()
    {
        if (original is null)
        {
            return;
        }

        current = session.Apply(original, "reverted to original");
        journal.Finished();
    }

    private WindowResult MeasureBaseline()
    {
        for (var attempt = 1; ; attempt++)
        {
            var first = Measure();
            var second = Measure();
            noise = NoiseFloor.FromRepeatedMeasurement(first.Statistics.MedianBusyMs, second.Statistics.MedianBusyMs);
            if (noise.CanBeTrusted || attempt == BaselineAttempts)
            {
                return WindowResult.Combine(first, second);
            }

            report("Measuring again", "The two baseline measurements disagreed too much to judge changes; the PC may be busy.");
        }
    }

    private WindowResult Measure()
    {
        windows++;
        windowsStillExpected = Math.Max(0, windowsStillExpected - 1);
        var result = session.Measure(current.Revision);
        WindowMeasured?.Invoke(windows, windows + windowsStillExpected);
        return result;
    }

    private BottleneckDiagnosis Diagnose(MonitorSummary monitor, FrameStatistics hitches) =>
        Diagnostician.Diagnose(new DiagnosisInput
        {
            Frames = reference.Statistics,
            TargetFps = targetFps,
            Monitor = monitor,
            Processor = new ProcessorLimits(hardware.Cpu.LogicalProcessors, hardware.Cpu.MaximumMegahertz, hardware.Cpu.LimitMegahertz),
            Stages = reference.Stages,
            HitchObservation = hitches,
        });

    private void NoteBackgroundLoad(MonitorSummary monitor)
    {
        notTouched.Add(monitor.BackgroundPercent < BackgroundThatCompetesPercent
            ? Text($"Process priority: background apps used only {monitor.BackgroundPercent:0}% of the CPU, so raising the game's priority has nothing to win.")
            : Text($"Background apps used {monitor.BackgroundPercent:0}% of the CPU. GAPO does not close programs; closing them yourself is likely to help."));
    }

    private Stopwatch? FixHitchesFirst(FrameStatistics hitches)
    {
        if (knowledge.HitchFix(current, hitches) is not { } fix || SkipWhenLocked(fix) || !PlayerAgrees(fix))
        {
            return null;
        }

        report("Fixing: " + fix.Title, fix.Hypothesis);
        journal.TrialStarted(current, fix);
        current = session.Apply(current.With(fix.Changes), "hitch fix");
        journal.TrialEnded(current);
        keptCandidates.Add(fix);
        experiments.Add(new ExperimentRecord(
            experiments.Count + 1, fix.Title, fix.Setting, fix.Kind, fix.Hypothesis, fix.VisualLoss, 0, hitches, null, 0, Decision.Kept,
            Text($"Kept: removes a repeating {hitches.LongestStorageHitchMs:0} ms freeze with no visual cost. Done first because random hitches would otherwise pollute every later measurement."),
            null));
        return Stopwatch.StartNew();
    }

    private void VerifyHitchFix(Stopwatch? sinceFix, FrameStatistics before)
    {
        var index = experiments.FindIndex(experiment => experiment.Kind == ChangeKind.HitchFix);
        if (sinceFix is null || index < 0)
        {
            return;
        }

        var since = session.ObserveHitches(sinceFix.Elapsed);
        var verdict = since.StorageHitches == 0
            ? Text($" Verified: no save freezes in the {since.DurationMs / MillisecondsPerSecond:0} s since (there were {before.StorageHitches} in the {before.DurationMs / MillisecondsPerSecond:0} s before).")
            : Text($" Check: {since.StorageHitches} save(s) still happened in the {since.DurationMs / MillisecondsPerSecond:0} s since the change.");
        experiments[index] = experiments[index] with { After = since, Reason = experiments[index].Reason + verdict };
    }

    private void RunExperiments(Goal goal, bool memoryPressure)
    {
        var tried = new HashSet<string>(StringComparer.Ordinal);
        var blocked = new HashSet<string>(StringComparer.Ordinal);
        while (true)
        {
            var smallestWorthTesting = Math.Max(SmallestPredictionMs, reference.Statistics.MedianBusyMs * SmallestPredictionShareOfWork);
            var candidates = knowledge.Candidates(current, reference, hardware.Cpu, memoryPressure)
                .Where(candidate => !tried.Contains(candidate.Key) && !blocked.Contains(candidate.Setting) && !SkipWhenLocked(candidate))
                .Where(candidate => candidate.Kind == ChangeKind.MemoryRelief || candidate.PredictedSavingMs >= smallestWorthTesting)
                .OrderByDescending(candidate => candidate.Value)
                .ToList();
            windowsStillExpected = candidates.Count + WindowsAfterTheLastTest;
            if (candidates.Count == 0)
            {
                return;
            }

            var next = candidates[0];
            var statistics = reference.Statistics;
            var targetMet = statistics.MedianBusyMs <= budgetMs * TargetMetWorkShare && statistics.Percentile95FrameMs <= budgetMs * TargetMetPercentile95Share;
            if (goal != Goal.MaximumFps && !targetMet && RunsAtTheDisplayRate(statistics) && next.VisualLoss > 0)
            {
                notTouched.Add(Text($"Stopped lowering visuals: the game already runs at your display's {hardware.Display.EffectiveRefreshHz} Hz ({statistics.AverageFps:0} FPS), so further reductions could not show more frames."));
                return;
            }

            if (goal != Goal.MaximumFps && targetMet && next.VisualLoss > 0)
            {
                var remaining = candidates.Where(candidate => candidate.VisualLoss > 0).Take(MostRemainingOptionsNamed).Select(candidate => candidate.Title);
                notTouched.Add(Text($"Stopped lowering visuals: the {targetFps:0} FPS target is met with headroom ({statistics.MedianBusyMs:0.0} ms of {budgetMs:0.0} ms). Remaining options would only cost quality: {string.Join(", ", remaining)}."));
                return;
            }

            tried.Add(next.Key);
            if (goal == Goal.Quality && next.VisualLoss >= QualityGuardVisualLoss && !targetMet)
            {
                notTouched.Add(Text($"Quality mode: not trying {next.Title} (visual cost {next.VisualLoss:0}/10)."));
                continue;
            }

            if (!PlayerAgrees(next))
            {
                continue;
            }

            var decision = RunExperiment(next, goal);
            if (decision != Decision.Kept)
            {
                blocked.Add(next.Setting);
            }

            var measured = experiments[^1];
            var hadNoEffect = decision == Decision.RolledBack && measured.GainPercent <= noise.FloorFraction * PercentScale * NoEffectShareOfNoiseFloor;
            changesWithoutEffect = hadNoEffect ? changesWithoutEffect + 1 : 0;
            if (changesWithoutEffect >= ChangesWithoutEffectBeforeStopping)
            {
                notTouched.Add(Text($"Stopped testing: the last {changesWithoutEffect} changes made no measurable difference to frame work ({statistics.MedianBusyMs:0.0} ms), although each was predicted to help. That usually means something outside these settings limits the frame rate, such as a frame-rate cap, VSync or the display itself."));
                return;
            }
        }
    }

    private Decision RunExperiment(SettingCandidate candidate, Goal goal)
    {
        report("Testing: " + candidate.Title, Text($"{candidate.Hypothesis} Predicted saving about {candidate.PredictedSavingMs:0.0} ms."));
        var before = reference;
        var snapshot = current;
        WindowResult result;
        try
        {
            result = ApplyAndMeasure(candidate, "testing " + candidate.Setting);
        }
        catch (GameSessionException failure) when (!failure.GameIsGone)
        {
            RollBackTo(snapshot);
            Record(candidate, before, null, 0, Decision.Failed, "The measurement could not be completed (" + failure.Message + "), so the change was rolled back.");
            return Decision.Failed;
        }

        var gain = ChangeComparison.RelativeGain(before.Statistics.MedianBusyMs, result.Statistics.MedianBusyMs);
        if (candidate.Kind != ChangeKind.MemoryRelief && ChangeComparison.Judge(gain, noise) == ChangeEffect.NeedsConfirmation)
        {
            var confirmation = Measure();
            var confirmedGain = ChangeComparison.RelativeGain(before.Statistics.MedianBusyMs, confirmation.Statistics.MedianBusyMs);
            gain = Math.Sign(confirmedGain) == Math.Sign(gain) ? (gain + confirmedGain) / 2 : 0;
            result = WindowResult.Combine(result, confirmation);
        }

        var (decision, reason) = Decide(candidate, goal, before.Statistics, result.Statistics, gain);
        if (decision == Decision.Kept)
        {
            reference = result;
            keptCandidates.Add(candidate);
            journal.TrialEnded(current);
        }
        else
        {
            RollBackTo(snapshot);
        }

        var bottleneckAfter = decision == Decision.Kept ? Diagnose(session.Monitor(), FrameStatistics.Empty).Title : null;
        Record(candidate, before, result.Statistics, gain * PercentScale, decision, reason, bottleneckAfter);
        report((decision == Decision.Kept ? "Kept: " : "Rolled back: ") + candidate.Title, reason);
        return decision;
    }

    private (Decision Decision, string Reason) Decide(SettingCandidate candidate, Goal goal, FrameStatistics before, FrameStatistics after, double gain)
    {
        var noisePercent = noise.FloorFraction * PercentScale;
        var gainPercent = gain * PercentScale;
        if (candidate.Kind == ChangeKind.MemoryRelief)
        {
            return (Decision.Kept, "Kept: relieves memory pressure.");
        }

        if (gain < -noise.FloorFraction)
        {
            return (Decision.RolledBack, Text($"Rolled back: frames took {-gainPercent:0}% more work after this change, beyond the ±{noisePercent:0.0}% measurement noise."));
        }

        if (gain <= noise.FloorFraction)
        {
            var visualNote = candidate.VisualLoss > 0 ? ", so not worth any visual cost." : ".";
            return (Decision.RolledBack, Text($"Rolled back: {gainPercent:+0;-0;0}% is within the ±{noisePercent:0.0}% measurement noise, so there is no proven benefit{visualNote}"));
        }

        var slowestFramesWorsening = before.Percentile95FrameMs <= 0 ? 0 : (after.Percentile95FrameMs - before.Percentile95FrameMs) / before.Percentile95FrameMs;
        if (slowestFramesWorsening > Math.Max(SlowestFramesGuard, noise.FloorFraction * SlowestFramesGuardNoiseMultiple))
        {
            return (Decision.RolledBack, Text($"Rolled back: typical frames got faster but the slowest frames got slower ({before.Percentile95FrameMs:0.0} → {after.Percentile95FrameMs:0.0} ms). Smoothness matters more than averages."));
        }

        var weights = GoalWeights.For(goal);
        var paceGain = before.Percentile95FrameMs <= 0 ? 0 : -slowestFramesWorsening;
        var score = (weights.Performance * gain) + (weights.Pacing * paceGain) - (weights.PerVisualLossPoint * candidate.VisualLoss);
        if (score <= 0)
        {
            return (Decision.RolledBack, Text($"Rolled back: {gainPercent:+0;-0;0}% faster is not worth a visual cost of {candidate.VisualLoss:0}/10 in {goal} mode."));
        }

        var costNote = candidate.VisualLoss == 0 ? " with no visual cost." : candidate.VisualLoss <= 2 ? " for a small visual cost." : ", a visible trade-off that is worth it for this goal.";
        return (Decision.Kept, Text($"Kept: frame work {before.MedianBusyMs:0.0} → {after.MedianBusyMs:0.0} ms ({gainPercent:+0;-0;0}%, above the ±{noisePercent:0.0}% noise floor){costNote}"));
    }

    private void GiveQualityBack()
    {
        if (original is null)
        {
            return;
        }

        var visibleReductions = experiments
            .Where(experiment => experiment.Decision == Decision.Kept && experiment.Kind == ChangeKind.Throughput && experiment.VisualLoss >= GiveBackSmallestVisualLoss)
            .OrderByDescending(experiment => experiment.VisualLoss)
            .ToList();
        foreach (var reduction in visibleReductions)
        {
            var work = reference.Statistics.MedianBusyMs;
            if (work > budgetMs * GiveBackHeadroomShare)
            {
                return;
            }

            var candidate = keptCandidates.First(kept => kept.Setting == reduction.Setting);
            var cost = Math.Max(0, reduction.MeasuredSavingMs);
            if (work + cost > budgetMs * GiveBackSafeShare)
            {
                notTouched.Add(Text($"Quality give-back: restoring {reduction.Setting} would cost about {cost:0.0} ms, and only {Math.Max(0, (budgetMs * GiveBackSafeShare) - work):0.0} ms of safe headroom is left."));
                continue;
            }

            var restore = new SettingCandidate(
                reduction.Setting, candidate.To, candidate.From, ChangeKind.QualityRestore, -reduction.VisualLoss, -cost,
                Text($"There is plenty of headroom ({work:0.0} ms of a {budgetMs:0.0} ms budget), so GAPO tries to give back the most visible reduction."),
                original.ValuesOf(candidate.Changes.Keys));
            RestoreQuality(restore);
        }
    }

    private void RestoreQuality(SettingCandidate restore)
    {
        report("Restore quality: " + restore.Setting, restore.Hypothesis);
        var before = reference;
        var snapshot = current;
        WindowResult result;
        try
        {
            result = ApplyAndMeasure(restore, "restoring " + restore.Setting);
        }
        catch (GameSessionException failure) when (!failure.GameIsGone)
        {
            RollBackTo(snapshot);
            Record(restore, before, null, 0, Decision.Failed, "The measurement could not be completed (" + failure.Message + "), so the previous setting was put back.");
            return;
        }

        var gainPercent = ChangeComparison.RelativeGain(before.Statistics.MedianBusyMs, result.Statistics.MedianBusyMs) * PercentScale;
        if (result.Statistics.MedianBusyMs <= budgetMs * GiveBackSafeShare && result.Statistics.Percentile95FrameMs <= budgetMs * GiveBackPercentile95Share)
        {
            reference = result;
            journal.TrialEnded(current);
            Record(restore, before, result.Statistics, gainPercent, Decision.Kept, Text($"Kept: quality restored and the target is still met ({result.Statistics.MedianBusyMs:0.0} ms of {budgetMs:0.0} ms)."));
        }
        else
        {
            RollBackTo(snapshot);
            Record(restore, before, result.Statistics, gainPercent, Decision.RolledBack, Text($"Rolled back: restoring it would break the frame budget ({result.Statistics.MedianBusyMs:0.0} ms)."));
        }
    }

    private const string CapsTurnedOff = "Not capped: you turned frame-rate caps off in GAPO's settings.";

    private string TunePacing(Goal goal, int refresh)
    {
        if (goal == Goal.MaximumFps)
        {
            notTouched.Add("Frame-rate cap: Maximum FPS mode never caps.");
            return "No frame cap (Maximum FPS mode).";
        }

        if (!controls.AllowFrameCap)
        {
            return CapsTurnedOff;
        }

        var statistics = reference.Statistics;
        if (RunsAtTheDisplayRate(statistics))
        {
            notTouched.Add(Text($"Frame-rate cap: the display already paces the game at about {statistics.AverageFps:0} FPS."));
            return Text($"No cap needed: the display already paces the game at about {statistics.AverageFps:0} FPS.");
        }

        var sustainedFps = MillisecondsPerSecond / Math.Max(0.1, statistics.Percentile95FrameMs);
        var workFps = MillisecondsPerSecond / Math.Max(0.1, statistics.MedianBusyMs);
        int cap;
        string why;
        if (workFps >= refresh * CapWorkHeadroom && sustainedFps >= refresh * CapSustainedShare)
        {
            cap = refresh;
            why = Text($"Your PC now finishes frames well within {MillisecondsPerSecond / refresh:0.0} ms, so locking to your {refresh} Hz display gives even frame pacing and frees CPU time (less heat and fan noise).");
        }
        else if (goal == Goal.Smoothness && workFps >= refresh / 2.0 * HalfCapWorkHeadroom)
        {
            cap = refresh / 2;
            why = Text($"Smoothness mode: {refresh} FPS is not sustainable yet, so a steady {cap} FPS (an even divisor of the refresh rate) avoids judder.");
        }
        else
        {
            var note = Text($"{refresh} FPS is not sustainable yet, and on a fixed-refresh display a cap that is not a divisor of {refresh} Hz would judder.");
            notTouched.Add("Frame-rate cap: " + note);
            return "Not capped: " + note;
        }

        var candidate = knowledge.FrameCap(current, cap, why);
        if (SkipWhenLocked(candidate))
        {
            return "Frame-rate cap left as it is: you locked it.";
        }

        if (!PlayerAgrees(candidate))
        {
            return "Not capped: you skipped the frame-rate cap.";
        }

        if (candidate.Changes.All(change => current.Is(change.Key, change.Value)))
        {
            return Text($"Already capped at {cap} FPS.");
        }

        report("Tuning frame pacing", why);
        var before = reference;
        var snapshot = current;
        var result = ApplyAndMeasure(candidate, "frame cap");
        var spreadBefore = before.Statistics.FrameTimeSpreadMs;
        var spreadAfter = result.Statistics.FrameTimeSpreadMs;
        var held = result.Statistics.AverageFps >= cap * CapHeldShare;
        var invisibleSpreadMs = MillisecondsPerSecond / cap * InvisibleSpreadShareOfFramePeriod;
        if (held && spreadAfter <= Math.Max(spreadBefore * CapSpreadTolerance, invisibleSpreadMs))
        {
            reference = result;
            journal.TrialEnded(current);
            Record(candidate, before, result.Statistics, 0, Decision.Kept, Text($"Kept: frame-time variation {spreadBefore:0.0} → {spreadAfter:0.0} ms at a steady {result.Statistics.AverageFps:0} FPS."));
            return Text($"Locked to {cap} FPS to match your {refresh} Hz display.");
        }

        RollBackTo(snapshot);
        if (held)
        {
            Record(candidate, before, result.Statistics, 0, Decision.RolledBack, Text($"Rolled back: frame-time variation went from {spreadBefore:0.0} to {spreadAfter:0.0} ms, more than the {invisibleSpreadMs:0.0} ms that would go unnoticed at {cap} FPS. The cap made pacing less even on this PC, so the extra frames are worth more."));
            return Text($"Left uncapped: a {cap} FPS cap was tested and did not make frame pacing smoother here.");
        }

        Record(candidate, before, result.Statistics, 0, Decision.RolledBack, Text($"Rolled back: the {cap} FPS cap did not hold ({result.Statistics.AverageFps:0} FPS measured)."));
        return Text($"Left uncapped: a {cap} FPS cap could not be held.");
    }

    private bool RunsAtTheDisplayRate(FrameStatistics statistics)
    {
        var refreshPeriodMs = MillisecondsPerSecond / hardware.Display.EffectiveRefreshHz;
        return statistics.MedianFrameMs >= refreshPeriodMs * (1 - DisplayRateTolerance)
            && statistics.MedianFrameMs <= refreshPeriodMs * (1 + DisplayRateTolerance)
            && statistics.MedianBusyMs >= refreshPeriodMs * DisplayRateBusyShare
            && statistics.Percentile95FrameMs <= refreshPeriodMs * TargetMetPercentile95Share;
    }

    private WindowResult ApplyAndMeasure(SettingCandidate candidate, string note)
    {
        journal.TrialStarted(current, candidate);
        current = session.Apply(current.With(candidate.Changes), note);
        return Measure();
    }

    private void RollBackTo(GameSettings snapshot)
    {
        current = session.Apply(snapshot, "rollback");
        journal.TrialEnded(current);
    }

    private void Record(SettingCandidate candidate, WindowResult before, FrameStatistics? after, double gainPercent, Decision decision, string reason, string? bottleneckAfter = null) =>
        experiments.Add(new ExperimentRecord(
            experiments.Count + 1,
            candidate.Kind == ChangeKind.QualityRestore ? "Restore quality: " + candidate.Setting : candidate.Title,
            candidate.Setting,
            candidate.Kind,
            candidate.Hypothesis,
            candidate.VisualLoss,
            candidate.PredictedSavingMs,
            before.Statistics,
            after,
            gainPercent,
            decision,
            reason,
            bottleneckAfter));

    private void TryLeaveBenchmark()
    {
        try
        {
            session.LeaveBenchmark();
        }
        catch (GameSessionException)
        {
        }
    }

    private static string Text(FormattableString message) => message.ToString(CultureInfo.InvariantCulture);
}
