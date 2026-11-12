using System.Diagnostics;
using Gapo.Adapters.Sandbox;
using Gapo.Agent.EngineHook;
using Gapo.Agent.Monitoring;
using Gapo.Core.Diagnosis;
using Gapo.Core.Measurement;
using Gapo.Core.Optimisation;
using Gapo.Sandbox.Contract;

namespace Gapo.Agent.Optimisation;

public sealed class SandboxSession : IGameSession
{
    private const int EnterBenchmarkArgument = 1;
    private const int LeaveBenchmarkArgument = 0;
    private const int MostObservedFrames = 24_000;
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(90);
    private static readonly TimeSpan CommandTimeout = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan SettingsTimeout = TimeSpan.FromSeconds(4);
    private static readonly TimeSpan ObservationGrace = TimeSpan.FromSeconds(5);

    private readonly SandboxLink link;
    private readonly AgentMonitor monitor;
    private readonly Func<bool> pump;
    private readonly MeasurementWindowOptions windowOptions = new();
    private readonly WarmupDetector warmup = new();
    private readonly List<MeasuredFrame> observed = new(4096);
    private MeasurementWindow? window;
    private StageAccumulator? stages;
    private double latestGameTimeMs;
    private bool hasBeenConnected;

    public SandboxSession(SandboxLink link, AgentMonitor monitor, Func<bool> pump)
    {
        this.link = link;
        this.monitor = monitor;
        this.pump = pump;
    }

    public bool IsInsideWindow => window is { State: MeasurementWindowState.Collecting };

    public void OnFrame(in FrameRecord record)
    {
        var marks = ((record.FrameFlags & FrameFlags.Warmup) != 0 ? FrameMarks.Warmup : FrameMarks.None)
            | ((record.FrameFlags & FrameFlags.Autosave) != 0 ? FrameMarks.StorageHitch : FrameMarks.None);
        var frame = new MeasuredFrame(record.TimeMs, record.FrameMs, record.WorkMs, record.Revision, marks);
        latestGameTimeMs = frame.EndTimeMs;
        warmup.Offer(frame);
        if (!frame.IsWarmup && observed.Count < MostObservedFrames)
        {
            observed.Add(frame);
        }

        if (window is { State: MeasurementWindowState.Collecting } collecting)
        {
            var accepted = collecting.Frames.Count;
            collecting.Offer(frame);
            if (collecting.Frames.Count > accepted)
            {
                stages!.Add(record);
            }
        }
    }

    public GameSettings ReadSettings()
    {
        WaitFor(() => link.IsConnected, ConnectTimeout, "The game was not found.");
        hasBeenConnected = true;
        var settings = SandboxSettings.Load(link.SettingsPath) ?? throw new GameSessionException("Could not read the game's settings file.", gameIsGone: false);
        return SandboxSettingsMap.ToGameSettings(settings, Math.Max(settings.Revision, link.AppliedSettingsRevision));
    }

    public GameSettings Apply(GameSettings settings, string note)
    {
        var onDisk = SandboxSettings.Load(link.SettingsPath);
        var revision = Math.Max(onDisk?.Revision ?? 0, link.AppliedSettingsRevision) + 1;
        var applied = settings.WithRevision(revision);
        try
        {
            SandboxSettingsMap.ToSandboxSettings(applied, $"GAPO ({note})").Save(link.SettingsPath);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new GameSessionException("The game's settings file could not be written: " + exception.Message, gameIsGone: false);
        }

        WaitFor(() => link.AppliedSettingsRevision == revision, SettingsTimeout, "The game did not apply the new settings.");
        return applied;
    }

    public void WaitUntilWarm()
    {
        var clock = Stopwatch.StartNew();
        while (!(link.GameIsRunningNormally && warmup.IsWarm) && clock.Elapsed < WarmupDetector.LongestWait)
        {
            PumpOrFail();
        }
    }

    public void EnterBenchmark() => SendCommand(SandboxCommand.BenchmarkCamera, EnterBenchmarkArgument, "The game did not respond to the benchmark command.");

    public void LeaveBenchmark() => SendCommand(SandboxCommand.BenchmarkCamera, LeaveBenchmarkArgument, "The game did not respond to the benchmark command.");

    public WindowResult Measure(int settingsRevision)
    {
        SendCommand(SandboxCommand.RestartBenchmarkPath, 0, "The game stopped responding.");
        var started = Stopwatch.StartNew();
        var startGameTimeMs = latestGameTimeMs;
        stages = new StageAccumulator();
        window = new MeasurementWindow(windowOptions, settingsRevision, startGameTimeMs);
        try
        {
            while (window.State == MeasurementWindowState.Collecting)
            {
                PumpOrFail();
                window.Advance(startGameTimeMs + started.Elapsed.TotalMilliseconds);
            }

            if (window.State == MeasurementWindowState.Failed)
            {
                throw new GameSessionException(window.FailureReason, gameIsGone: false);
            }

            return new WindowResult(window.Summarise(), stages.ToStageTimes(), window.Frames.ToArray());
        }
        finally
        {
            window = null;
        }
    }

    public FrameStatistics ObserveHitches(TimeSpan trailingSpan)
    {
        var clock = Stopwatch.StartNew();
        while (ObservedSpanMs() < trailingSpan.TotalMilliseconds && clock.Elapsed < trailingSpan + ObservationGrace)
        {
            PumpOrFail();
        }

        var from = latestGameTimeMs - trailingSpan.TotalMilliseconds;
        return FrameStatistics.From(observed.Where(frame => frame.EndTimeMs >= from).ToArray());
    }

    public MonitorSummary Monitor() => monitor.SummariseForDiagnosis();

    private double ObservedSpanMs() => observed.Count < 2 ? 0 : observed[^1].EndTimeMs - observed[0].EndTimeMs;

    private void SendCommand(SandboxCommand command, int argument, string failureWhenIgnored)
    {
        if (!link.TrySendCommand(command, argument))
        {
            throw new GameSessionException("The game was closed.", gameIsGone: true);
        }

        WaitFor(() => link.LastCommandWasAcknowledged, CommandTimeout, failureWhenIgnored);
    }

    private void WaitFor(Func<bool> condition, TimeSpan timeout, string failure)
    {
        var clock = Stopwatch.StartNew();
        while (!condition())
        {
            if (clock.Elapsed > timeout)
            {
                throw new GameSessionException(failure, gameIsGone: hasBeenConnected && !link.IsConnected);
            }

            PumpOrFail();
        }
    }

    private void PumpOrFail()
    {
        if (!pump())
        {
            throw new GameSessionException("The optimisation was stopped.", gameIsGone: false);
        }

        if (hasBeenConnected && !link.IsConnected)
        {
            throw new GameSessionException("The game was closed.", gameIsGone: true);
        }
    }
}
