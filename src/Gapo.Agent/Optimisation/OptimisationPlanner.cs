using System.Globalization;
using Gapo.Adapters.Sandbox;
using Gapo.Core.Diagnosis;
using Gapo.Core.Games.Unreal;
using Gapo.Core.Optimisation;
using Gapo.Core.Protocol;

namespace Gapo.Agent.Optimisation;

public static class OptimisationPlanner
{
    private const int MostTestsNamed = 5;

    public static PlanMessage SwitchedOff(string gameName) =>
        new(gameName, false, "The optimiser is switched off in GAPO's settings, so GAPO only measures this game and changes nothing. Switch it on under Settings → Optimisation to optimise.", [], 0, 0, 0);

    public static PlanMessage NotPossible(string gameName, string reason) => new(gameName, false, reason, [], 0, 0, 0);

    public static PlanMessage ForLiveGame(string gameName, IReadOnlyList<SettingState> settings, Goal goal, double fps, AgentPreferencesCommand preferences)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(preferences);
        var free = settings
            .Where(setting => !setting.Locked && setting.Choices.Count > 1)
            .Where(setting => preferences.AllowFrameCap || setting.Key != SandboxSettingsMap.FrameRateCap)
            .ToList();
        var locked = settings.Where(setting => setting.Locked).Select(setting => setting.Label).ToList();
        if (free.Count == 0)
        {
            return NotPossible(gameName, "Every setting GAPO could test is locked. Unlock at least one under Game settings to optimise.");
        }

        var time = OptimisationEstimate.ForLiveChanges(fps, free.Count, gameHasSettled: fps > 0, preferences.AllowFrameCap);
        var steps = new List<string>
        {
            "Wait until the game runs steadily, then measure your current settings twice, a few seconds each.",
            Text($"Change one setting at a time while you play and measure again. Up to {free.Count} settings may be tested, most promising first: {Names(free.Select(setting => setting.Label))}."),
            "Keep a change only if it helps by more than the measuring noise; put every other change straight back.",
            "Measure the final result once more and show the frame rate before and after.",
        };
        if (preferences.AskBeforeEachChange)
        {
            steps.Insert(1, "Ask you before each change. Nothing changes until you press Try it.");
        }

        AddLimits(steps, locked, preferences.AllowFrameCap);
        var summary = Text($"{GoalName(goal)}. {gameName} applies its settings while it runs, so no restart is needed. Estimated time: {time.Describe()}; it usually ends early once the target is met. Stop ends it at any time and Revert all puts every setting back.");
        return new PlanMessage(gameName, true, summary, steps, Seconds(time.Low), Seconds(time.High), 0);
    }

    public static PlanMessage ForRestartGame(string gameName, IReadOnlyDictionary<string, string> values, Bottleneck? knownBottleneck, Goal goal, IReadOnlySet<string> lockedKeys, AgentPreferencesCommand preferences)
    {
        ArgumentNullException.ThrowIfNull(lockedKeys);
        ArgumentNullException.ThrowIfNull(preferences);
        var all = UnrealKnowledge.Candidates(values, knownBottleneck ?? Bottleneck.GraphicsCard);
        var locked = all.Where(candidate => lockedKeys.Contains(candidate.Setting.Key)).Select(candidate => candidate.Setting.Label).ToList();
        var testable = all
            .Where(candidate => !lockedKeys.Contains(candidate.Setting.Key))
            .Where(candidate => goal != Goal.Quality || candidate.VisualLoss <= RestartOptimiser.QualityGoalLargestVisualLoss)
            .ToList();
        if (testable.Count == 0)
        {
            return NotPossible(gameName, locked.Count > 0
                ? "Every setting GAPO would test for this game is locked. Unlock at least one under Game settings to optimise."
                : "Every setting GAPO knows for this game is already at its lowest useful level, so there is nothing to test.");
        }

        var mostRestarts = preferences.MostRestarts > 0 ? preferences.MostRestarts : RestartOptimiser.MostRestartsPerRun;
        var tests = Math.Min(testable.Count, mostRestarts);
        var time = OptimisationEstimate.ForRestarts(testable.Count, mostRestarts);
        var observe = ObservedSeconds();
        var steps = new List<string>
        {
            Text($"Watch {observe} s of play on your current settings once the game has settled. Keep playing as usual."),
            Text($"Change one setting in the settings file and ask you to close and start the game again; GAPO then watches {observe} s of play. Up to {tests} restarts, in this order: {Names(testable.Take(tests).Select(candidate => candidate.Title))}."),
            "Keep a change only if each frame takes measurably less work; put the others back in the settings file.",
            "Stop early once the frame-rate target is met, or after three changes in a row that make no difference.",
        };
        if (knownBottleneck is null)
        {
            steps.Insert(1, "Find out from the first measurement what limits the game; the settings to test may change with it.");
        }

        if (preferences.AskBeforeEachChange)
        {
            steps.Insert(1, "Ask you before each change. Nothing changes until you press Try it.");
        }

        AddLimits(steps, locked, allowFrameCap: true);
        var summary = Text($"{GoalName(goal)}. {gameName} applies these settings only when it starts, so GAPO needs you to restart it up to {tests} {(tests == 1 ? "time" : "times")}. Estimated time: {time.Describe()}, most of it spent restarting. Stop ends it at any time and Revert all puts every setting back.");
        return new PlanMessage(gameName, true, summary, steps, Seconds(time.Low), Seconds(time.High), tests);
    }

    private static void AddLimits(List<string> steps, List<string> locked, bool allowFrameCap)
    {
        if (locked.Count > 0)
        {
            steps.Add("Never touch the settings you locked: " + string.Join(", ", locked) + ".");
        }

        if (!allowFrameCap)
        {
            steps.Add("Never cap the frame rate; you turned caps off in Settings.");
        }
    }

    private static string Names(IEnumerable<string> names)
    {
        var list = names.ToList();
        return list.Count <= MostTestsNamed
            ? string.Join(", ", list)
            : string.Join(", ", list.Take(MostTestsNamed)) + Text($" and {list.Count - MostTestsNamed} more");
    }

    private static string GoalName(Goal goal) => goal switch
    {
        Goal.MaximumFps => "Goal: maximum FPS",
        Goal.Quality => "Goal: quality",
        Goal.Smoothness => "Goal: smoothness",
        _ => "Goal: balanced",
    };

    private static int ObservedSeconds() => (int)Measurement.ObservedGameDiagnosis.ObservationLength.TotalSeconds;

    private static int Seconds(TimeSpan time) => (int)Math.Round(time.TotalSeconds);

    private static string Text(FormattableString text) => text.ToString(CultureInfo.InvariantCulture);
}
