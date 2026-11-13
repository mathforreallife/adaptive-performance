using Gapo.Core.Diagnosis;
using Gapo.Core.Hardware;
using Gapo.Core.Measurement;
using Gapo.Core.Optimisation;

namespace Gapo.Core.Tests;

public sealed class SimulatedGame : IGameSession, IGameKnowledge, IChangeJournal
{
    public const string Threads = "Threads";
    public const string AntiAliasing = "AntiAliasing";
    public const string Shadows = "Shadows";
    public const string Placebo = "Placebo";
    public const string Autosave = "AutosaveSeconds";
    public const string Cap = "FrameRateCap";

    private const int FramesPerWindow = 120;
    private readonly Random random = new(12345);
    private double clockMs;

    public SimulatedGame()
    {
        Settings = new GameSettings(
            new Dictionary<string, string>
            {
                [Threads] = "1",
                [AntiAliasing] = "SSAA",
                [Shadows] = "Ultra",
                [Placebo] = "On",
                [Autosave] = "10",
                [Cap] = "0",
            },
            revision: 0);
    }

    public GameSettings Settings { get; private set; }

    public double SimulationMs { get; set; } = 6;

    public double RenderMs { get; set; } = 24;

    public double NoiseFraction { get; set; } = 0.004;

    public double CapJitterMs { get; set; }

    public double WorkNeverGoesBelowMs { get; set; }

    public double ThreadsPredictionFactor { get; set; } = 1.0;

    public bool OffersOnlyThreadsAndAntiAliasing { get; set; }

    public int WindowsMeasured { get; private set; }

    public int FailMeasurementNumber { get; set; } = -1;

    public int GameDisappearsAtMeasurement { get; set; } = -1;

    public int DisturbedBaselineWindows { get; set; }

    public int SlowsDownFromMeasurement { get; set; } = int.MaxValue;

    public double SlowDownFactor { get; set; } = 1;

    public int OptimisedSettingsHurtFromMeasurement { get; set; } = int.MaxValue;

    public bool InBenchmark { get; private set; }

    public bool WaitedForWarmup { get; private set; }

    public List<string> AppliedNotes { get; } = [];

    public List<string> JournalEvents { get; } = [];

    public double WorkMs(GameSettings settings)
    {
        var render = RenderMs
            * (settings.Is(AntiAliasing, "SSAA") ? 1.0 : 0.5)
            * (settings.Is(Shadows, "Ultra") ? 1.0 : settings.Is(Shadows, "High") ? 0.8 : 0.75)
            / (settings.Number(Threads) >= 2 ? 1.7 : 1.0);
        return SimulationMs + render;
    }

    public GameSettings ReadSettings() => Settings;

    public GameSettings Apply(GameSettings settings, string note)
    {
        AppliedNotes.Add(note);
        Settings = settings.WithRevision(Settings.Revision + 1);
        return Settings;
    }

    public void WaitUntilWarm() => WaitedForWarmup = true;

    public void EnterBenchmark() => InBenchmark = true;

    public void LeaveBenchmark() => InBenchmark = false;

    public WindowResult Measure(int settingsRevision)
    {
        WindowsMeasured++;
        if (WindowsMeasured == GameDisappearsAtMeasurement)
        {
            throw new GameSessionException("The game was closed.", gameIsGone: true);
        }

        if (WindowsMeasured == FailMeasurementNumber)
        {
            throw new GameSessionException("Too few frames were measured (3).", gameIsGone: false);
        }

        var disturbance = WindowsMeasured <= DisturbedBaselineWindows && WindowsMeasured % 2 == 1 ? 1.25 : 1.0;
        disturbance *= WindowsMeasured >= SlowsDownFromMeasurement ? SlowDownFactor : 1.0;
        disturbance *= WindowsMeasured >= OptimisedSettingsHurtFromMeasurement && !Settings.Is(AntiAliasing, "SSAA") ? 3.0 : 1.0;
        var work = Math.Max(WorkNeverGoesBelowMs, WorkMs(Settings) * disturbance);
        var cap = Settings.Number(Cap);
        var frames = new MeasuredFrame[FramesPerWindow];
        for (var index = 0; index < frames.Length; index++)
        {
            var busy = work * (1 + ((random.NextDouble() - 0.5) * 2 * NoiseFraction));
            var frame = cap > 0 ? Math.Max(busy, (1000.0 / cap) + ((random.NextDouble() - 0.5) * 2 * CapJitterMs)) : busy;
            clockMs += frame;
            frames[index] = new MeasuredFrame(clockMs, frame, busy, settingsRevision, FrameMarks.None);
        }

        var render = work - SimulationMs;
        var stages = new StageTimes(SimulationMs, render, 0.5, 0, Settings.Number(Threads), 500, SimulationMs * 0.5,
            [new RenderPass("shadows", render * 0.4), new RenderPass("terrain", render * 0.6)]);
        return new WindowResult(FrameStatistics.From(frames), stages, frames);
    }

