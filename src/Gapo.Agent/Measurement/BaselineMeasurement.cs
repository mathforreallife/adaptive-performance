using Gapo.Agent.EngineHook;
using Gapo.Core.Measurement;
using Gapo.Sandbox.Contract;

namespace Gapo.Agent.Measurement;

public sealed class BaselineMeasurement
{
    public const int MaximumAttempts = 3;

    private static readonly TimeSpan CommandTimeout = TimeSpan.FromSeconds(2);
    private const int EnterBenchmark = 1;
    private const int LeaveBenchmark = 0;
    private const int WindowsPerAttempt = 2;

    private readonly MeasurementWindowOptions windowOptions = new();
    private readonly WarmupDetector warmup = new();
    private readonly List<FrameStatistics> windows = new(WindowsPerAttempt);
    private Phase phase = Phase.WaitingForGame;
    private MeasurementWindow? window;
    private TimeSpan phaseStartedAt;
    private TimeSpan windowStartedAt;
    private double windowStartGameTimeMs;
    private double latestGameTimeMs;
    private bool anyWindowCutShort;

    private enum Phase
    {
        WaitingForGame,
        WaitingForWarmup,
        EnteringBenchmark,
        RestartingScene,
        Collecting,
        LeavingBenchmark,
        Finished,
    }

    public bool IsInsideWindow => phase == Phase.Collecting;

    public bool IsFinished => phase == Phase.Finished;

    public BaselineReport? Result { get; private set; }

    public TimeSpan WaitedForWarmup { get; private set; }

    public bool WarmupWaitTimedOut { get; private set; }

    public int Attempts { get; private set; }

    public void OnFrame(in FrameRecord record)
    {
        var frame = new MeasuredFrame(record.TimeMs, record.FrameMs, record.WorkMs, record.Revision, MarksOf(record.FrameFlags));
        latestGameTimeMs = frame.EndTimeMs;
        if (phase == Phase.WaitingForWarmup)
        {
            warmup.Offer(frame);
        }
        else if (phase == Phase.Collecting)
        {
            window!.Offer(frame);
        }
    }

    public void Advance(SandboxLink link, TimeSpan now)
    {
        if (phase != Phase.WaitingForGame && phase != Phase.Finished && !link.IsConnected)
        {
            Fail("The game closed before the measurement finished.");
            return;
        }

        switch (phase)
        {
            case Phase.WaitingForGame when link.IsConnected:
                Enter(Phase.WaitingForWarmup, now);
                break;
            case Phase.WaitingForWarmup:
                AdvanceWarmupWait(link, now);
                break;
            case Phase.EnteringBenchmark when CommandSettled(link, now, "The game did not respond to the benchmark command."):
                RestartScene(link, now);
                break;
            case Phase.RestartingScene when CommandSettled(link, now, "The game stopped responding."):
                window = new MeasurementWindow(windowOptions, link.AppliedSettingsRevision, latestGameTimeMs);
                windowStartedAt = now;
                windowStartGameTimeMs = latestGameTimeMs;
                Enter(Phase.Collecting, now);
                break;
            case Phase.Collecting:
                AdvanceWindow(link, now);
                break;
            case Phase.LeavingBenchmark when link.LastCommandWasAcknowledged || now - phaseStartedAt > CommandTimeout:
                phase = Phase.Finished;
                break;
        }
    }

    private void AdvanceWarmupWait(SandboxLink link, TimeSpan now)
    {
        var waited = now - phaseStartedAt;
        var timedOut = waited >= WarmupDetector.LongestWait;
        if ((link.GameIsRunningNormally && warmup.IsWarm) || timedOut)
        {
            WaitedForWarmup = waited;
            WarmupWaitTimedOut = timedOut;
            link.TrySendCommand(SandboxCommand.BenchmarkCamera, EnterBenchmark);
            Enter(Phase.EnteringBenchmark, now);
        }
    }

    private void AdvanceWindow(SandboxLink link, TimeSpan now)
    {
        window!.Advance(windowStartGameTimeMs + (now - windowStartedAt).TotalMilliseconds);
        if (window.State == MeasurementWindowState.Collecting)
        {
            return;
        }

        if (window.State == MeasurementWindowState.Failed)
        {
            Fail(window.FailureReason);
            link.TrySendCommand(SandboxCommand.BenchmarkCamera, LeaveBenchmark);
            return;
        }

        anyWindowCutShort |= window.WasCutShort;
        windows.Add(window.Summarise());
        if (windows.Count < WindowsPerAttempt)
        {
            RestartScene(link, now);
            return;
        }

        Attempts++;
        var noise = NoiseFloor.FromRepeatedMeasurement(windows[0].MedianBusyMs, windows[1].MedianBusyMs);
        if (!noise.CanBeTrusted && Attempts < MaximumAttempts)
        {
            windows.Clear();
            RestartScene(link, now);
            return;
        }

        Result = new BaselineReport(windows[0], windows[1], noise, Attempts, anyWindowCutShort, FailureReason: null);
        link.TrySendCommand(SandboxCommand.BenchmarkCamera, LeaveBenchmark);
        Enter(Phase.LeavingBenchmark, now);
    }

    private void RestartScene(SandboxLink link, TimeSpan now)
    {
        link.TrySendCommand(SandboxCommand.RestartBenchmarkPath, 0);
        Enter(Phase.RestartingScene, now);
    }

    private bool CommandSettled(SandboxLink link, TimeSpan now, string failureWhenIgnored)
    {
        if (link.LastCommandWasAcknowledged)
        {
            return true;
        }

        if (now - phaseStartedAt > CommandTimeout)
        {
            Fail(failureWhenIgnored);
        }

        return false;
    }

    private void Enter(Phase next, TimeSpan now)
    {
        phase = next;
        phaseStartedAt = now;
    }

    private void Fail(string reason)
    {
        Result = new BaselineReport(FrameStatistics.Empty, FrameStatistics.Empty, NoiseFloor.FromRepeatedMeasurement(0, 0), Attempts, anyWindowCutShort, reason);
        phase = Phase.Finished;
    }

    private static FrameMarks MarksOf(FrameFlags flags) =>
        ((flags & FrameFlags.Warmup) != 0 ? FrameMarks.Warmup : FrameMarks.None)
        | ((flags & FrameFlags.Autosave) != 0 ? FrameMarks.StorageHitch : FrameMarks.None);
}

public sealed record BaselineReport(
    FrameStatistics FirstWindow,
    FrameStatistics SecondWindow,
    NoiseFloor Noise,
    int Attempts,
    bool AnyWindowCutShort,
    string? FailureReason)
{
    public bool Succeeded => FailureReason is null;
}
