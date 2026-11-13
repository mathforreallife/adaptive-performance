using System.Globalization;
using Gapo.Agent.Measurement;
using Gapo.Agent.Monitoring;
using Gapo.Core.Diagnosis;
using Gapo.Core.FrameTiming;
using Gapo.Core.Games.Unreal;
using Gapo.Core.Hardware;
using Gapo.Core.Measurement;
using Gapo.Core.Optimisation;
using Gapo.Core.Protocol;
using Gapo.Windows.Games;

namespace Gapo.Agent.Optimisation;

public sealed class RestartOptimiser
{
    public const int MostRestartsPerRun = 6;
    public const int ChangesWithoutEffectBeforeStopping = 3;

    private const double PercentScale = 100.0;
    private const double MillisecondsPerSecond = 1000.0;
    private const double QualityGoalLargestVisualLoss = 0.3;
    private const string UnlimitedTargetReason = "Maximum FPS mode: no target, every candidate is tested.";

    private readonly string gameName;
    private readonly string settingsPath;
    private readonly Dictionary<string, string> values;
    private readonly Dictionary<string, string> originalValues;
    private readonly IniChangeJournal journal;
    private readonly Goal goal;
    private readonly double targetFps;
    private readonly AgentMonitor monitor;
    private readonly HardwareInventory hardware;
    private readonly Action<AgentState, string, string> report;
    private readonly TimeSpan startedAt;
    private readonly Func<long>? gpuBusyTicksSoFar;
    private readonly List<ExperimentRecord> experiments = [];
    private readonly List<string> notTouched = [];
    private Queue<UnrealCandidate> candidates = new();
    private Phase phase = Phase.Baseline;
    private ObservedGameDiagnosis? observation;
    private int observedProcessId;
    private int processIdWhenChanged;
    private UnrealCandidate? underTest;
    private bool measuredAgainInThisSession;
    private FrameStatistics baseline = FrameStatistics.Empty;
    private FrameStatistics reference = FrameStatistics.Empty;
    private double noiseFloorPercent;
    private BottleneckDiagnosis? diagnosisBefore;
    private BottleneckDiagnosis? diagnosisAfter;
    private int sessions;
    private int changesWithoutEffect;
    private bool lastChangeWaitsForRestart;

    public RestartOptimiser(
        string gameName,
        string settingsPath,
        IReadOnlyDictionary<string, string> currentValues,
        IniChangeJournal journal,
        Goal goal,
        AgentMonitor monitor,
        HardwareInventory hardware,
        Action<AgentState, string, string> report,
        TimeSpan now,
        int processId,
        Func<long>? gpuBusyTicksSoFar = null)
    {
        this.gpuBusyTicksSoFar = gpuBusyTicksSoFar;
        this.gameName = gameName;
        this.settingsPath = settingsPath;
        values = new Dictionary<string, string>(currentValues, StringComparer.Ordinal);
        originalValues = new Dictionary<string, string>(currentValues, StringComparer.Ordinal);
        this.journal = journal;
        this.goal = goal;
        this.monitor = monitor;
        this.hardware = hardware;
        this.report = report;
        startedAt = now;
        targetFps = goal == Goal.MaximumFps ? 0 : Math.Min(hardware.Display.EffectiveRefreshHz, 144);
        journal.Begin(settingsPath);
        StartObserving(now, processId);
        report(AgentState.Optimising, "Measuring the baseline", Text($"GAPO watches {ObservedGameDiagnosis.ObservationLength.TotalSeconds:0} s of play on your current settings once the game has settled. Keep playing as you normally would."));
    }

    private enum Phase
    {
        Baseline,
        WaitingForRestart,
        Measuring,
        Finished,
    }

    public void OnFrame(in PresentedFrame frame) => observation?.OnFrame(frame);

    public OptimisationReport? Advance(TimeSpan now, int runningProcessId)
    {
        if (phase == Phase.Finished)
        {
            return null;
        }

        if (runningProcessId != observedProcessId)
        {
            return OnGameChanged(now, runningProcessId);
        }

        if (observation is null || !observation.Advance(now, monitor, hardware))
        {
            return null;
        }

        sessions++;
        return phase == Phase.Baseline ? AfterBaseline(now) : AfterMeasurement(now);
    }