    public FrameStatistics ObserveHitches(TimeSpan trailingSpan)
    {
        var saving = Settings.Number(Autosave) is > 0 and < 120;
        return FrameStatistics.Empty with
        {
            Count = 300,
            DurationMs = Math.Max(1000, trailingSpan.TotalMilliseconds),
            StorageHitches = saving ? 2 : 0,
            LongestStorageHitchMs = saving ? 80 : 0,
        };
    }

    public MonitorSummary Monitor() => new()
    {
        LoadPerProcessor = [55, 50, 20, 15],
        AverageLoadPercent = 35,
        BusiestProcessorPercent = 55,
        GameProcessorsUsed = 1,
        BusiestGameThreadPercent = 97,
        BackgroundPercent = 4,
        MemoryLoadPercent = 40,
        LowestAvailableMemoryBytes = 8L * 1024 * 1024 * 1024,
    };

    public IReadOnlyList<SettingCandidate> Candidates(GameSettings current, WindowResult reference, CpuFacts processor, bool memoryIsUnderPressure)
    {
        var render = reference.Stages?.RenderMs ?? 0;
        var candidates = new List<SettingCandidate>();
        if (current.Number(Threads) < 2)
        {
            candidates.Add(Step("Render threads", "1", "2", 0, render * 0.4 * ThreadsPredictionFactor, Threads, "2"));
        }

        if (current.Is(AntiAliasing, "SSAA"))
        {
            candidates.Add(Step("Anti-aliasing", "SSAA", "FXAA", 2, render * 0.5, AntiAliasing, "FXAA"));
        }

        if (OffersOnlyThreadsAndAntiAliasing)
        {
            return candidates;
        }

        if (current.Is(Shadows, "Ultra"))
        {
            candidates.Add(Step("Shadows", "Ultra", "High", 1, render * 0.2, Shadows, "High"));
        }
        else if (current.Is(Shadows, "High"))
        {
            candidates.Add(Step("Shadows", "High", "Off", 5, render * 0.06, Shadows, "Off"));
        }

        if (current.Is(Placebo, "On"))
        {
            candidates.Add(Step("Placebo effect", "On", "Off", 1, 3.0, Placebo, "Off"));
        }

        return candidates;
    }

    public SettingCandidate? HitchFix(GameSettings current, FrameStatistics hitches) =>
        hitches.StorageHitches > 0 && current.Number(Autosave) is > 0 and < 120
            ? new SettingCandidate("Autosave interval", current[Autosave] + " s", "5 min", ChangeKind.HitchFix, 0, 0, "Saving blocks the game.", Change(Autosave, "300"))
            : null;

    public SettingCandidate FrameCap(GameSettings current, int capFps, string why) =>
        new(OptimisationEngine.FrameCapSetting, current.Number(Cap) == 0 ? "Unlimited" : current[Cap], capFps + " FPS", ChangeKind.Pacing, 0, 0, why, Change(Cap, capFps.ToString(System.Globalization.CultureInfo.InvariantCulture)));

    public IReadOnlyList<string> NotWorthTouching(GameSettings current, WindowResult reference, bool memoryIsUnderPressure) =>
        memoryIsUnderPressure ? [] : ["Texture quality: memory is not under pressure."];

    public void Begin(GameSettings original) => JournalEvents.Add("begin");

    public void TrialStarted(GameSettings lastCommitted, SettingCandidate candidate) => JournalEvents.Add("trial " + candidate.Setting);

    public void TrialEnded(GameSettings committed) => JournalEvents.Add("ended");

    public void Finished() => JournalEvents.Add("finished");

    private static SettingCandidate Step(string setting, string from, string to, double visualLoss, double predictedMs, string key, string value) =>
        new(setting, from, to, ChangeKind.Throughput, visualLoss, predictedMs, setting + " costs frame time.", Change(key, value));

    private static IReadOnlyDictionary<string, string> Change(string key, string value) => new Dictionary<string, string> { [key] = value };
}