    public OptimisationReport Stop(TimeSpan now)
    {
        if (underTest is { } candidate)
        {
            PutBack(candidate);
            experiments.Add(Record(candidate, null, 0, Decision.Failed, "Stopped by you before it could be measured; the setting was put back."));
            lastChangeWaitsForRestart = true;
        }

        return Finish(now, completed: false, "Stopped before the end. The setting under test was put back.");
    }

    private OptimisationReport? OnGameChanged(TimeSpan now, int runningProcessId)
    {
        if (runningProcessId == 0)
        {
            observedProcessId = 0;
            observation = null;
            if (phase != Phase.WaitingForRestart)
            {
                report(AgentState.Optimising, "Waiting for the game", "The game closed during a measurement. Start it again and GAPO measures the next session.");
            }

            return null;
        }

        if (phase == Phase.WaitingForRestart && runningProcessId != processIdWhenChanged)
        {
            phase = Phase.Measuring;
            report(AgentState.Optimising, "Measuring: " + underTest!.Title, Text($"The game restarted with the changed setting. GAPO watches {ObservedGameDiagnosis.ObservationLength.TotalSeconds:0} s of play once it has settled."));
        }

        StartObserving(now, runningProcessId);
        return null;
    }

    private OptimisationReport? AfterBaseline(TimeSpan now)
    {
        if (observation!.Statistics.Count == 0 || observation.Result is not { } diagnosis)
        {
            return Finish(now, completed: false, "The baseline could not be measured: " + (observation.FailureReason ?? "no frames were seen."));
        }

        baseline = observation.Statistics;
        reference = baseline;
        diagnosisBefore = diagnosis;
        noiseFloorPercent = RestartJudgement.NoiseFloorPercent(observation.FirstHalf, observation.SecondHalf);
        var ordered = UnrealKnowledge.Candidates(values, diagnosis.Primary).ToList();
        if (goal == Goal.Quality)
        {
            foreach (var skipped in ordered.Where(candidate => candidate.VisualLoss > QualityGoalLargestVisualLoss))
            {
                notTouched.Add($"Quality mode: not trying {skipped.Title}, it costs too much visual quality.");
            }

            ordered = ordered.Where(candidate => candidate.VisualLoss <= QualityGoalLargestVisualLoss).ToList();
        }

        candidates = new Queue<UnrealCandidate>(ordered);
        if (RestartJudgement.TargetMet(reference, targetFps))
        {
            return Finish(now, completed: true, null, Text($"The game already finishes frames well within {MillisecondsPerSecond / targetFps:0.0} ms, so no setting was lowered."));
        }

        return ProposeNextOrFinish(now);
    }

    private OptimisationReport? AfterMeasurement(TimeSpan now)
    {
        var candidate = underTest!;
        var measured = observation!.Statistics;
        diagnosisAfter = observation.Result ?? diagnosisAfter;
        var verdict = RestartJudgement.Judge(reference, measured, noiseFloorPercent);
        if (verdict.LooksDisturbed && !measuredAgainInThisSession)
        {
            measuredAgainInThisSession = true;
            observation = new ObservedGameDiagnosis(now, gpuBusyTicksSoFar);
            report(AgentState.Optimising, "Measuring again: " + candidate.Title, Text($"Frames took {-verdict.GainPercent:0}% more work after lowering this setting, which a lower setting should not cause; something else probably disturbed those {ObservedGameDiagnosis.ObservationLength.TotalSeconds:0} s. GAPO measures another {ObservedGameDiagnosis.ObservationLength.TotalSeconds:0} s in the same session, no restart needed."));
            return null;
        }

        underTest = null;
        measuredAgainInThisSession = false;
        var key = new IniKey(candidate.Setting.Section, candidate.Setting.Key);
        if (verdict.Keep)
        {
            values[candidate.Setting.Key] = candidate.To;
            journal.TrialEnded(key, candidate.To);
            experiments.Add(Record(candidate, measured, verdict.GainPercent, Decision.Kept, verdict.Reason));
            reference = measured;
            changesWithoutEffect = 0;
            lastChangeWaitsForRestart = false;
        }
        else
        {
            PutBack(candidate);
            experiments.Add(Record(candidate, measured, verdict.GainPercent, Decision.RolledBack, verdict.Reason));
            changesWithoutEffect = verdict.ShowedNoEffect ? changesWithoutEffect + 1 : 0;
            lastChangeWaitsForRestart = true;
        }

        if (RestartJudgement.TargetMet(reference, targetFps))
        {
            return Finish(now, completed: true, null, Text($"Target reached: frames now take {reference.MedianBusyMs:0.0} ms of work, within the {MillisecondsPerSecond / targetFps:0.0} ms budget for {targetFps:0} FPS."));
        }

        if (changesWithoutEffect >= ChangesWithoutEffectBeforeStopping)
        {
            return Finish(now, completed: true, null, Text($"Stopped after {ChangesWithoutEffectBeforeStopping} changes in a row that changed frame work by no more than half the noise floor; the remaining settings are expected to matter even less."));
        }

        return ProposeNextOrFinish(now);
    }

    private OptimisationReport? ProposeNextOrFinish(TimeSpan now)
    {
        if (candidates.Count == 0 || experiments.Count >= MostRestartsPerRun)
        {
            return Finish(now, completed: true, null, candidates.Count == 0 ? "Every setting worth testing has been tested." : Text($"Stopped after {MostRestartsPerRun} restarts so the session does not drag on; optimise again to continue."));
        }

        var candidate = candidates.Dequeue();
        var key = new IniKey(candidate.Setting.Section, candidate.Setting.Key);
        journal.TrialStarted(key, candidate.From, candidate.To);
        if (!IniSettingsFile.Change(settingsPath, document => document.Set(key.Section, key.Key, candidate.To)))
        {
            journal.TrialEnded(key, candidate.From);
            return Finish(now, completed: false, "The game's settings file could not be written: " + settingsPath);
        }

        underTest = candidate;
        phase = Phase.WaitingForRestart;
        processIdWhenChanged = observedProcessId;
        observation = null;
        report(AgentState.Optimising, "Restart the game to test: " + candidate.Title, $"{candidate.Why} GAPO changed only this setting in {Path.GetFileName(settingsPath)}. Close the game and start it again; GAPO then measures {ObservedGameDiagnosis.ObservationLength.TotalSeconds:0} s of play and keeps the change only if each frame takes measurably less work.");
        return null;
    }

    private void PutBack(UnrealCandidate candidate)
    {
        var key = new IniKey(candidate.Setting.Section, candidate.Setting.Key);
        IniSettingsFile.Change(settingsPath, document => document.Set(key.Section, key.Key, candidate.From));
        journal.TrialEnded(key, candidate.From);
        underTest = null;
    }

    private void StartObserving(TimeSpan now, int processId)
    {
        observedProcessId = processId;
        observation = processId == 0 || phase == Phase.WaitingForRestart ? null : new ObservedGameDiagnosis(now, gpuBusyTicksSoFar);
    }

    private ExperimentRecord Record(UnrealCandidate candidate, FrameStatistics? after, double gainPercent, Decision decision, string reason) =>
        new(
            experiments.Count + 1,
            candidate.Title,
            candidate.Setting.Label,
            ChangeKind.Throughput,
            candidate.Why,
            candidate.VisualLoss,
            reference.MedianBusyMs * candidate.ExpectedShareSaved,
            reference,
            after,
            gainPercent,
            decision,
            reason,
            null);

    private OptimisationReport Finish(TimeSpan now, bool completed, string? error, string? note = null)
    {
        phase = Phase.Finished;
        journal.Finished();
        foreach (var remaining in candidates)
        {
            notTouched.Add($"Not tested: {remaining.Title}.");
        }

        if (note is not null)
        {
            notTouched.Insert(0, note);
        }

        if (lastChangeWaitsForRestart)
        {
            notTouched.Add("The last change was put back in the settings file; it takes effect the next time the game starts.");
        }

        return new OptimisationReport
        {
            Goal = goal,
            TargetFps = targetFps,
            TargetReason = targetFps > 0 ? Text($"Your display runs at {targetFps:0} Hz.") : UnlimitedTargetReason,
            Baseline = baseline,
            Final = reference,
            Noise = new NoiseFloor(noiseFloorPercent / PercentScale, noiseFloorPercent / PercentScale, true),
            Experiments = experiments,
            NotTouched = notTouched,
            PacingNote = "Frame-rate cap left as it is; " + gameName + " applies its settings at start-up, and GAPO tests one change per restart.",
            DiagnosisBefore = diagnosisBefore,
            DiagnosisAfter = diagnosisAfter,
            OriginalSettings = new GameSettings(originalValues, 0),
            FinalSettings = new GameSettings(values, 0),
            Completed = completed && error is null,
            Error = error,
            MeasurementWindows = sessions,
            Duration = now - startedAt,
        };
    }

    private static string Text(FormattableString text) => text.ToString(CultureInfo.InvariantCulture);
}
